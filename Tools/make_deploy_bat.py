# -*- coding: utf-8 -*-
"""生成 Deploy/install_app.bat（GBK 编码 + CRLF，cmd 直接双击可用的中文批处理）"""
import os

BAT = r'''@echo off
chcp 936 >nul
title VoiceAI 数字人 一键部署
setlocal
set PKG=com.parasiticwasps.voiceai
set APK=%~dp0VoiceAI.apk

rem ===== 1. 定位 adb：优先用本文件夹自带的 =====
set "ADB="
if exist "%~dp0adb\adb.exe" set "ADB=%~dp0adb\adb.exe"
if not defined ADB (
    adb version >nul 2>&1
    if not errorlevel 1 set "ADB=adb"
)
if not defined ADB (
    echo [错误] 未找到 adb。请把本文件夹整个拷贝使用，或将 adb 加入 PATH。
    pause
    exit /b 1
)

echo ===== 1/5 检测设备（最长等待60秒）=====
echo 请插好USB线，并在设备上开启 USB 调试、允许调试授权
"%ADB%" start-server >nul 2>&1
set /a N=0
:waitdev
"%ADB%" get-state >nul 2>&1
if not errorlevel 1 goto devok
set /a N+=1
if %N% geq 60 (
    echo [错误] 60秒内未检测到设备。请检查：USB线是否可用、设备是否已开启USB调试、授权弹窗是否已允许。
    pause
    exit /b 1
)
ping -n 2 127.0.0.1 >nul
goto waitdev
:devok
set "SERIAL="
set "SERIALARG="
for /f "skip=1 tokens=1,2" %%a in ('"%ADB%" devices') do (
    if "%%b"=="device" if not defined SERIAL set "SERIAL=%%a"
)
if defined SERIAL (
    echo 已连接设备: %SERIAL%
    set "SERIALARG=-s %SERIAL%"
)

echo ===== 2/5 安装 APK（约900MB，需1-3分钟，请勿拔线）=====
"%ADB%" %SERIALARG% install -r -g "%APK%"
if errorlevel 1 (
    echo [错误] 安装失败。请确认设备存储空间足够、USB调试授权正常。
    pause
    exit /b 1
)

echo ===== 3/5 授权与白名单（部分系统会拒绝，失败不影响使用）=====
"%ADB%" %SERIALARG% shell pm grant %PKG% android.permission.RECORD_AUDIO >nul 2>&1
"%ADB%" %SERIALARG% shell pm grant %PKG% android.permission.POST_NOTIFICATIONS >nul 2>&1
"%ADB%" %SERIALARG% shell appops set %PKG% SYSTEM_ALERT_WINDOW allow >nul 2>&1
"%ADB%" %SERIALARG% shell dumpsys deviceidle whitelist +%PKG% >nul 2>&1
echo 完成。

echo ===== 4/5 启动应用 =====
"%ADB%" %SERIALARG% shell monkey -p %PKG% -c android.intent.category.LAUNCHER 1 >nul 2>&1
ping -n 6 127.0.0.1 >nul

echo ===== 5/5 验证进程 =====
"%ADB%" %SERIALARG% shell ps -A | findstr /c:"%PKG%"
if errorlevel 1 (
    echo [警告] 未检测到应用进程，请检查设备画面。
) else (
    echo 应用已运行（正常应看到主进程和 :watchdog 看门狗两个进程）。
)

echo.
echo ============================================================
echo  部署完成！以下能力已内置在应用中，无需额外设置：
echo    1. 开机自启动 —— 设备重启后应用自动打开
echo    2. 崩溃自动重启 —— 主进程消失约4秒内自动拉起
echo.
echo  自测崩溃重启（可选）：
echo    "%ADB%" %SERIALARG% shell am broadcast -a %PKG%.CRASH_TEST
echo ============================================================
pause
'''

out = os.path.join(os.path.dirname(__file__), '..', 'Deploy', 'install_app.bat')
os.makedirs(os.path.dirname(out), exist_ok=True)
data = BAT.replace('\n', '\r\n').encode('gbk')
with open(out, 'wb') as f:
    f.write(data)
print('written:', os.path.abspath(out), len(data), 'bytes (GBK)')
