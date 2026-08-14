package com.hpcamcontrol;

import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.google.firebase.messaging.FirebaseMessagingService;
import com.google.firebase.messaging.RemoteMessage;

// Delivers an "incoming call" style wake-up when the relay server signals that a
// peer joined a room this device is registered for, even if this app's process was
// fully killed - Android auto-starts the process to deliver an FCM message (unless
// the user explicitly Force-Stopped the app in Settings, which no app can escape).
//
// The launch target is referenced by string (setClassName), not by class reference,
// because com.google.firebase.MessagingUnityPlayerActivity is generated into the
// launcher/app Gradle module at build time (see FirebaseMessagingActivityGenerator.cs),
// while this class lives in a library module that the app module depends on - a
// direct class reference the other way around would be a circular module dependency.
public class WakeUpMessagingService extends FirebaseMessagingService {

    private static final String TAG = "WakeUpMessagingService";
    private static final String MESSAGING_ACTIVITY_CLASS = "com.google.firebase.MessagingUnityPlayerActivity";

    @Override
    public void onMessageReceived(RemoteMessage message) {
        super.onMessageReceived(message);

        String type = message.getData().get("type");
        Log.i(TAG, "onMessageReceived type=" + type);
        if (!"wake".equals(type)) return;

        // Primary path: a self-managed Telecom "incoming call" carries the
        // exemption needed to bring the app to the foreground even when the
        // screen is already on and unlocked (see TelecomCallHelper/HpCamConnection).
        TelecomCallHelper.triggerIncomingCall(getApplicationContext());

        // Fallback in case the Telecom path is rejected on this device/OS build -
        // works in some background states, though not all. No visible notification
        // is shown either way; the app is just expected to appear directly.
        launchAppDirectly();
    }

    private void launchAppDirectly() {
        try {
            Context context = getApplicationContext();
            Intent launchIntent = new Intent();
            launchIntent.setClassName(context.getPackageName(), MESSAGING_ACTIVITY_CLASS);
            launchIntent.setFlags(Intent.FLAG_ACTIVITY_NEW_TASK
                    | Intent.FLAG_ACTIVITY_CLEAR_TOP
                    | Intent.FLAG_ACTIVITY_SINGLE_TOP);
            context.startActivity(launchIntent);
            Log.i(TAG, "launchAppDirectly: startActivity called");
        } catch (Exception e) {
            Log.w(TAG, "launchAppDirectly failed", e);
        }
    }

    @Override
    public void onNewToken(String token) {
        super.onNewToken(token);
        // The C# side re-registers its token on every app Start() (via registerPush),
        // which covers rotation in practice since this app is opened frequently.
        Log.i(TAG, "onNewToken (will be re-registered next app launch)");
    }
}
