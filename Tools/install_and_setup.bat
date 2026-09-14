@echo off
rem ============================================================
rem One-click install + kiosk setup (ASCII only, avoid codepage issues)
rem Usage: connect the Android device via USB (USB debugging on,
rem        authorization accepted), then run this script.
rem Steps: install APK -> grant runtime permissions -> allow overlay
rem        (required for watchdog to restart UI on Android 10+)
rem        -> battery optimization whitelist -> launch app
rem Built-in (no script needed): boot auto-start + crash watchdog
rem ============================================================
setlocal
set PKG=com.parasiticwasps.voiceai
set APK=%~dp0..\Builds\VoiceAI.apk

echo == 1/6 Waiting for device ==
adb wait-for-device || goto :err
adb devices

echo == 2/6 Installing APK ==
adb install -r "%APK%" || goto :err

echo == 3/6 Granting runtime permissions (record audio / notifications) ==
adb shell pm grant %PKG% android.permission.RECORD_AUDIO
adb shell pm grant %PKG% android.permission.POST_NOTIFICATIONS

echo == 4/6 Allowing overlay (Android 10+ background activity start) ==
adb shell appops set %PKG% SYSTEM_ALERT_WINDOW allow

echo == 5/6 Battery optimization whitelist ==
adb shell dumpsys deviceidle whitelist +%PKG%

echo == 6/6 Launching app ==
adb shell monkey -p %PKG% -c android.intent.category.LAUNCHER 1

echo.
echo Done. Boot auto-start and crash watchdog are built into the APK.
exit /b 0

:err
echo FAILED. Check USB debugging authorization or APK path.
exit /b 1
