using UnityEngine;

public class BackgroundStreamServiceBridge : MonoBehaviour
{
    public FMSolution.FMWebSocket.FMWebSocketManager webSocketManager;
    public FMSolution.FMETP.WebcamManager webcamManager;
    public int frameLabel = 1001;

#if UNITY_ANDROID && !UNITY_EDITOR
    private const string LogTag = "[BGCapture] ";
    private const string ServiceClass = "com.hpcamcontrol.StreamForegroundService";
    private const string NativeCameraClass = "com.hpcamcontrol.NativeCameraCapture";
    private const int PollIntervalMs = 100;

    private const float RejoinCooldownSeconds = 3f;

    // Give FMWebSocketManager's own AutoInit join handshake time to complete
    // before this loop's rejoin logic is allowed to act at all. Without this,
    // a very early OnApplicationPause(true) - observed in practice within a few
    // hundred ms of a Telecom-wake cold start - can see wsJoinedRoom still false
    // (the join request is in flight, not failed) and fire a redundant
    // RegisterRoom() on top of it, causing the server to send OnJoinedRoom twice.
    private const float StartupGraceSeconds = 5f;

    private int lastSentFrameId = -1;
    private ushort dataIdCounter = 0;
    private volatile bool isPaused = false;

    // Time.realtimeSinceStartup throws off the main thread, so use a plain
    // .NET stopwatch for the rejoin cooldown (this loop runs on a threadpool
    // thread due to ConfigureAwait(false) below).
    private readonly System.Diagnostics.Stopwatch rejoinStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private double lastRejoinAttemptSeconds = -999;

    private void Start()
    {
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.POST_NOTIFICATIONS") == false)
        {
            UnityEngine.Android.Permission.RequestUserPermission("android.permission.POST_NOTIFICATIONS");
        }

        // StreamForegroundService is no longer auto-started here - everything is
        // button-driven now (see CamStart.cs), so the foreground-service
        // notification/wakelock should only appear while actually in a call.
        // CamStart calls StartService()/StopService() directly.

        // Loop is started once here (normal foreground context) and left running
        // for the app's lifetime - it just no-ops while !isPaused. Starting a
        // brand-new async chain from inside OnApplicationPause itself appears to
        // silently hang (suspected Unity engine transition timing issue), so we
        // avoid ever doing that.
        _ = PollLoopAsync();
    }

    private void OnApplicationPause(bool pause)
    {
        Debug.Log(LogTag + "OnApplicationPause(" + pause + ")");
        isPaused = pause;

        if (pause)
        {
            StartNativeCapture();
        }
        else
        {
            StopNativeCapture();
        }
    }

    public void StartService()
    {
        using (var activityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = activityClass.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var serviceClass = new AndroidJavaClass(ServiceClass))
        {
            serviceClass.CallStatic("start", activity);
        }
    }

    public void StopService()
    {
        using (var activityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = activityClass.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var serviceClass = new AndroidJavaClass(ServiceClass))
        {
            serviceClass.CallStatic("stop", activity);
        }
    }

    private void StartNativeCapture()
    {
        // WebcamManager (Unity's own WebCamTexture, used for foreground preview/
        // streaming) and NativeCameraCapture both open the same physical camera -
        // Camera2 only allows one owner at a time, so leaving both active
        // concurrently makes both fail ("Error configuring streams", disconnects).
        // Release it from WebcamManager first.
        if (webcamManager != null) webcamManager.Action_StopWebcam();

        using (var activityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = activityClass.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var nativeCameraClass = new AndroidJavaClass(NativeCameraClass))
        {
            nativeCameraClass.CallStatic("start", activity);
        }
        lastSentFrameId = -1;
        Debug.Log(LogTag + "Native capture start requested");
    }

    private void StopNativeCapture()
    {
        using (var activityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = activityClass.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var nativeCameraClass = new AndroidJavaClass(NativeCameraClass))
        {
            nativeCameraClass.CallStatic("stop", activity);
        }
        Debug.Log(LogTag + "Native capture stop requested");

        // Hand the camera back to WebcamManager now that NativeCameraCapture has
        // released it, so the foreground preview/stream resumes. Deliberately NOT
        // calling Action_StartWebcam() here: its `if (!isInitWaiting) ...` guard
        // can silently no-op if WebcamManager's OWN initial startup (from OnEnable,
        // e.g. right after app launch) is still mid-flight and hasn't reached
        // isInitWaiting=false yet - a real scenario, since a pause/resume can be
        // triggered this early by the POST_NOTIFICATIONS permission dialog above.
        // When that race happens, the foreground preview never gets a working
        // WebCamTexture again for the rest of the session (permanent black quad).
        // Action_useFrontCam() routes through RestartWebcamAsync(), which explicitly
        // stops first and resets stop/cancellationTokenSource_global before
        // re-initialising, so it reliably recovers regardless of what state the
        // earlier attempt was left in.
        if (webcamManager != null) webcamManager.Action_useFrontCam(webcamManager.useFrontCam);
    }

