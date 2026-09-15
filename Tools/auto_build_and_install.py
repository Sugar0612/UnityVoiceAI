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

def mcp(tool, args):
    try:
        r = subprocess.run([sys.executable, os.path.join(PROJECT, 'Tools', 'unity_mcp.py'),
                            tool, json.dumps(args)], capture_output=True, text=True,
                           timeout=900, encoding='utf-8', errors='replace')
        return r.stdout or ''
    except Exception as e:
        return 'EXC:' + str(e)

def adb(*args):
    return subprocess.run(['adb'] + list(args), capture_output=True, text=True,
                          timeout=600, encoding='utf-8', errors='replace')

def unity_running():
    return 'unity.exe' in subprocess.run(['tasklist'], capture_output=True, text=True).stdout.lower()

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
    ps = adb('shell', 'ps -A').stdout
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
