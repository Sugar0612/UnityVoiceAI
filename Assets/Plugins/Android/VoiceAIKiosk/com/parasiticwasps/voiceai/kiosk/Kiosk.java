package com.parasiticwasps.voiceai.kiosk;

import android.app.Activity;
import android.content.Intent;
import android.util.Log;

/**
 * Unity -> Java 桥接入口。
 * 应用启动后由 Unity 侧调用 startWatchdog，确保看门狗服务随应用常驻；
 * 开机场景则由 BootReceiver 直接拉起。
 */
public final class Kiosk {
    private static final String TAG = "VoiceAIKiosk";

    public static void startWatchdog(Activity activity) {
        if (activity == null) {
            Log.w(TAG, "startWatchdog: activity is null");
            return;
        }
        try {
            Intent i = new Intent(activity, WatchdogService.class);
            if (android.os.Build.VERSION.SDK_INT >= 26) {
                activity.startForegroundService(i);
            } else {
                activity.startService(i);
            }
            Log.i(TAG, "watchdog service requested");
        } catch (Throwable t) {
            Log.e(TAG, "startWatchdog failed", t);
        }
    }
}
