using System.Collections.Generic;
using UnityEngine;
using FMSolution.FMWebSocket;

// Room join (and therefore streaming) now happens automatically on launch - see
// FMWebSocketManager's own AutoInit/NetworkType Inspector values (left at their
// defaults: AutoInit=true, NetworkType=Room). CamStart only handles the manual
// controls (re-join after closing, hang up, flip camera) and the FCM wake-push
// side effect that a room join triggers server-side.
public class CamStart : MonoBehaviour
{
    public FMWebSocketManager webSocketManager;
    public BackgroundStreamServiceBridge streamServiceBridge;
    public FMSolution.FMETP.WebcamManager webcamManager;
    public GameObject audioEncoderObject;
    
    
    public GameObject goSetting;
    
    

    private const string CmdHangup = "CMD:HANGUP";
    private const string CmdFlipCamera = "CMD:FLIP_CAMERA";
    private const string NativeCameraClass = "com.hpcamcontrol.NativeCameraCapture";

    // FMWebSocketManager's OnJoinedRoomEvent/OnReceivedStringDataEvent fire from
    // whatever thread processed the incoming WebSocket message, not the Unity main
    // thread. AndroidJavaObject/AndroidJavaClass calls (StartService/StopService/
    // switchCamera) are not safe off the main thread - Unity's JNI reflection cache
    // isn't thread-safe and crashes with a NullPointerException in
    // ReflectionHelper.getFieldID when hit concurrently. Queue the work here and
    // drain it in Update(), which always runs on the main thread.
    private readonly Queue<System.Action> mainThreadActions = new Queue<System.Action>();
    private readonly object queueLock = new object();

    // This app assumes exactly two participants in a room - a third device
    // joining the same room causes overlapping/flickering video, since both
    // peers' streams land on the one single-peer display. Rather than build
    // real multi-party support, this is a persistent per-device opt-out: once
    // set, the device never auto-joins or manually joins the room again, until
    // OnClickRejoinRoomAccess() clears it. Survives app restarts (PlayerPrefs),
    // so it's a real "stop participating," not just "don't join this session."
    private const string RoomAccessDisabledKey = "HpCamControl_RoomAccessDisabled";

    private void Awake()
    {
        // Must happen in Awake(), before FMWebSocketManager's own Start() (which
        // is what actually acts on AutoInit) - Unity guarantees all Awake() calls
        // finish before any Start() call, regardless of component/script order,
        // so this is the only place that reliably wins the race.
        if (webSocketManager != null && PlayerPrefs.GetInt(RoomAccessDisabledKey, 0) == 1)
        {
            webSocketManager.AutoInit = false;
            // AutoInit only gates the very first Init() call in
            // FMWebSocketManager.Start() - FMETP's own socket layer has a
            // SEPARATE autoReconnect flag (default true) that reconnects and
            // re-joins the lobby on its own regardless of AutoInit. Without
            // this, AutoInit=false only stops the FIRST auto-join; anything
            // that later gets the socket connected again (including this same
            // autoReconnect loop reacting to some other trigger) still rejoins.
            webSocketManager.Settings.autoReconnect = false;
        }
    }

    private void Start()
    {
        if (webSocketManager != null)
        {
            webSocketManager.OnReceivedStringDataEvent.AddListener(OnStringReceived);
            webSocketManager.OnJoinedRoomEvent.AddListener(OnJoinedRoom);
            // Server-detected TCP close - fires no matter how the peer's app went
            // away (swipe-close, crash, force-stop, network loss), unlike CMD:HANGUP
            // which depends on the peer's process staying alive long enough to send
            // it. This is the reliable path; CMD:HANGUP is just the faster one for
            // the common explicit-close-button case.
            webSocketManager.OnClientDisconnectedEvent.AddListener(OnPeerDisconnected);
        }
        OnClickCloseSetting();
    }

    // Tapping anywhere on screen 5 times in quick succession reveals the
    // settings panel - a deliberate "secret" gesture so it's not one accidental
    // tap away during normal use.
    private const int SettingsRevealTapCount = 5;
    private const float SettingsRevealTapWindowSeconds = 2f;
    private readonly List<float> recentTapTimes = new List<float>();

    private void Update()
    {
        while (true)
        {
            System.Action action;
            lock (queueLock)
            {
                if (mainThreadActions.Count == 0) break;
                action = mainThreadActions.Dequeue();
            }
            action.Invoke();
        }

        DetectSettingsRevealTaps();
    }

