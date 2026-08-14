package com.hpcamcontrol;

import android.content.Context;
import android.graphics.ImageFormat;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.util.Size;
import android.view.Surface;
import android.view.WindowManager;

import java.nio.ByteBuffer;
import java.util.concurrent.atomic.AtomicInteger;

public class NativeCameraCapture {

    private static final String TAG = "NativeCameraCapture";
    // Kept small deliberately: this feeds a single unchunked WebSocket message
    // (FMETP's own encoder chunks into ~1.4KB pieces; sending large unchunked
    // JPEGs at high frequency was destabilizing/dropping the connection).
    private static final int TARGET_WIDTH = 480;
    private static final int TARGET_HEIGHT = 270;
    private static final byte JPEG_QUALITY = 45;
    private static final int OPEN_RETRY_ATTEMPTS = 6;
    private static final long OPEN_RETRY_DELAY_MS = 200;

    private static HandlerThread backgroundThread;
    private static Handler backgroundHandler;

    private static CameraDevice cameraDevice;
    private static CameraCaptureSession captureSession;
    private static ImageReader imageReader;
    private static String cameraId;

    private static final Object frameLock = new Object();
    private static byte[] latestFrame;
    private static final AtomicInteger frameId = new AtomicInteger(0);

    private static volatile boolean running = false;
    private static volatile boolean useFrontCamera = false;

    // Guards start()/stop()/switchCamera() against each other. Without this, a
    // stop() arriving on the main thread milliseconds after start() (observed in
    // practice right after the Telecom wake-up sequence launches the app, which
    // triggers a rapid pause/resume blip during cold start) can call
    // backgroundThread.quitSafely()+join() while openCameraWithRetry() is still
    // synchronously inside cameraManager.openCamera() on that same thread. Android's
    // camera framework captures backgroundHandler for a later async callback as
    // part of that call, and if the thread's Looper is already dead by the time
    // that callback fires, it crashes with "sending message to a Handler on a dead
    // thread" - and the camera never actually opens. Serializing start/stop closes
    // the most common instance of this race (immediate stop-after-start).
    private static final Object lifecycleLock = new Object();

    public static void start(Context context) {
        synchronized (lifecycleLock) {
            Log.i(TAG, "start() called, running=" + running);
            if (running) return;
            running = true;

            backgroundThread = new HandlerThread("NativeCameraCaptureThread");
            backgroundThread.start();
            backgroundHandler = new Handler(backgroundThread.getLooper());

            backgroundHandler.post(() -> openCameraWithRetry(context, OPEN_RETRY_ATTEMPTS));
        }
    }

    // Flips between back/front lens. Safe to call whether or not capture is
    // currently running - if it's running, the camera is closed and reopened
    // with the new facing; the flag alone is enough if capture hasn't started yet.
    public static void switchCamera(Context context) {
        synchronized (lifecycleLock) {
            useFrontCamera = !useFrontCamera;
            Log.i(TAG, "switchCamera() -> useFrontCamera=" + useFrontCamera);
            if (!running || backgroundHandler == null) return;

            backgroundHandler.post(() -> {
                closeCameraInternal();
                openCameraWithRetry(context, OPEN_RETRY_ATTEMPTS);
            });
        }
    }

    public static void stop(Context context) {
        synchronized (lifecycleLock) {
            if (!running) return;
            running = false;

            HandlerThread threadToStop = backgroundThread;
            Handler handlerToStop = backgroundHandler;
            backgroundThread = null;
            backgroundHandler = null;

            if (handlerToStop != null) {
                // Post the close+quit onto the capture thread itself instead of
                // tearing it down from the caller's thread, so it runs strictly
                // after anything openCameraWithRetry() already queued there.
                handlerToStop.post(() -> {
                    closeCameraInternal();
                    threadToStop.quitSafely();
                });
            } else if (threadToStop != null) {
                threadToStop.quitSafely();
            }
        }
    }

    public static int getLatestFrameId() {
        return frameId.get();
    }

    public static byte[] getLatestFrameBytes() {
        synchronized (frameLock) {
            return latestFrame;
        }
    }

