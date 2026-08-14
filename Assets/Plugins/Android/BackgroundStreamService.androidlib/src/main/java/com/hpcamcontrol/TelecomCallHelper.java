package com.hpcamcontrol;

import android.content.ComponentName;
import android.content.Context;
import android.os.Build;
import android.os.Bundle;
import android.telecom.PhoneAccount;
import android.telecom.PhoneAccountHandle;
import android.telecom.TelecomManager;
import android.util.Log;

public class TelecomCallHelper {

    private static final String TAG = "TelecomCallHelper";
    private static final String ACCOUNT_ID = "HpCamControlAccount";

    public static void triggerIncomingCall(Context context) {
        // PhoneAccount.CAPABILITY_SELF_MANAGED requires API 26 (Oreo).
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;

        try {
            TelecomManager telecomManager = (TelecomManager) context.getSystemService(Context.TELECOM_SERVICE);
            if (telecomManager == null) {
                Log.w(TAG, "TelecomManager unavailable");
                return;
            }

            PhoneAccountHandle handle = getPhoneAccountHandle(context);
            registerPhoneAccount(context, telecomManager, handle);

            Bundle extras = new Bundle();
            telecomManager.addNewIncomingCall(handle, extras);
            Log.i(TAG, "addNewIncomingCall requested");
        } catch (Exception e) {
            // Older/OEM Android builds can reject self-managed registration or
            // addNewIncomingCall in ways that vary by device - the caller still
            // falls back to the plain notification/direct-launch path.
            Log.w(TAG, "triggerIncomingCall failed", e);
        }
    }

    private static PhoneAccountHandle getPhoneAccountHandle(Context context) {
        return new PhoneAccountHandle(
                new ComponentName(context, HpCamConnectionService.class),
                ACCOUNT_ID);
    }

    // Self-managed accounts (unlike SIM-backed ones) don't need the user to
    // enable anything in Settings - registerPhoneAccount() alone makes it usable,
    // as long as the app holds MANAGE_OWN_CALLS (a normal, install-time permission).
    private static void registerPhoneAccount(Context context, TelecomManager telecomManager, PhoneAccountHandle handle) {
        PhoneAccount account = PhoneAccount.builder(handle, "HpCamControl")
                .setCapabilities(PhoneAccount.CAPABILITY_SELF_MANAGED)
                .build();
        telecomManager.registerPhoneAccount(account);
    }
}