    private void DetectSettingsRevealTaps()
    {
        if (goSetting == null) return;
        // GetMouseButtonDown(0) also covers touch input on Android.
        if (!Input.GetMouseButtonDown(0)) return;

        float now = Time.unscaledTime;
        recentTapTimes.Add(now);
        recentTapTimes.RemoveAll(t => now - t > SettingsRevealTapWindowSeconds);

        if (recentTapTimes.Count >= SettingsRevealTapCount)
        {
            recentTapTimes.Clear();
            goSetting.SetActive(true);
        }
    }

    private void RunOnMainThread(System.Action action)
    {
        lock (queueLock)
        {
            mainThreadActions.Enqueue(action);
        }
    }
    // Turns on two-way audio: starts capturing this device's microphone and
    // sends it (receiving is passive - the scene's AudioDecoder is already
    // enabled and wired to FMWebSocketManager.OnReceivedByteDataEvent, so
    // whatever the peer sends just plays automatically once they enable audio
    // too). The "+ AudioEncoder" object starts inactive in the scene and its
    // OnDataByteReadyEvent is already wired to SendToOthers - the only things
    // missing are an actual audio source to capture (AudioEncoder reads
    // whatever the scene's AudioListener hears, not the microphone directly)
    // and activating the object.
    public void OnClickEnableAudio()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += (permission) => StartCoroutine(StartMicAndEncoder());
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, callbacks);
            return;
        }