    private static void openCameraWithRetry(Context context, int attemptsLeft) {
        if (!running) return;

        CameraManager cameraManager = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
        if (cameraManager == null) {
            Log.e(TAG, "CameraManager unavailable");
            return;
        }

        try {
            int desiredFacing = useFrontCamera
                    ? CameraCharacteristics.LENS_FACING_FRONT
                    : CameraCharacteristics.LENS_FACING_BACK;
            cameraId = pickCameraId(cameraManager, desiredFacing);
            if (cameraId == null) {
                Log.w(TAG, "No camera found facing " + desiredFacing + ", falling back to back camera");
                cameraId = pickCameraId(cameraManager, CameraCharacteristics.LENS_FACING_BACK);
            }
            if (cameraId == null) {
                Log.e(TAG, "No usable camera found");
                return;
            }

            CameraCharacteristics characteristics = cameraManager.getCameraCharacteristics(cameraId);
            Size captureSize = pickJpegSize(characteristics);
            Integer sensorOrientation = characteristics.get(CameraCharacteristics.SENSOR_ORIENTATION);

            imageReader = ImageReader.newInstance(captureSize.getWidth(), captureSize.getHeight(), ImageFormat.JPEG, 2);
            imageReader.setOnImageAvailableListener(reader -> {
                Image image = null;
                try {
                    image = reader.acquireLatestImage();
                    if (image == null) return;

                    ByteBuffer buffer = image.getPlanes()[0].getBuffer();
                    byte[] bytes = new byte[buffer.remaining()];
                    buffer.get(bytes);

                    synchronized (frameLock) {
                        latestFrame = bytes;
                    }
                    int newId = frameId.incrementAndGet();
                    if (newId % 10 == 1) {
                        Log.i(TAG, "Captured frame #" + newId + " (" + bytes.length + " bytes)");
                    }
                } catch (Exception e) {
                    Log.e(TAG, "Error reading captured frame", e);
                } finally {
                    if (image != null) image.close();
                }
            }, backgroundHandler);

            cameraManager.openCamera(cameraId, new CameraDevice.StateCallback() {
                @Override
                public void onOpened(CameraDevice device) {
                    if (!running) {
                        Log.i(TAG, "Camera opened after stop() was called - closing immediately");
                        device.close();
                        return;
                    }
                    Log.i(TAG, "Camera opened: " + cameraId + " size=" + captureSize);
                    cameraDevice = device;
                    startCaptureSession(context, sensorOrientation == null ? 0 : sensorOrientation);
                }

                @Override
                public void onDisconnected(CameraDevice device) {
                    device.close();
                    cameraDevice = null;
                }

                @Override
                public void onError(CameraDevice device, int error) {
                    device.close();
                    cameraDevice = null;
                    Log.w(TAG, "Camera open error: " + error);
                    if (running && attemptsLeft > 1) {
                        backgroundHandler.postDelayed(
                                () -> openCameraWithRetry(context, attemptsLeft - 1), OPEN_RETRY_DELAY_MS);
                    }
                }
            }, backgroundHandler);

        } catch (CameraAccessException | SecurityException e) {
            Log.w(TAG, "openCamera failed, retrying: " + e.getMessage());
            if (running && attemptsLeft > 1) {
                backgroundHandler.postDelayed(
                        () -> openCameraWithRetry(context, attemptsLeft - 1), OPEN_RETRY_DELAY_MS);
            }
        }
    }

    private static void startCaptureSession(Context context, int sensorOrientation) {
        if (cameraDevice == null || imageReader == null) return;

        try {
            Surface surface = imageReader.getSurface();
            CaptureRequest.Builder requestBuilder = cameraDevice.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
            requestBuilder.addTarget(surface);
            requestBuilder.set(CaptureRequest.JPEG_ORIENTATION, computeJpegOrientation(context, sensorOrientation));
            requestBuilder.set(CaptureRequest.JPEG_QUALITY, JPEG_QUALITY);

            cameraDevice.createCaptureSession(
                    java.util.Collections.singletonList(surface),
                    new CameraCaptureSession.StateCallback() {
                        @Override
                        public void onConfigured(CameraCaptureSession session) {
                            if (!running) {
                                Log.i(TAG, "Session configured after stop() was called - closing immediately");
                                session.close();
                                return;
                            }
                            captureSession = session;
                            try {
                                session.setRepeatingRequest(requestBuilder.build(), null, backgroundHandler);
                                Log.i(TAG, "Repeating capture request started");
                            } catch (CameraAccessException e) {
                                Log.e(TAG, "setRepeatingRequest failed", e);
                            }
                        }

                        @Override
                        public void onConfigureFailed(CameraCaptureSession session) {
                            Log.e(TAG, "Capture session configuration failed");
                        }
                    },
                    backgroundHandler);
        } catch (CameraAccessException e) {
            Log.e(TAG, "startCaptureSession failed", e);
        }
    }

    private static int computeJpegOrientation(Context context, int sensorOrientation) {
        int deviceRotationDegrees;
        try {
            WindowManager windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
            int rotation = windowManager.getDefaultDisplay().getRotation();
            switch (rotation) {
                case Surface.ROTATION_90: deviceRotationDegrees = 90; break;
                case Surface.ROTATION_180: deviceRotationDegrees = 180; break;
                case Surface.ROTATION_270: deviceRotationDegrees = 270; break;
                default: deviceRotationDegrees = 0;
            }
        } catch (Exception e) {
            deviceRotationDegrees = 0;
        }
        return (sensorOrientation - deviceRotationDegrees + 360) % 360;
    }

    private static String pickCameraId(CameraManager cameraManager, int desiredFacing) throws CameraAccessException {
        for (String id : cameraManager.getCameraIdList()) {
            CameraCharacteristics characteristics = cameraManager.getCameraCharacteristics(id);
            Integer facing = characteristics.get(CameraCharacteristics.LENS_FACING);
            if (facing != null && facing == desiredFacing) {
                return id;
            }
        }
        return null;
    }

    private static Size pickJpegSize(CameraCharacteristics characteristics) {
        StreamConfigurationMap map = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        if (map == null) return new Size(TARGET_WIDTH, TARGET_HEIGHT);

        Size[] sizes = map.getOutputSizes(ImageFormat.JPEG);
        if (sizes == null || sizes.length == 0) return new Size(TARGET_WIDTH, TARGET_HEIGHT);

        Size best = sizes[0];
        long bestDiff = Long.MAX_VALUE;
        for (Size s : sizes) {
            long diff = Math.abs((long) s.getWidth() * s.getHeight() - (long) TARGET_WIDTH * TARGET_HEIGHT);
            if (diff < bestDiff) {
                bestDiff = diff;
                best = s;
            }
        }
        return best;
    }

    private static void closeCameraInternal() {
        if (captureSession != null) {
            captureSession.close();
            captureSession = null;
        }
        if (cameraDevice != null) {
            cameraDevice.close();
            cameraDevice = null;
        }
        if (imageReader != null) {
            imageReader.close();
            imageReader = null;
        }
    }
}
