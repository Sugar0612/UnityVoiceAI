package com.parasiticwasps.voiceai.kiosk;

import android.app.ActivityManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

/**
 * 崩溃看门狗服务（运行在独立进程 :watchdog）。
 * 周期检查本应用主进程是否存在：主进程崩溃后自动重新拉起主界面。
 * getRunningAppProcesses 在新版 Android 上会返回"调用者自己应用"的进程列表，
 * 同包名的 :watchdog 进程可以看到主进程，主进程死亡时列表中不再包含它——
 * 这个特性正好用于判断主进程是否存活。
 *
 * 注意：Android 10+ 从后台启动 Activity 需要"显示在其他应用上层"权限，
 * 安装后请用 adb 执行：adb shell appops set <包名> SYSTEM_ALERT_WINDOW allow
 */
public class WatchdogService extends Service {
    private static final String TAG = "VoiceAIKiosk";
    private static final String CHANNEL_ID = "voiceai_watchdog";
    private static final long CHECK_INTERVAL_MS = 2000L;
    private static final int MISSED_TICKS_TO_RESTART = 2;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private int missedTicks = 0;
    private boolean ticking = false;

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            try {
                check();
            } catch (Throwable t) {
                Log.e(TAG, "check failed", t);
            }
            handler.postDelayed(tick, CHECK_INTERVAL_MS);
        }
    };

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        startForegroundCompat();
        if (!ticking) {
            ticking = true;
            handler.postDelayed(tick, CHECK_INTERVAL_MS);
        }
        return START_STICKY; // 系统杀掉看门狗后也会尝试重启它
    }

    private void startForegroundCompat() {
        try {
            Notification.Builder b;
            if (android.os.Build.VERSION.SDK_INT >= 26) {
                NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
                nm.createNotificationChannel(new NotificationChannel(CHANNEL_ID, "VoiceAI 常驻守护",
                        NotificationManager.IMPORTANCE_MIN));
                b = new Notification.Builder(this, CHANNEL_ID);
            } else {
                b = new Notification.Builder(this);
            }
            Notification n = b.setSmallIcon(android.R.drawable.stat_sys_download)
                    .setContentTitle("VoiceAI 数字人")
                    .setContentText("守护运行中")
                    .setOngoing(true)
                    .build();
            startForeground(1, n);
        } catch (Throwable t) {
            Log.e(TAG, "startForeground failed", t);
        }
    }

    private void check() {
        if (mainProcessAlive()) {
            missedTicks = 0;
            return;
        }
        missedTicks++;
        Log.w(TAG, "main process not found, missed=" + missedTicks);
        if (missedTicks >= MISSED_TICKS_TO_RESTART) {
            missedTicks = 0;
            restartMain();
        }
    }

    private boolean mainProcessAlive() {
        String pkg = getPackageName();
        ActivityManager am = (ActivityManager) getSystemService(ACTIVITY_SERVICE);
        try {
            for (ActivityManager.RunningAppProcessInfo p : am.getRunningAppProcesses()) {
                if (pkg.equals(p.processName)) {
                    return true;
                }
            }
        } catch (Throwable t) {
            Log.e(TAG, "query processes failed", t);
        }
        return false;
    }

    private void restartMain() {
        PackageManager pm = getPackageManager();
        Intent i = pm.getLaunchIntentForPackage(getPackageName());
        if (i == null) return;
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            startActivity(i);
            Log.i(TAG, "main activity restarted by watchdog");
        } catch (Throwable t) {
            Log.e(TAG, "restart activity failed", t);
        }
    }
}
