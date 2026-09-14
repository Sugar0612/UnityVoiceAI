package com.parasiticwasps.voiceai.kiosk;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/**
 * 崩溃测试工具：收到自定义广播后立刻终止主进程（等效真实崩溃）。
 * 用途：adb shell am broadcast -a com.parasiticwasps.voiceai.CRASH_TEST
 * 用于验证 :watchdog 看门狗能否自动拉起应用。正式部署可从清单中移除此接收器。
 */
public class CrashTestReceiver extends BroadcastReceiver {
    private static final String TAG = "VoiceAIKiosk";

    @Override
    public void onReceive(Context context, Intent intent) {
        Log.w(TAG, "CRASH_TEST received, killing main process now");
        android.os.Process.killProcess(android.os.Process.myPid());
    }
}
