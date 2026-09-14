package com.parasiticwasps.voiceai.kiosk;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.util.Log;

/**
 * 开机自启动接收器。
 * Android 10+ 的后台启动限制明确豁免 BOOT_COMPLETED，
 * 因此开机后可以直接拉起主界面；同时拉起崩溃看门狗服务。
 */
public class BootReceiver extends BroadcastReceiver {
    private static final String TAG = "VoiceAIKiosk";

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent == null ? null : intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action)
                && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)
                && !Intent.ACTION_LOCKED_BOOT_COMPLETED.equals(action)) {
            return;
        }
        Log.i(TAG, "received action: " + action + ", launching app");

        PackageManager pm = context.getPackageManager();
        Intent launch = pm.getLaunchIntentForPackage(context.getPackageName());
        if (launch != null) {
            launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            try {
                context.startActivity(launch);
            } catch (Throwable t) {
                Log.e(TAG, "start activity failed", t);
            }
        }

        try {
            Intent svc = new Intent(context, WatchdogService.class);
            if (android.os.Build.VERSION.SDK_INT >= 26) {
                context.startForegroundService(svc);
            } else {
                context.startService(svc);
            }
        } catch (Throwable t) {
            Log.e(TAG, "start watchdog failed", t);
        }
    }
}
