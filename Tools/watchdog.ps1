# PC 侧备用看门狗（可选）：设备长期连着电脑时使用
# 用法：powershell -ExecutionPolicy Bypass -File watchdog.ps1
# 设备内置的 :watchdog 看门狗已负责崩溃拉起；本脚本用于 PC 在位时的双保险
param(
    [string]$Pkg = "com.parasiticwasps.voiceai",
    [int]$IntervalSec = 5
)
Write-Host "PC watchdog watching $Pkg (every ${IntervalSec}s)"
while ($true) {
    try {
        $ps = adb -s $(adb devices | Select-String "device`$" | ForEach-Object { ($_ -split "\s+")[0] } | Select-Object -First 1) shell ps -A 2>$null |
            Select-String ("^\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+" + [regex]::Escape($Pkg) + "$")
        if (-not $ps) {
            Write-Host "$(Get-Date -Format T) main process NOT running, launching..."
            adb shell monkey -p $Pkg -c android.intent.category.LAUNCHER 1 | Out-Null
        }
    } catch {
        Write-Host "$(Get-Date -Format T) adb error: $_"
    }
    Start-Sleep -Seconds $IntervalSec
}
