using System;
using UnityEngine;

namespace VoiceAI
{
    /// <summary>
    /// 封装 Android 系统文字转语音 (android.speech.tts.TextToSpeech)。
    /// 发音直接通过手机扬声器播放，无需 AudioSource。
    /// 仅在 Android 真机可用。
    /// </summary>
    public class AndroidTextToSpeech : IDisposable
    {
        private const int Success = 0;

        private AndroidJavaObject _tts;
        private AndroidJavaObject _activity;
        // Java 回调代理需要被 C# 强引用，防止被 GC 后回调丢失
        private AndroidJavaProxy _initListener;
        private AndroidJavaProxy _utteranceListener;
        private bool _ready;

        /// <summary>初始化完成且中文语音可用</summary>
        public event Action OnReady;
        /// <summary>初始化失败/播放失败的错误信息</summary>
        public event Action<string> OnError;
        /// <summary>一句话播放完成</summary>
        public event Action OnUtteranceCompleted;

        public bool IsReady => _ready;

        /// <summary>初始化 TTS。语言参数目前仅用于内部记录，固定使用简体中文。</summary>
        public void Initialize(string languageTag = "zh-CN")
        {
            try
            {
                _activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                    .GetStatic<AndroidJavaObject>("currentActivity");

                _initListener = new OnInitListener(status =>
                {
                    if (status != Success)
                    {
                        _ready = false;
                        OnError?.Invoke("TTS 初始化失败（status=" + status + "），请检查系统 TTS 服务是否可用");
                        return;
                    }

                    try
                    {
                        var locale = new AndroidJavaClass("java.util.Locale")
                            .GetStatic<AndroidJavaObject>("SIMPLIFIED_CHINESE");
                        int langResult = _tts.Call<int>("setLanguage", locale);

                        if (langResult < 0) // LANG_MISSING_DATA(-1) / LANG_NOT_SUPPORTED(-2)
                        {
                            _ready = false;
                            OnError?.Invoke("系统缺少中文语音包，请在 设置→系统→语言与输入法→文字转语音 中安装中文语音（推荐 Google TTS）");
                            return;
                        }

                        // 监听一句话播完（旧接口，但通用可用）
                        try
                        {
                            _utteranceListener = new UtteranceCompletedListener(_ => OnUtteranceCompleted?.Invoke());
                            _tts.Call("setOnUtteranceCompletedListener", _utteranceListener);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[VoiceAI] 设置播完回调失败(不影响播放): " + e.Message);
                        }

                        _ready = true;
                        OnReady?.Invoke();
                    }
                    catch (Exception e)
                    {
                        _ready = false;
                        OnError?.Invoke("TTS 语言设置失败: " + e.Message);
                    }
                });

                _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", _activity, _initListener);
            }
            catch (Exception e)
            {
                _ready = false;
                OnError?.Invoke("创建 TTS 失败: " + e.Message);
            }
        }

        /// <summary>朗读一段文字。返回 false 表示播放未启动。</summary>
        public bool Speak(string text)
        {
            if (!_ready || _tts == null || string.IsNullOrEmpty(text)) return false;
            try
            {
                string utteranceId = Guid.NewGuid().ToString("N");
                // speak(CharSequence text, int QUEUE_FLUSH, Bundle params, String utteranceId)
                int result = _tts.Call<int>("speak", text, 0, null, utteranceId);
                return result == Success;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] TTS speak 失败: " + e.Message);
                return false;
            }
        }

        /// <summary>停止当前朗读</summary>
        public void Stop()
        {
            if (_tts == null) return;
            try { _tts.Call("stop"); }
            catch (Exception e) { Debug.LogWarning("[VoiceAI] TTS stop: " + e.Message); }
        }

        public void Dispose()
        {
            try { if (_tts != null) _tts.Call("shutdown"); }
            catch (Exception e) { Debug.LogWarning("[VoiceAI] TTS shutdown: " + e.Message); }
            _tts = null;
            _initListener = null;
            _utteranceListener = null;
            _ready = false;
        }

        // ---------- Java 回调代理 ----------
        private class OnInitListener : AndroidJavaProxy
        {
            private readonly Action<int> _onInit;
            public OnInitListener(Action<int> onInit)
                : base("android.speech.tts.TextToSpeech$OnInitListener")
            {
                _onInit = onInit;
            }
            public void onInit(int status) => _onInit?.Invoke(status);
        }

        private class UtteranceCompletedListener : AndroidJavaProxy
        {
            private readonly Action<string> _onCompleted;
            public UtteranceCompletedListener(Action<string> onCompleted)
                : base("android.speech.tts.TextToSpeech$OnUtteranceCompletedListener")
            {
                _onCompleted = onCompleted;
            }
            public void onUtteranceCompleted(string utteranceId) => _onCompleted?.Invoke(utteranceId);
        }
    }
}
