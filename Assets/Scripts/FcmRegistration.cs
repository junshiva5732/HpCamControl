using UnityEngine;

public class FcmRegistration : MonoBehaviour
{
    public FMSolution.FMWebSocket.FMWebSocketManager webSocketManager;

    private const string LogTag = "[FCM] ";
    private const string DeviceIdPrefKey = "HpCamControl_DeviceId";

    private string deviceId;
    private string fcmToken;
    private bool pushRegistered = false;

    private static int awakeCallCount = 0;

    private void Awake()
    {
        awakeCallCount++;
        bool hadKey = PlayerPrefs.HasKey(DeviceIdPrefKey);
        string existing = PlayerPrefs.GetString(DeviceIdPrefKey, "");
        Debug.Log(LogTag + "Awake() call #" + awakeCallCount
            + " | HasKey=" + hadKey
            + " | existingValue='" + existing + "'"
            + " | persistentDataPath=" + Application.persistentDataPath
            + " | instanceID=" + GetInstanceID());

        deviceId = existing;
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = System.Guid.NewGuid().ToString();
            PlayerPrefs.SetString(DeviceIdPrefKey, deviceId);
            PlayerPrefs.Save();
            Debug.Log(LogTag + "Generated NEW deviceId=" + deviceId + " and saved. Verify readback='" + PlayerPrefs.GetString(DeviceIdPrefKey, "<MISSING>") + "'");
        }
        else
        {
            Debug.Log(LogTag + "Reusing EXISTING deviceId=" + deviceId);
        }

        // Set before any other script's Start() runs (Unity guarantees all Awake()
        // calls finish before any Start() begins), so FMWebSocketManager's own
        // AutoInit-triggered join already carries this deviceId.
        if (webSocketManager != null) webSocketManager.DeviceId = deviceId;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void Start()
    {
        if (webSocketManager != null)
        {
            webSocketManager.OnJoinedRoomEvent.AddListener(OnJoinedRoom);
        }

        Firebase.FirebaseApp.CheckAndFixDependenciesAsync().ContinueWith(task =>
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                Debug.LogError(LogTag + "CheckAndFixDependenciesAsync failed: " + task.Exception);
                return;
            }
            if (task.Result != Firebase.DependencyStatus.Available)
            {
                Debug.LogError(LogTag + "Firebase dependencies not available: " + task.Result);
                return;
            }

            Firebase.Messaging.FirebaseMessaging.TokenReceived += OnTokenReceived;
            Firebase.Messaging.FirebaseMessaging.GetTokenAsync().ContinueWith(tokenTask =>
            {
                if (tokenTask.IsFaulted || tokenTask.IsCanceled)
                {
                    Debug.LogError(LogTag + "GetTokenAsync failed: " + tokenTask.Exception);
                    return;
                }
                fcmToken = tokenTask.Result;
                Debug.Log(LogTag + "token received");
                TrySendRegisterPush();
            });
        });
    }

    private void OnTokenReceived(object sender, Firebase.Messaging.TokenReceivedEventArgs e)
    {
        fcmToken = e.Token;
        Debug.Log(LogTag + "token refreshed");
        pushRegistered = false;
        TrySendRegisterPush();
    }

    private void OnJoinedRoom(string roomName)
    {
        TrySendRegisterPush();
    }

    private void TrySendRegisterPush()
    {
        if (pushRegistered) return;
        if (string.IsNullOrEmpty(fcmToken)) return;
        if (webSocketManager == null || webSocketManager.fmwebsocket == null) return;
        if (!webSocketManager.fmwebsocket.IsWebSocketConnected()) return;

        string value = webSocketManager.RoomName + "," + deviceId + "," + fcmToken;
        webSocketManager.fmwebsocket.FMWebSocketEvent("registerPush", value);
        pushRegistered = true;
        Debug.Log(LogTag + "registerPush sent for room " + webSocketManager.RoomName);
    }
#endif
}
