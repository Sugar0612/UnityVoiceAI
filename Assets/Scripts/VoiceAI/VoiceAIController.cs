using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VoiceAI
{
    public enum VoiceAIState
    {
        Idle,        // 空闲
        Listening,   // 正在听（系统识别中）
        Thinking,    // 等待 DeepSeek 回复
        Speaking,    // AI 正在朗读回复
    }

    /// <summary>
    /// 语音 AI 总控：录音(系统识别) → DeepSeek → TTS 播放。
    /// Android 真机：系统 SpeechRecognizer 负责录音+识别，TextToSpeech 负责朗读。
    /// 使用：把本组件挂到场景任意物体，给按钮绑定 ToggleListening()；
    /// 或在菜单 Tools → VoiceAI → 创建演示 UI 一键生成。
    /// </summary>
    public class VoiceAIController : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        [Header("DeepSeek 配置")]
        [SerializeField] private DeepSeekSettings deepSeek = new DeepSeekSettings();

        [Header("UI 引用（可选）")]
        [Tooltip("状态提示文字，如 正在听/思考中/播放中")]
        [SerializeField] private Text statusText;
        [Tooltip("显示识别出的内容")]
        [SerializeField] private Text recognizedText;
        [Tooltip("显示 AI 回复")]
        [SerializeField] private Text replyText;

        [Header("交互")]
        [Tooltip("true=按住说话，false=点击开始/再点结束")]
        [SerializeField] private bool holdToTalk = false;
        [Tooltip("识别语言，中文为 zh-CN")]
        [SerializeField] private string sttLanguage = "zh-CN";

        public VoiceAIState State { get; private set; } = VoiceAIState.Idle;

        public event Action<VoiceAIState> OnStateChanged;
        public event Action<string> OnPartialText;
        public event Action<string> OnRecognizedText;
        public event Action<string> OnReplyText;
        public event Action<string> OnError;

        private AndroidSpeechRecognizer _recognizer;
        private AndroidTextToSpeech _tts;
        private bool _permissionGranted;
        // 防止同一帧内按钮 OnClick 与 IPointerClickHandler 重复触发切换
        private int _lastToggleFrame = -1;

        private void Awake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _recognizer = new AndroidSpeechRecognizer();
            _recognizer.OnReadyForSpeech += () => SetState(VoiceAIState.Listening);
            _recognizer.OnPartialResult += text =>
            {
                OnPartialText?.Invoke(text);
                SetText(recognizedText, "正在听: " + text);
            };
            _recognizer.OnResult += OnRecognized;
            _recognizer.OnError += OnRecognitionError;

            _tts = new AndroidTextToSpeech();
            _tts.OnReady += () => Debug.Log("[VoiceAI] TTS 已就绪");
            _tts.OnError += msg => { RaiseError(msg); SetState(VoiceAIState.Idle); };
            _tts.OnUtteranceCompleted += () => SetState(VoiceAIState.Idle);
            _tts.Initialize(sttLanguage);
#endif
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _recognizer?.Cancel();
            _recognizer?.Dispose();
            _recognizer = null;
            _tts?.Stop();
            _tts?.Dispose();
            _tts = null;
#endif
        }

        // ---------- 对外接口：按钮调用 ----------

        public void ToggleListening()
        {
            // 同一帧内重复调用（如按钮 OnClick 与指针点击同时触发）只执行一次
            if (Time.frameCount == _lastToggleFrame) return;
            _lastToggleFrame = Time.frameCount;

            switch (State)
            {
                case VoiceAIState.Idle: StartListening(); break;
                case VoiceAIState.Listening: StopListening(); break;
                case VoiceAIState.Speaking: StopSpeaking(); StartListening(); break;
                // Thinking 期间忽略点击
            }
        }

        public void StartListening()
        {
            if (State != VoiceAIState.Idle) return;
            StartCoroutine(TryStartListening());
        }

        public void StopListening()
        {
            if (State != VoiceAIState.Listening) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            _recognizer?.StopListening();
#else
            SetState(VoiceAIState.Idle);
#endif
        }

        public void StopSpeaking()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _tts?.Stop();
