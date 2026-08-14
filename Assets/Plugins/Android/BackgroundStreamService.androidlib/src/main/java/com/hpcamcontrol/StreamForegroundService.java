package com.hpcamcontrol;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

public class StreamForegroundService extends Service {

    private static final String CHANNEL_ID = "hpcam_stream_channel";
    private static final int NOTIFICATION_ID = 1001;

    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;

    public static void start(Context context) {
        Intent intent = new Intent(context, StreamForegroundService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(intent);
        } else {
            context.startService(intent);
        }
    }

    public static void stop(Context context) {
        context.stopService(new Intent(context, StreamForegroundService.class));
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onCreate() {
        super.onCreate();

        PowerManager powerManager = (PowerManager) getSystemService(Context.POWER_SERVICE);
        if (powerManager != null) {
            wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "HpCamControl:StreamWakeLock");
            wakeLock.acquire();
        }

        // Without this, Wi-Fi can drop into power-save mode shortly after the
        // screen turns off / app backgrounds, causing brief connectivity drops
        // that kill the WebSocket connection mid-stream.
        WifiManager wifiManager = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        if (wifiManager != null) {
            wifiLock = wifiManager.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "HpCamControl:StreamWifiLock");
            wifiLock.acquire();
        }

        Notification notification = buildNotification();
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
        } catch (Exception e) {
            // Android 14+ can deny a foreground-service start if the app's process
            // importance dropped below "foreground" between the request and this
            // call (e.g. a slow cold boot). Crashing the whole app over this is
            // worse than just not streaming in the background for this session -
            // the poll loop in BackgroundStreamServiceBridge.cs is unaffected.
            Log.e("StreamForegroundService", "startForeground() denied, continuing without it", e);
            stopSelf();
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        // START_NOT_STICKY: if the OS kills this service (e.g. the user swipes the app
        // away), it must stay dead. FCM push-to-wake is the intended way to bring the
        // app back, not an automatic service restart racing against that.
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
        }
        if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
        }
        super.onDestroy();
    }

    private Notification buildNotification() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (manager != null && manager.getNotificationChannel(CHANNEL_ID) == null) {
                NotificationChannel channel = new NotificationChannel(
                        CHANNEL_ID, "HpCamControl Streaming", NotificationManager.IMPORTANCE_LOW);
                channel.setDescription("Keeps camera streaming active in the background");
                manager.createNotificationChannel(channel);
            }

            return new Notification.Builder(this, CHANNEL_ID)
                    .setContentTitle("HpCamControl")
                    .setContentText("스트리밍 중")
                    .setSmallIcon(android.R.drawable.ic_menu_camera)
                    .setOngoing(true)
                    .build();
        } else {
            @SuppressWarnings("deprecation")
            Notification notification = new Notification.Builder(this)
                    .setContentTitle("HpCamControl")
                    .setContentText("스트리밍 중")
                    .setSmallIcon(android.R.drawable.ic_menu_camera)
                    .setOngoing(true)
                    .build();
            return notification;
        }
    }
}
