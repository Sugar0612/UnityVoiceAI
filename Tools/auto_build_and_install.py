# -*- coding: utf-8 -*-
"""自动等Unity可用 -> 打包 -> 安装到设备 -> 设桌面 -> 重启应用"""
import json, os, subprocess, sys, time

PROJECT = r'E:\ParasiticWasps\UnityVoiceAI'
UNITY = r'D:\Engine\Unity\6000.3.20f1\Editor\Unity.exe'
LOCK = os.path.join(PROJECT, 'Temp', 'UnityLockfile')
APK = os.path.join(PROJECT, 'Builds', 'VoiceAI.apk')
PKG = 'com.parasiticwasps.voiceai'
LOG = os.path.join(PROJECT, 'Tools', 'auto_build.log')
DEADLINE = time.time() + 45 * 60

KEY_CODE = ('var canvas = GameObject.Find("VoiceAI_Canvas"); if (canvas == null) return "canvas not found";'
            'var ctrl = canvas.GetComponent("VoiceAI.VoiceAIController") as MonoBehaviour;'
            'var t = ctrl.GetType();'
            'var f = t.GetField("deepSeek", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);'
            'var ds = f.GetValue(ctrl); var kf = ds.GetType().GetField("apiKey");'
            'var cur = (string)kf.GetValue(ds);'
            'kf.SetValue(ds, "sk-d0ee5c03c32a4b9f98cfb4a2f4ab402c");'
            'UnityEditor.EditorUtility.SetDirty(ctrl);'
            'UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();'
            'return "key set (was " + (string.IsNullOrEmpty(cur) ? "EMPTY" : "len" + cur.Length) + ")";')

def log(msg):
    line = time.strftime('%H:%M:%S ') + msg
    with open(LOG, 'a', encoding='utf-8') as f:
        f.write(line + '\n')
    print(line, flush=True)

import urllib.request, urllib.error
_MCP_URL = 'http://127.0.0.1:8083/mcp'

def mcp(tool, args):
    try:
        req = urllib.request.Request(_MCP_URL, data=json.dumps(
            {"jsonrpc": "2.0", "id": 1, "method": "initialize",
             "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                        "clientInfo": {"name": "w", "version": "1"}}}).encode(),
            headers={"Content-Type": "application/json",
                     "Accept": "application/json, text/event-stream"})
        with urllib.request.urlopen(req, timeout=30) as r:
            sid = r.headers.get("mcp-session-id")
            r.read()
        req2 = urllib.request.Request(_MCP_URL, data=json.dumps(
            {"jsonrpc": "2.0", "method": "notifications/initialized"}).encode(),
            headers={"Content-Type": "application/json",
                     "Accept": "application/json, text/event-stream",
                     "mcp-session-id": sid or ""})
        urllib.request.urlopen(req2, timeout=30).read()
        req3 = urllib.request.Request(_MCP_URL, data=json.dumps(
            {"jsonrpc": "2.0", "id": 2, "method": "tools/call",
             "params": {"name": tool, "arguments": args}}).encode(),
            headers={"Content-Type": "application/json",
                     "Accept": "application/json, text/event-stream",
                     "mcp-session-id": sid or ""})
        with urllib.request.urlopen(req3, timeout=880) as r:
            body = r.read().decode('utf-8', 'replace')
        lines = [l[5:].strip() for l in body.splitlines() if l.startswith('data:')]
        return chr(10).join(lines) if lines else body
    except Exception as e:
        return 'EXC:' + str(e)

def run_cmd(cmd, timeout=600):
    """bytes 模式 + 重试：规避本机 subprocess 管道线程偶发崩溃"""
    last = ''
    for _ in range(3):
        try:
            r = subprocess.run(cmd, capture_output=True, timeout=timeout)
            out = (r.stdout or b'') + b'\n' + (r.stderr or b'')
            return out.decode('utf-8', 'replace')
        except IndexError:
            time.sleep(2)
        except subprocess.TimeoutExpired:
            return 'TIMEOUT'
        except Exception as e:
            return 'EXC:' + str(e)
    return 'PIPE_FAIL: ' + last

def adb(*args):
    return run_cmd(['adb'] + list(args))

def unity_running():
    return 'unity.exe' in run_cmd(['tasklist']).lower()

def build_via_mcp():
    mcp('execute_code', {'action': 'execute', 'code': KEY_CODE})
    log('MCP: key set + scene save')
    mcp('manage_scene', {'action': 'save'})
    out = mcp('manage_build', {'action': 'build', 'target': 'android',
                               'output_path': 'E:/ParasiticWasps/UnityVoiceAI/Builds/VoiceAI.apk'})
    log('MCP build scheduled: ' + out.strip()[:200])
    job = None
    try:
        job = json.loads(out).get('data', {}).get('job_id')
    except Exception:
        pass
    if not job:
        return False
    for _ in range(60):
        time.sleep(20)
        st = mcp('manage_build', {'action': 'status', 'job_id': job})
        if 'succeeded' in st:
            log('MCP build succeeded')
            return True
        if 'failed' in st or 'cancel' in st:
            log('MCP build failed: ' + st.strip()[:200])
            return False
    return False

def build_via_cli():
    try:
        os.remove(LOCK)
    except OSError:
        pass
    rc = subprocess.call([UNITY, '-batchmode', '-quit', '-projectPath', PROJECT,
                          '-executeMethod', 'CliBuild.BuildAndroid',
                          '-logFile', os.path.join(PROJECT, 'Builds', 'cli_build.log')], timeout=900)
    log('CLI build rc=' + str(rc))
    return rc == 0

def deploy():
    log('installing APK...')
    r = adb('install', '-r', '-g', APK)
    log('install: ' + (r.stdout or r.stderr).strip()[-80:])
    if 'Success' not in (r.stdout or ''):
        return False
    adb('shell', 'cmd', 'package', 'set-home-activity', PKG + '/com.unity3d.player.UnityPlayerActivity')
    adb('shell', 'am', 'force-stop', PKG)
    time.sleep(2)
    adb('shell', 'monkey', '-p', PKG, '-c', 'android.intent.category.LAUNCHER', '1')
    time.sleep(8)
    ps = adb('shell', 'ps -A')
    procs = [l.split()[1] for l in ps.splitlines() if PKG in l]
    log('processes: ' + ', '.join(procs))
    return PKG in ps

def main():
    built = False
    while time.time() < DEADLINE and not built:
        out = mcp('execute_code', {'action': 'execute', 'code': 'return "ok";'})
        if '"ok"' in out:
            log('MCP session alive -> building via editor')
            built = build_via_mcp()
            if built:
                break
        if not unity_running():
            log('Unity closed -> command-line build')
            built = build_via_cli()
            if built:
                break
        time.sleep(45)
    if not built:
        log('TIMEOUT: 45分钟内未能获得Unity进行打包')
        return
    ok = deploy()
    log('[DONE] deploy ' + ('OK' if ok else 'FAILED'))

main()
