package com.hpcamcontrol;

import android.net.Uri;
import android.telecom.Connection;
import android.telecom.ConnectionRequest;
import android.telecom.ConnectionService;
import android.telecom.PhoneAccountHandle;
import android.telecom.TelecomManager;
import android.util.Log;

// Registered as a self-managed Telecom ConnectionService purely to get the
// background-activity-start / screen-wake exemption Android grants to incoming-call
// UI, even when the screen is already on and unlocked - a plain FCM background
// service is not allowed to force an Activity to the foreground in that state.
// There is no real telecom-routed audio call: HpCamConnection resolves itself
// immediately after launching the app (see its onShowIncomingCallUi()).
public class HpCamConnectionService extends ConnectionService {

    private static final String TAG = "HpCamConnectionService";

    @Override
    public Connection onCreateIncomingConnection(PhoneAccountHandle connectionManagerPhoneAccount, ConnectionRequest request) {
        Log.i(TAG, "onCreateIncomingConnection");

        HpCamConnection connection = new HpCamConnection(getApplicationContext());
        connection.setConnectionProperties(Connection.PROPERTY_SELF_MANAGED);
        connection.setAudioModeIsVoip(false);

        Uri address = request.getAddress();
        if (address != null) {
            connection.setAddress(address, TelecomManager.PRESENTATION_ALLOWED);
        }
        connection.setCallerDisplayName("HpCamControl", TelecomManager.PRESENTATION_ALLOWED);
        connection.setRinging();
        return connection;
    }

    @Override
    public void onCreateIncomingConnectionFailed(PhoneAccountHandle connectionManagerPhoneAccount, ConnectionRequest request) {
        Log.w(TAG, "onCreateIncomingConnectionFailed for " + request);
    }
}