#endif
            SetState(VoiceAIState.Idle);
        }

        // ---------- 按住说话 ----------

        public void OnPointerDown(PointerEventData eventData)
        {
            if (holdToTalk) StartListening();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (holdToTalk) StopListening();
        }

        /// <summary>
        /// 点击（非按住模式）自动切换开始/停止，无需给按钮绑定任何事件。
        /// 前提：本组件挂在按钮自身，或按钮的某个祖先物体上（UI 事件会向上冒泡）。
        /// </summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (!holdToTalk) ToggleListening();
        }

        // ---------- 内部流程 ----------

        private IEnumerator TryStartListening()
        {
            SetState(VoiceAIState.Listening);

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_permissionGranted)
            {
                yield return RequestMicrophonePermission();
                if (!_permissionGranted)
                {
                    RaiseError("没有麦克风权限，无法开始语音识别。请在系统设置中允许。");
                    SetState(VoiceAIState.Idle);
                    yield break;
                }
            }

            if (_recognizer == null || !_recognizer.IsAvailable || !_recognizer.StartListening(sttLanguage))
            {
                RaiseError("无法启动语音识别：设备可能不支持，或系统识别服务不可用。");
                SetState(VoiceAIState.Idle);
                yield break;
            }
#else
            Debug.LogWarning("[VoiceAI] 系统语音识别仅支持 Android 真机，请在手机上测试。");
            RaiseError("系统语音识别仅支持 Android 真机，请打包到手机测试。");
            SetState(VoiceAIState.Idle);
#endif
            yield break;
        }

        private IEnumerator RequestMicrophonePermission()
        {
            // 已授权直接返回
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.RECORD_AUDIO"))
            {
                _permissionGranted = true;
                yield break;
            }

            bool granted = false;
            bool denied = false;

            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => granted = true;
            callbacks.PermissionDenied += _ => denied = true;
            callbacks.PermissionDeniedAndDontAskAgain += _ => denied = true;

            UnityEngine.Android.Permission.RequestUserPermission("android.permission.RECORD_AUDIO", callbacks);

            while (!granted && !denied) yield return null;

            _permissionGranted = granted;
        }

        private void OnRecognized(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                RaiseError("没有识别到内容，请再试一次。");
                SetState(VoiceAIState.Idle);
                return;
            }

            OnRecognizedText?.Invoke(text);
            SetText(recognizedText, "你说: " + text);
            SetText(replyText, "AI 思考中...");
            SetState(VoiceAIState.Thinking);

            StartCoroutine(DeepSeekClient.SendMessage(deepSeek, text, OnDeepSeekSuccess, OnDeepSeekError));
        }

        private void OnDeepSeekSuccess(string reply)
        {
            OnReplyText?.Invoke(reply);
            SetText(replyText, "AI: " + reply);
            SetState(VoiceAIState.Speaking);
            Speak(reply);
        }

        private void OnDeepSeekError(string error)
        {
            RaiseError(error);
            SetState(VoiceAIState.Idle);
        }

        private void Speak(string text)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_tts != null && _tts.IsReady)
            {
                if (_tts.Speak(text))
                {
                    // 安全兜底：如果系统没有回调播完事件，按语速估算时间后自动复位
                    StartCoroutine(SafetyResetAfterSpeaking(text));
                }
                else
                {
                    RaiseError("TTS 播放失败。");
                    SetState(VoiceAIState.Idle);
                }
            }
            else
            {
                RaiseError("TTS 尚未就绪，无法朗读。");
                SetState(VoiceAIState.Idle);
            }
#else
            Debug.Log("[VoiceAI] (编辑器模拟) AI 回复: " + text);
            SetState(VoiceAIState.Idle);
#endif
        }

        private IEnumerator SafetyResetAfterSpeaking(string reply)
        {
            float wait = 3f + reply.Length * 0.12f; // 粗略按语速估算
            yield return new WaitForSeconds(wait);
            if (State == VoiceAIState.Speaking) SetState(VoiceAIState.Idle);
        }

        private void OnRecognitionError(int code)
        {
            SetState(VoiceAIState.Idle);
            RaiseError("语音识别失败: " + AndroidSpeechRecognizer.DescribeError(code));
        }

        // ---------- UI ----------

        private void SetState(VoiceAIState state)
        {
            if (State == state) return;
            State = state;
            OnStateChanged?.Invoke(state);
            UpdateStatusText();
        }

        private void UpdateStatusText()
        {
            string text;
            switch (State)
            {
                case VoiceAIState.Idle: text = "点击按钮开始说话"; break;
                case VoiceAIState.Listening: text = "正在听，请说话..."; break;
                case VoiceAIState.Thinking: text = "AI 思考中..."; break;
                case VoiceAIState.Speaking: text = "正在朗读回复..."; break;
                default: text = ""; break;
            }
            SetText(statusText, text);
        }

        private static void SetText(Text target, string value)
        {
            if (target != null) target.text = value;
        }

        private void RaiseError(string message)
        {
            Debug.LogWarning("[VoiceAI] " + message);
            OnError?.Invoke(message);
            if (statusText != null) statusText.text = "⚠ " + message;
        }
    }
}
