using System;
using UnityEngine;

namespace VoiceAI
{
    /// <summary>
    /// 封装 Android 系统语音识别 (android.speech.SpeechRecognizer)。
    /// 识别由系统语音服务完成（自带录音），通过 RecognitionListener 回调返回结果。
    /// 仅在 Android 真机可用。识别语言默认中文 zh-CN。
    /// </summary>
    public class AndroidSpeechRecognizer : IDisposable
    {
        private const string ResultRecognition = "results_recognition";

        private AndroidJavaObject _recognizer;
        private AndroidJavaObject _activity;
        private Listener _listener;
        private bool _isListening;

        public bool IsListening => _isListening;

        /// <summary>开始收音前的准备完成（此时可以提示用户说话）</summary>
        public event Action OnReadyForSpeech;
        /// <summary>检测到开始说话</summary>
        public event Action OnBeginningOfSpeech;
        /// <summary>检测到说话结束</summary>
        public event Action OnEndOfSpeech;
        /// <summary>实时(部分)识别结果，用于展示"正在听"效果</summary>
        public event Action<string> OnPartialResult;
        /// <summary>最终识别结果（可能为 null）</summary>
        public event Action<string> OnResult;
        /// <summary>识别错误码（用 DescribeError 翻译）</summary>
        public event Action<int> OnError;

        public AndroidSpeechRecognizer()
        {
            try
            {
                _activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                    .GetStatic<AndroidJavaObject>("currentActivity");
                var cls = new AndroidJavaClass("android.speech.SpeechRecognizer");
                if (cls.CallStatic<bool>("isRecognitionAvailable", _activity))
                {
                    _recognizer = cls.CallStatic<AndroidJavaObject>("createSpeechRecognizer", _activity);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] 创建 SpeechRecognizer 失败: " + e.Message);
                _recognizer = null;
            }
        }

        public bool IsAvailable => _recognizer != null;

        /// <summary>开始监听语音。返回 false 表示启动失败。</summary>
        public bool StartListening(string language = "zh-CN")
        {
            if (_recognizer == null) return false;

            try
            {
                _listener = new Listener(this);
                _recognizer.Call("setRecognitionListener", _listener);

                var intentClass = new AndroidJavaClass("android.speech.RecognizerIntent");
                var intent = new AndroidJavaObject(
                    "android.content.Intent",
                    intentClass.GetStatic<string>("ACTION_RECOGNIZE_SPEECH"));

                intent.Call<AndroidJavaObject>("putExtra",
                    intentClass.GetStatic<string>("EXTRA_LANGUAGE_MODEL"),
                    intentClass.GetStatic<string>("LANGUAGE_MODEL_FREE_FORM"));
                intent.Call<AndroidJavaObject>("putExtra",
                    intentClass.GetStatic<string>("EXTRA_LANGUAGE"), language);
                intent.Call<AndroidJavaObject>("putExtra",
                    intentClass.GetStatic<string>("EXTRA_PARTIAL_RESULTS"), true);
                intent.Call<AndroidJavaObject>("putExtra",
                    intentClass.GetStatic<string>("EXTRA_MAX_RESULTS"), 5);
                intent.Call<AndroidJavaObject>("putExtra",
                    intentClass.GetStatic<string>("EXTRA_CALLING_PACKAGE"),
                    _activity.Call<string>("getPackageName"));
                // 静音约 2.5 秒自动结束录音
                intent.Call<AndroidJavaObject>("putExtra",
                    intentClass.GetStatic<string>("EXTRA_SPEECH_INPUT_COMPLETE_SILENCE_LENGTH_MILLIS"), 2500L);
                intent.Call<AndroidJavaObject>("putExtra",
                    intentClass.GetStatic<string>("EXTRA_SPEECH_INPUT_POSSIBLY_COMPLETE_SILENCE_LENGTH_MILLIS"), 1500L);

                _recognizer.Call("startListening", intent);
                _isListening = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] startListening 失败: " + e.Message);
                _isListening = false;
                return false;
            }
        }

        /// <summary>结束录音，稍后 onResults 会返回最终结果</summary>
        public void StopListening()
        {
            if (_recognizer != null && _isListening)
            {
                try { _recognizer.Call("stopListening"); }
                catch (Exception e) { Debug.LogWarning("[VoiceAI] stopListening: " + e.Message); }
            }
        }

        public void Cancel()
        {
            _isListening = false;
            if (_recognizer == null) return;
            try { _recognizer.Call("cancel"); }
            catch (Exception e) { Debug.LogWarning("[VoiceAI] cancel: " + e.Message); }
        }

        public void Dispose()
        {
            try
            {
                if (_recognizer != null)
                {
                    if (_isListening) _recognizer.Call("cancel");
                    _recognizer.Call("destroy");
                }
            }
            catch (Exception e) { Debug.LogWarning("[VoiceAI] destroy: " + e.Message); }
            _recognizer = null;
            _listener = null;
            _isListening = false;
        }

        /// <summary>把识别错误码翻译成可读信息</summary>
        public static string DescribeError(int code)
        {
            switch (code)
            {
                case 1: return "网络超时";
                case 2: return "网络错误";
                case 3: return "录音音频错误";
                case 4: return "服务端错误";
                case 5: return "客户端错误";
                case 6: return "没有说话(超时)";
                case 7: return "没有匹配到内容";
                case 8: return "识别服务忙，请稍后再试";
                case 9: return "缺少麦克风权限";
                case 10: return "识别服务断开";
                case 11: return "语言不支持";
                case 12: return "语言数据不可用";
                case 13: return "请求过于频繁";
                default: return "未知错误(" + code + ")";
            }
        }

        // ---------- Java 回调代理 ----------
        private class Listener : AndroidJavaProxy
        {
            private readonly AndroidSpeechRecognizer _owner;

            public Listener(AndroidSpeechRecognizer owner)
                : base("android.speech.RecognitionListener")
            {
                _owner = owner;
            }

            public void onReadyForSpeech(AndroidJavaObject bundle)
            {
                _owner.OnReadyForSpeech?.Invoke();
            }

            public void onBeginningOfSpeech()
            {
                _owner.OnBeginningOfSpeech?.Invoke();
            }

            public void onRmsChanged(float rmsdB) { }

            public void onBufferReceived(byte[] buffer) { }

            public void onEndOfSpeech()
            {
                _owner.OnEndOfSpeech?.Invoke();
            }

            public void onError(int error)
            {
                _owner._isListening = false;
                _owner.OnError?.Invoke(error);
            }

            public void onResults(AndroidJavaObject bundle)
            {
                _owner._isListening = false;
                string text = _owner.ExtractBestResult(bundle);
                _owner.OnResult?.Invoke(text);
            }

            public void onPartialResults(AndroidJavaObject bundle)
            {
                string text = _owner.ExtractBestResult(bundle);
                if (!string.IsNullOrEmpty(text)) _owner.OnPartialResult?.Invoke(text);
            }

            public void onEvent(int eventType, AndroidJavaObject bundle) { }
        }

        private string ExtractBestResult(AndroidJavaObject bundle)
        {
            if (bundle == null) return null;
            try
            {
                var list = bundle.Call<AndroidJavaObject>("getStringArrayList", ResultRecognition);
                if (list == null) return null;
                int size = list.Call<int>("size");
                if (size > 0)
                {
                    string best = list.Call<string>("get", 0);
                    return string.IsNullOrEmpty(best) ? null : best;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] 解析识别结果失败: " + e.Message);
            }
            return null;
        }
    }
}