    private async System.Threading.Tasks.Task PollLoopAsync()
    {
        Debug.Log(LogTag + "Poll loop started");

        // ConfigureAwait(false) is required here: this loop is started from
        // Start() on the Unity main thread, so without it the continuation
        // after Task.Delay is posted back to UnitySynchronizationContext,
        // which only gets pumped by Unity's per-frame Update loop - and that
        // loop does not run while the app is backgrounded, so the continuation
        // would silently never fire. FMETP's own async loops avoid this only
        // because they happen to originate from background socket threads.
        while (true)
        {
            await System.Threading.Tasks.Task.Delay(PollIntervalMs).ConfigureAwait(false);

            if (!isPaused) continue;

            try
            {
                // This threadpool thread is not attached to the JVM by default;
                // AndroidJavaObject/JNI calls (and possibly Unity's own Android
                // logging bridge) can hang silently on an unattached thread.
                AndroidJNI.AttachCurrentThread();
                PollAndSendNativeFrame();
            }
            catch (System.Exception e)
            {
                Debug.LogError(LogTag + "Poll loop exception: " + e);
            }
        }
    }

    private int pollTickCount = 0;

    private void PollAndSendNativeFrame()
    {
        pollTickCount++;
        bool heartbeat = (pollTickCount % 20 == 1); // ~every 2s at 100ms interval

        if (heartbeat) Debug.Log(LogTag + "tick alive, isPaused=" + isPaused);

        if (webSocketManager == null)
        {
            if (heartbeat) Debug.Log(LogTag + "tick: webSocketManager is null");
            return;
        }

        bool joined = webSocketManager.Settings.wsJoinedRoom;
        if (heartbeat) Debug.Log(LogTag + "tick: wsJoinedRoom=" + joined + " Initialised=" + webSocketManager.Initialised);

        if (!joined)
        {
            TryRejoinRoom();
            return;
        }

        int currentFrameId;
        using (var nativeCameraClass = new AndroidJavaClass(NativeCameraClass))
        {
            currentFrameId = nativeCameraClass.CallStatic<int>("getLatestFrameId");
        }
        if (heartbeat) Debug.Log(LogTag + "tick: currentFrameId=" + currentFrameId + " lastSentFrameId=" + lastSentFrameId);

        if (currentFrameId == lastSentFrameId) return;

        byte[] jpegBytes;
        using (var nativeCameraClass = new AndroidJavaClass(NativeCameraClass))
        {
            jpegBytes = nativeCameraClass.CallStatic<byte[]>("getLatestFrameBytes");
        }
        if (jpegBytes == null || jpegBytes.Length == 0) return;

        lastSentFrameId = currentFrameId;
        SendFramedJpeg(jpegBytes);
        Debug.Log(LogTag + "Sent frame id=" + currentFrameId + " size=" + jpegBytes.Length);
    }

    private void TryRejoinRoom()
    {
        double now = rejoinStopwatch.Elapsed.TotalSeconds;
        if (now < StartupGraceSeconds) return;
        if (now - lastRejoinAttemptSeconds < RejoinCooldownSeconds) return;
        lastRejoinAttemptSeconds = now;

        // FMWebSocketManager.Action_JoinOrCreateRoom() touches Unity API
        // (hideFlags) that throws off the main thread. FMWebSocketCore's own
        // reconnect loop restores the underlying WebSocket connection during
        // background but doesn't automatically re-send the room-join handshake,
        // so wsJoinedRoom stays false forever without this. RegisterRoom() is
        // the minimal, thread-safe piece (just encodes+sends a string over the
        // already-open socket) that resends it.
        if (webSocketManager.fmwebsocket != null && webSocketManager.fmwebsocket.IsWebSocketConnected())
        {
            Debug.Log(LogTag + "Not joined to room, resending room-join handshake");
            webSocketManager.fmwebsocket.RegisterRoom();
        }
        else
        {
            Debug.Log(LogTag + "Not joined to room, socket not connected yet - waiting");
        }
    }

    private void SendFramedJpeg(byte[] jpegBytes)
    {
        dataIdCounter++;

        byte[] header = new byte[15];
        System.Buffer.BlockCopy(System.BitConverter.GetBytes((ushort)frameLabel), 0, header, 0, 2);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(dataIdCounter), 0, header, 2, 2);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(jpegBytes.Length), 0, header, 4, 4);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(0), 0, header, 8, 4); // offset = 0 (single packet)
        header[12] = 0; // isGZipped
        header[13] = 0; // ColorReductionLevel
        header[14] = 0; // isDesktopFrame

        byte[] framedData = new byte[header.Length + jpegBytes.Length];
        System.Buffer.BlockCopy(header, 0, framedData, 0, header.Length);
        System.Buffer.BlockCopy(jpegBytes, 0, framedData, header.Length, jpegBytes.Length);

        webSocketManager.SendToOthers(framedData);
    }

    private void OnApplicationQuit()
    {
        StopNativeCapture();
        StopService();
    }
#endif
}
