package com.hpcamcontrol;

import android.content.Context;
import android.content.Intent;
import android.telecom.Connection;
import android.telecom.DisconnectCause;
import android.util.Log;

public class HpCamConnection extends Connection {

    private static final String TAG = "HpCamConnection";
    private static final String MESSAGING_ACTIVITY_CLASS = "com.google.firebase.MessagingUnityPlayerActivity";

    private final Context appContext;

    public HpCamConnection(Context appContext) {
        this.appContext = appContext;
        setConnectionCapabilities(Connection.CAPABILITY_SUPPORT_HOLD);
    }

    // Telecom calls this on the app that owns a self-managed incoming connection
    // when it decides the app itself must show its own incoming-call UI - unlike a
    // regular SIM call, Android does not render any UI for self-managed connections.
    // This callback carries the exemption that lets the launch below actually reach
    // the foreground and turn the screen on, regardless of lock/screen state.
    @Override
    public void onShowIncomingCallUi() {
        Log.i(TAG, "onShowIncomingCallUi - launching app");
        launchApp();

        // We only needed the connection to get here - there's no real
        // telecom-routed audio call underneath (streaming runs over our own
        // WebSocket), so resolve it immediately instead of leaving a phantom
        // "ongoing call" indicator in the status bar.
        setActive();
        setDisconnected(new DisconnectCause(DisconnectCause.LOCAL));
        destroy();
    }

    @Override
    public void onAnswer() {
        setActive();
    }

    @Override
    public void onReject() {
        setDisconnected(new DisconnectCause(DisconnectCause.REJECTED));
        destroy();
    }

    @Override
    public void onDisconnect() {
        setDisconnected(new DisconnectCause(DisconnectCause.LOCAL));
        destroy();
    }

    private void launchApp() {
        try {
            Intent launchIntent = new Intent();
            launchIntent.setClassName(appContext.getPackageName(), MESSAGING_ACTIVITY_CLASS);
            launchIntent.setFlags(Intent.FLAG_ACTIVITY_NEW_TASK
                    | Intent.FLAG_ACTIVITY_CLEAR_TOP
                    | Intent.FLAG_ACTIVITY_SINGLE_TOP);
            appContext.startActivity(launchIntent);
        } catch (Exception e) {
            Log.w(TAG, "launchApp failed", e);
        }
    }
}