#endif
        StartCoroutine(StartMicAndEncoder());
    }

    private System.Collections.IEnumerator StartMicAndEncoder()
    {
        if (audioEncoderObject == null || Microphone.devices.Length == 0) yield break;

        string micDevice = Microphone.devices[0];
        int sampleRate = AudioSettings.outputSampleRate;
        AudioClip micClip = Microphone.Start(micDevice, true, 1, sampleRate);

        // Microphone.Start() returns immediately, but the clip has no real
        // samples in it until recording actually begins - playing it too early
        // just plays silence into the audio graph.
        while (Microphone.GetPosition(micDevice) <= 0) yield return null;

        var audioSource = audioEncoderObject.GetComponent<AudioSource>();
        if (audioSource == null) audioSource = audioEncoderObject.AddComponent<AudioSource>();
        audioSource.clip = micClip;
        audioSource.loop = true;
        audioSource.Play();

        var encoder = audioEncoderObject.GetComponent<FMSolution.FMETP.AudioEncoder>();
        // Without this, the mic audio AudioEncoder just captured also plays back
        // out of this device's own speaker - not just redundant but a feedback
        // loop with the mic. AudioEncoder's OnAudioFilterRead zeroes the buffer
        // AFTER reading it into the send queue when this is set, so capture is
        // unaffected.
        if (encoder != null) encoder.MuteLocalAudioPlayback = true;

        audioEncoderObject.SetActive(true);
    }

    // Persistently stops this device from participating in the room at all -
    // for a spare/extra test device, since a third participant in a room built
    // for 1:1 calls causes overlapping/flickering video (both peers' streams
    // land on the one single-peer display). Leaves the room immediately if
    // currently in it, and the PlayerPrefs flag (checked in Awake(), before
    // AutoInit would otherwise kick in) keeps it out on every future launch too.
    public void OnClickLeaveRoomPermanently()
    {
        PlayerPrefs.SetInt(RoomAccessDisabledKey, 1);
        PlayerPrefs.Save();

        if (webSocketManager != null)
        {
            // The wake-push list (server-side pushRegistry) is keyed by deviceId
            // and independent of the live connection/room-join state - it's what
            // FcmRegistration populated earlier via 'registerPush', and it stays
            // registered (surviving app restarts, reconnects, everything below)
            // until explicitly removed. Without this, the server keeps sending
            // Telecom wake-ups to this device forever even though it now refuses
            // to ever join the room - "camera never connects but the app keeps
            // launching itself" is exactly that: still registered, just no
            // longer willing to join. Has to happen here, while still connected -
            // once AutoInit/autoReconnect are off below there's no connection
            // left to send it on.
            if (webSocketManager.fmwebsocket != null && webSocketManager.fmwebsocket.IsWebSocketConnected())
            {
                string unregValue = webSocketManager.RoomName + "," + webSocketManager.DeviceId;
                webSocketManager.fmwebsocket.FMWebSocketEvent("unregisterPush", unregValue);
            }

            webSocketManager.AutoInit = false;
            // See the matching comment in Awake() - without turning this off
            // too, FMETP's own socket layer reconnects and rejoins the lobby on
            // its own, independent of AutoInit, undoing the close below.
            webSocketManager.Settings.autoReconnect = false;
            if (webSocketManager.fmwebsocket != null) webSocketManager.fmwebsocket.autoReconnect = false;
            SendHangupIfJoined();
            webSocketManager.Action_Close();
        }
        OnClickCloseSetting();
    }

    public void OnClickCloseSetting()
    {
        goSetting.SetActive(false);
    }

    // Reverses OnClickLeaveRoomPermanently().
    public void OnClickRejoinRoomAccess()
    {
        Debug.Log("[CamStart] OnClickRejoinRoomAccess");
        PlayerPrefs.SetInt(RoomAccessDisabledKey, 0);
        PlayerPrefs.Save();
        if (webSocketManager != null)
        {
            // These only affect what happens on the NEXT launch (Start() already
            // ran and read AutoInit once for this session) - without the explicit
            // OnClickCreateOrJoinRoom() call below, pressing this while the app is
            // already open just clears the flag silently: it looks like nothing
            // happened, and OnClickChangeCameraView etc. keep no-op'ing because
            // this session's wsJoinedRoom is still false.
            webSocketManager.AutoInit = true;
            webSocketManager.Settings.autoReconnect = true;
            if (webSocketManager.fmwebsocket != null) webSocketManager.fmwebsocket.autoReconnect = true;
        }
        OnClickCloseSetting();
        OnClickCreateOrJoinRoom();
    }

    // Manual re-join (e.g. after OnClickCloseCameraView). Also what a fresh launch
    // effectively does automatically via AutoInit, just triggered by hand instead.
    public void OnClickCreateOrJoinRoom()
    {
        if (webSocketManager == null) return;
        if (PlayerPrefs.GetInt(RoomAccessDisabledKey, 0) == 1)
        {
            Debug.Log("[CamStart] OnClickCreateOrJoinRoom: blocked, RoomAccessDisabled");
            return;
        }
        Debug.Log("[CamStart] OnClickCreateOrJoinRoom");

        webSocketManager.NetworkType = FMWebSocketNetworkType.Room;

        // Connect() only runs when currently Disconnected, so if a WebSocket-only
        // connection is already open, resend the room-join directly on it instead
        // of relying on Init()/Connect() to do something it won't.
        //
        // Deliberately checking Settings.ConnectionStatus here instead of
        // fmwebsocket.IsWebSocketConnected(): that method also treats the
        // WebSocketState.Closing transition state as "connected", so calling this
        // immediately after OnClickCloseCameraView (whose Action_Close() starts an
        // async close) could still take this branch and RegisterRoom() on a socket
        // that's mid-close - the message never really goes anywhere. ConnectionStatus
        // is set to Disconnected synchronously inside StopAll(), so it doesn't race.
        bool stillActivelyConnected = webSocketManager.fmwebsocket != null
            && webSocketManager.Settings.ConnectionStatus == FMWebSocketConnectionStatus.FMWebSocketConnected;

        if (stillActivelyConnected)
        {
            webSocketManager.fmwebsocket.RegisterRoom();
        }
        else
        {
            webSocketManager.Action_JoinOrCreateRoom();
        }
    }

    // Ends the call for both sides by closing the app entirely, not just the
    // connection - re-establishing the reconnect-while-already-initialised state
    // (NetworkType/connectionStatus/initialised all mid-transition) turned out to
    // be an unreliable third-party async path to keep chasing. Quitting means the
    // *next* call always starts from the one path that's actually proven solid:
    // a fresh launch with AutoInit joining the room from scratch.
    public void OnClickCloseCameraView()
    {
        if (webSocketManager == null) return;
        SendHangupIfJoined();
        QuitApp();
    }

    // Covers app closures that don't go through the Close button (swipe from
    // recents, back button on the root activity, OS-initiated shutdown) so the
    // peer isn't left connected to a call nobody is on anymore. Unity calls this
    // on Application.Quit() too, so OnClickCloseCameraView's own send below is
    // technically redundant with this - kept anyway since it's the more reliable,
    // synchronous-feeling path for the common explicit-close case.
    private void OnApplicationQuit()
    {
        SendHangupIfJoined();
    }

    private void SendHangupIfJoined()
    {
        if (webSocketManager == null) return;
        if (webSocketManager.Settings.wsJoinedRoom)
        {
            webSocketManager.SendToOthers(CmdHangup);
        }
    }

    // Tells the peer device to flip between its front/back camera.
    public void OnClickChangeCameraView()
    {
        if (webSocketManager == null) return;
        if (!webSocketManager.Settings.wsJoinedRoom)
        {
            Debug.Log("[CamStart] OnClickChangeCameraView: not sent, wsJoinedRoom=false");
            return;
        }

        Debug.Log("[CamStart] OnClickChangeCameraView: sending CMD:FLIP_CAMERA");
        webSocketManager.SendToOthers(CmdFlipCamera);
    }

    private void OnJoinedRoom(string roomName)
    {
        RunOnMainThread(StartLocalStreaming);
        StartCoroutine(KickWebcamAfterDelay());
    }

    // Works around a cold-start-only quirk observed on-device: the very first
    // WebCamTexture.Play() after app launch (WebcamManager.OnEnable, before any
    // pause/resume has happened) reports the camera as opened/active with no
    // errors, but never actually delivers frames to the texture, leaving the
    // local preview quad permanently black. A subsequent stop+restart (the same
    // path camera-flip and the post-pause resume already use, both confirmed
    // working) reliably recovers it, so force one automatically shortly after
    // the initial join instead of waiting for the user to flip the camera.
    private System.Collections.IEnumerator KickWebcamAfterDelay()
    {
        yield return new WaitForSeconds(2f);
        if (webcamManager != null) webcamManager.Action_useFrontCam(webcamManager.useFrontCam);
    }

    private void QuitApp()
    {
        webSocketManager.Action_Close();
        StopLocalStreaming();

        // finish() alone leaves a stale task card in Recents even after the
        // process dies. finishAndRemoveTask() removes that card too, so the app
        // is gone from Recents as well as actually closed.
        FinishAndRemoveTask();

        // FinishAndRemoveTask() still only ends the Activity - the OS can leave
        // the underlying process alive for a while afterward, especially with a
        // foreground service or background threads still unwinding. Killing the
        // process directly guarantees it's actually gone. Peer notification
        // doesn't depend on this running cleanly first - the server detects the
        // socket closing either way - so there's no need to wait before doing
        // this.
        KillProcess();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void KillProcess()
    {
        using (var processClass = new AndroidJavaClass("android.os.Process"))
        {
            int pid = processClass.CallStatic<int>("myPid");
            processClass.CallStatic("killProcess", pid);
        }
    }

    private void FinishAndRemoveTask()
    {
        using (var activityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = activityClass.GetStatic<AndroidJavaObject>("currentActivity"))
        {
            activity.Call("finishAndRemoveTask");
        }
    }
#else
    private void KillProcess() { }
    private void FinishAndRemoveTask() { }
#endif

    private void OnStringReceived(string data)
    {
        Debug.Log("[CamStart] OnStringReceived: " + data);
        switch (data)
        {
            case CmdHangup:
                RunOnMainThread(QuitApp);
                break;
            case CmdFlipCamera:
                RunOnMainThread(FlipLocalCamera);
                break;
        }
    }

    private void OnPeerDisconnected(string disconnectedWsid)
    {
        RunOnMainThread(QuitApp);
    }

    // The foreground video source is FMETP's own WebcamManager (Unity WebCamTexture),
    // not our custom NativeCameraCapture - that one is background-only (it exists
    // because WebCamTexture itself closes as soon as the app is backgrounded). Both
    // get flipped here so switching stays correct across a foreground/background
    // transition mid-call.
    private void FlipLocalCamera()
    {
        Debug.Log("[CamStart] FlipLocalCamera invoked");
        if (webcamManager != null)
        {
            webcamManager.Action_useFrontCam(!webcamManager.useFrontCam);
        }
        FlipNativeBackgroundCamera();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void StartLocalStreaming()
    {
        if (streamServiceBridge != null) streamServiceBridge.StartService();
    }

    private void StopLocalStreaming()
    {
        if (streamServiceBridge != null) streamServiceBridge.StopService();
    }

    private void FlipNativeBackgroundCamera()
    {
        using (var activityClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = activityClass.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var nativeCameraClass = new AndroidJavaClass(NativeCameraClass))
        {
            nativeCameraClass.CallStatic("switchCamera", activity);
        }
    }
#else
    private void StartLocalStreaming() { }
    private void StopLocalStreaming() { }
    private void FlipNativeBackgroundCamera() { }
#endif
}
