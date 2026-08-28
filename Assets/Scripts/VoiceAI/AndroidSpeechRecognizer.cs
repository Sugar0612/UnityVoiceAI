using System;
using UnityEngine;

namespace VoiceAI
{
    /// <summary>
    /// Android 系统自带语音识别（android.speech.SpeechRecognizer）封装：
    /// 国行 ROM（ColorOS/小米/vivo 等）内置厂商识别引擎，无需 Key、速度极快。
    /// 优先离线模型（EXTRA_PREFER_OFFLINE），不可用自动走厂商云。
    /// 注意：SpeechRecognizer 自管麦克风——使用期间不得开启 Unity 麦克风/唤醒监听。
    /// 仅在真机可用（编辑器/非 Android 直接返回不可用，由调用方回退云端 STT）。
    /// </summary>
    public class AndroidSpeechRecognizer : IDisposable
    {
        // 编辑器下这些类型不存在，字段/成员逐一套条件编译（否则 CS0246）
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _recognizer;
        private ListenerProxy _listener;
        private AndroidJavaObject _activity;
        private bool _running;
#endif

        /// <summary>设备上是否有可用的识别服务</summary>
        public static bool IsAvailable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                    .GetStatic<AndroidJavaObject>("currentActivity"))
                using (var sr = new AndroidJavaClass("android.speech.SpeechRecognizer"))
                {
                    return sr.CallStatic<bool>("isRecognitionAvailable", activity);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] 系统识别可用性检查失败: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>创建识别器实例；不可用返回 null</summary>
        public static AndroidSpeechRecognizer Create()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsAvailable()) return null;
            var r = new AndroidSpeechRecognizer();
            return r.Init() ? r : null;
#else
            return null;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool Init()
        {
            try
            {
                _activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                    .GetStatic<AndroidJavaObject>("currentActivity");
                _recognizer = new AndroidJavaClass("android.speech.SpeechRecognizer")
                    .CallStatic<AndroidJavaObject>("createSpeechRecognizer", _activity);

                _listener = new ListenerProxy(
                    finalText => { _running = false; OnResult?.Invoke(finalText); },
                    partialText => OnPartial?.Invoke(partialText),
                    errCode => { _running = false; OnError?.Invoke(ErrToString(errCode)); },
                    rms => OnRms?.Invoke(rms));

                _recognizer.Call("setRecognitionListener", _listener);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] 系统识别器创建失败: " + e.Message);
                Dispose();
                return false;
            }
        }

        /// <summary>开始识别（自管麦克风，含说话结束检测）</summary>
        public bool Start(string language = "zh-CN")
        {
            if (_recognizer == null) return false;
            try
            {
                using (var intent = new AndroidJavaObject("android.content.Intent"))
                {
                    intent.Call<AndroidJavaObject>("setAction", "android.speech.action.RECOGNIZE_SPEECH");
                    intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE_MODEL", "free_form");
                    intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE", language);
                    intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.PARTIAL_RESULTS", true);
                    // 优先离线模型（API 23+）：国行 ROM 基本都带本地中文模型，延迟最低；
                    // 无离线模型时系统自动回落厂商云端
                    if (AndroidVersion >= 23)
                        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.PREFER_OFFLINE", true);
                    _recognizer.Call("startListening", intent);
                }
                _running = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] 系统识别启动失败: " + e.Message);
                _running = false;
                return false;
            }
        }

        private static int AndroidVersion
        {
            get
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    return version.GetStatic<int>("SDK_INT");
            }
        }
#endif

        /// <summary>主动停止：已说内容仍会回调 OnResult（截尾识别）</summary>
        public void Stop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_recognizer != null && _running)
            {
                try { _recognizer.Call("stopListening"); } catch { }
                _running = false;
            }
#endif
        }

        public void Dispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_recognizer != null)
            {
                try { _recognizer.Call("destroy"); } catch { }
                _recognizer.Dispose();
                _recognizer = null;
            }
            _activity?.Dispose();
            _activity = null;
#endif
        }

        /// <summary>最终识别文本（一句话结束自动触发）</summary>
        public event Action<string> OnResult;
        /// <summary>中间结果（实时上屏）</summary>
        public event Action<string> OnPartial;
        /// <summary>错误（code 转成可读串）</summary>
        public event Action<string> OnError;
        /// <summary>实时音量（0..10，喂给流光特效）</summary>
        public event Action<float> OnRms;

        private static string ErrToString(int code)
        {
            // android.speech.SpeechRecognizer.ERROR_* 常量
            switch (code)
            {
                case 1: return "网络错误";
                case 2: return "网络超时";
                case 3: return "录音音频错误";
                case 4: return "服务器错误";
                case 5: return "客户端错误";
                case 6: return "没听到说话(speech-timeout)";
                case 7: return "没有识别结果";
                case 8: return "识别器忙";
                case 9: return "权限不足";
                default: return "错误码" + code;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>RecognitionListener 的 JNI 代理（回调线程注意：Unity 的 AndroidJavaProxy 自动转主线程）</summary>
        private class ListenerProxy : AndroidJavaProxy
        {
            private readonly Action<string> _onResults;
            private readonly Action<string> _onPartial;
            private readonly Action<int> _onError;
            private readonly Action<float> _onRms;

            public ListenerProxy(Action<string> onResults, Action<string> onPartial,
                Action<int> onError, Action<float> onRms)
                : base("android.speech.RecognitionListener")
            {
                _onResults = onResults;
                _onPartial = onPartial;
                _onError = onError;
                _onRms = onRms;
            }

            // onResults(Bundle)：取 ArrayList<String> 的第一条
            private void onResults(AndroidJavaObject bundle)
            {
                var list = bundle.Call<AndroidJavaObject>("getStringArrayList", "results_recognition");
                if (list == null) { _onResults(string.Empty); return; }
                int n = list.Call<int>("size");
                if (n == 0) { _onResults(string.Empty); return; }
                _onResults(list.Call<string>("get", 0));
            }

            private void onPartialResults(AndroidJavaObject bundle)
            {
                var list = bundle.Call<AndroidJavaObject>("getStringArrayList", "results_recognition");
                if (list == null || list.Call<int>("size") == 0) return;
                _onPartial(list.Call<string>("get", 0));
            }

            private void onError(int code) => _onError(code);
            private void onRmsChanged(float rms) => _onRms(rms);
        }
#endif
    }
}
