using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace VoiceAI
{
    public enum VoiceAIState
    {
        Idle,        // 空闲
        Listening,   // 正在录音（麦克风）
        Thinking,    // 云端识别 + 等待 DeepSeek 回复
        Speaking,    // AI 正在朗读回复
    }

    /// <summary>
    /// 语音 AI 总控：麦克风录音 → 云端语音识别(STT) → DeepSeek → 系统TTS朗读。
    /// Android 方案说明：
    ///   - STT 走云端 OpenAI 兼容接口（国行手机通常没有系统语音识别服务，系统 SpeechRecognizer 不可用）
    ///   - TTS 走系统 TextToSpeech（Oplus/Google 引擎均可）
    /// 使用：把本组件挂到场景任意物体；点按钮（或按住）说话即可，无需手动绑定事件。
    /// </summary>
    public class VoiceAIController : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        [Header("DeepSeek 配置")]
        [SerializeField] private DeepSeekSettings deepSeek = new DeepSeekSettings();

        [Header("语音识别(STT) 配置")]
        [SerializeField] private SttSettings stt = new SttSettings();

        [Header("UI 引用（可选）")]
        [Tooltip("状态提示文字，如 正在录音/思考中/播放中")]
        [SerializeField] private Text statusText;
        [Tooltip("显示识别出的内容")]
        [SerializeField] private Text recognizedText;
        [Tooltip("显示 AI 回复")]
        [SerializeField] private Text replyText;

        [Header("交互")]
        [Tooltip("true=按住说话，false=点击开始/再点结束")]
        [SerializeField] private bool holdToTalk = false;

        [Header("录音")]
        [Tooltip("静音多少秒后自动结束录音")]
        [SerializeField] private float silenceAutoStopSeconds = 2.5f;
        [Tooltip("单次录音最长秒数")]
        [SerializeField] private float maxRecordSeconds = 25f;

        public VoiceAIState State { get; private set; } = VoiceAIState.Idle;

        public event Action<VoiceAIState> OnStateChanged;
        public event Action<string> OnPartialText;
        public event Action<string> OnRecognizedText;
        public event Action<string> OnReplyText;
        public event Action<string> OnError;

        private AndroidTextToSpeech _tts;
        private bool _permissionGranted;
        // 防止同一帧内按钮 OnClick 与 IPointerClickHandler 重复触发切换
        private int _lastToggleFrame = -1;

        // 录音状态
        private AudioClip _recClip;
        private string _recDevice;
        private float _recStartTime;
        private float[] _levelBuf;
        private int _silentCount;
        private bool _processing;

        private void Awake()
        {
            Debug.Log("[VoiceAI] Awake，holdToTalk=" + holdToTalk + "，sttModel=" + stt.model);
#if UNITY_ANDROID && !UNITY_EDITOR
            _tts = new AndroidTextToSpeech();
            _tts.OnReady += () => Debug.Log("[VoiceAI] TTS 已就绪");
            _tts.OnError += msg => { RaiseError(msg); SetState(VoiceAIState.Idle); };
            _tts.OnUtteranceCompleted += () => SetState(VoiceAIState.Idle);
            _tts.Initialize("zh-CN");
#endif
        }

        private void Start()
        {
            EnsureEventSystem();

            // 自动绑定场景中的按钮（运行时绑定，无需在 Inspector 手动配置 OnClick）
            // 与按钮持久化绑定共存时，ToggleListening 的同帧去重会避免重复触发
            if (!holdToTalk)
            {
                var btn = GetComponentInChildren<Button>();
                if (btn != null)
                {
                    btn.onClick.AddListener(ToggleListening);
                    Debug.Log("[VoiceAI] 已自动绑定按钮: " + btn.name + " → ToggleListening");
                }
                else
                {
                    Debug.LogWarning("[VoiceAI] 未找到可绑定的 Button（请把本组件挂在 Canvas 上，按钮作为其子物体）");
                }
            }
        }

        /// <summary>确保场景里有可用的 EventSystem，否则自动创建一个（UI 事件的前提）</summary>
        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                Debug.Log("[VoiceAI] 事件系统正常: " + EventSystem.current.name +
                    " (" + (EventSystem.current.isActiveAndEnabled ? "已启用" : "未启用") + ")");
                return;
            }

            Debug.LogWarning("[VoiceAI] 场景中没有 EventSystem，正在自动创建...");
            var go = new GameObject("EventSystem", typeof(EventSystem));
            go.AddComponent<InputSystemUIInputModule>();
        }

        private void OnDestroy()
        {
            StopRecordingInternal();
#if UNITY_ANDROID && !UNITY_EDITOR
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
            StopRecordingAndProcess();
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
            Debug.Log("[VoiceAI] 按下按钮，holdToTalk=" + holdToTalk);
            if (holdToTalk) StartListening();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Debug.Log("[VoiceAI] 松开按钮，holdToTalk=" + holdToTalk);
            if (holdToTalk) StopListening();
        }

        /// <summary>点击（非按住模式）自动切换开始/停止，无需给按钮绑定任何事件</summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            Debug.Log("[VoiceAI] 点击按钮，holdToTalk=" + holdToTalk);
            if (!holdToTalk) ToggleListening();
        }

        // ---------- 录音流程 ----------

        private IEnumerator TryStartListening()
        {
            SetState(VoiceAIState.Listening);

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_permissionGranted)
            {
                yield return RequestMicrophonePermission();
                if (!_permissionGranted)
                {
                    RaiseError("没有麦克风权限，无法开始录音。请在系统设置中允许。");
                    SetState(VoiceAIState.Idle);
                    yield break;
                }
            }
#endif

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                RaiseError("未检测到麦克风设备");
                SetState(VoiceAIState.Idle);
                yield break;
            }

            StartRecording();
            yield break;
        }

        private void StartRecording()
        {
            _processing = false;
            _silentCount = 0;
            _recDevice = Microphone.devices[0];
            _recClip = Microphone.Start(_recDevice, false, (int)maxRecordSeconds + 5, 16000);
            _recStartTime = Time.unscaledTime;
            Debug.Log("[VoiceAI] 开始录音: " + _recDevice);
            SetText(statusText, "正在录音 0.0s（说完了再点一次）");
            StartCoroutine(RecordingMonitor());
        }

        private void StopRecordingAndProcess()
        {
            if (_processing || _recClip == null) return;
            _processing = true;

            float elapsed = Time.unscaledTime - _recStartTime;
            StopRecordingInternal();

            if (elapsed < 0.7f)
            {
                RaiseError("录音太短，请再说一次");
                SetState(VoiceAIState.Idle);
                return;
            }

            SetText(statusText, "正在识别...");
            SetState(VoiceAIState.Thinking);

            byte[] wav = WavUtility.ToWav16kMono(_recClip);
            _recClip = null;

            StartCoroutine(CloudSttClient.Transcribe(stt, wav, OnSttSuccess, err =>
            {
                SetText(replyText, "⚠ 语音识别失败: " + err);
                RaiseError("语音识别失败: " + err);
                SetState(VoiceAIState.Idle);
            }));
        }

        private void StopRecordingInternal()
        {
            if (_recDevice != null && _recClip != null && Microphone.IsRecording(_recDevice))
                Microphone.End(_recDevice);
        }

        /// <summary>录音监控：更新秒数、静音自动停止、最长时长限制</summary>
        private IEnumerator RecordingMonitor()
        {
            var wait = new WaitForSeconds(0.25f);
            while (State == VoiceAIState.Listening && _recClip != null)
            {
                yield return wait;
                if (_processing) yield break;

                float elapsed = Time.unscaledTime - _recStartTime;
                if (!Microphone.IsRecording(_recDevice))
                {
                    StopRecordingAndProcess();
                    yield break;
                }

                SetText(statusText, "正在录音 " + elapsed.ToString("0.0") + "s（说完了再点一次）");

                // 静音检测（读取最近 0.25 秒的峰值）
                if (_levelBuf == null || _levelBuf.Length != _recClip.samples)
                    _levelBuf = new float[_recClip.samples];
                _recClip.GetData(_levelBuf, 0);

                int pos = Microphone.GetPosition(_recDevice);
                int win = 4000; // 0.25s @16kHz
                float peak = 0f;
                for (int i = 0; i < win; i++)
                {
                    int idx = pos - win + i;
                    if (idx < 0) idx += _recClip.samples;
                    float s = _levelBuf[idx];
                    if (s < 0) s = -s;
                    if (s > peak) peak = s;
                }

                if (peak < 0.02f)
                {
                    if (elapsed > 1.2f && ++_silentCount >= (int)(silenceAutoStopSeconds / 0.25f))
                    {
                        Debug.Log("[VoiceAI] 检测到静音，自动结束录音");
                        StopRecordingAndProcess();
                        yield break;
                    }
                }
                else
                {
                    _silentCount = 0;
                }

                if (elapsed >= maxRecordSeconds)
                {
                    Debug.Log("[VoiceAI] 达到最长录音时长，自动结束");
                    StopRecordingAndProcess();
                    yield break;
                }
            }
        }

        // ---------- 识别 → DeepSeek → TTS ----------

        private void OnSttSuccess(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                RaiseError("没有识别到内容，请再试一次。");
                SetState(VoiceAIState.Idle);
                return;
            }

            Debug.Log("[VoiceAI] 识别结果: " + text);
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
            // 错误同时写入绿色回复框，避免界面停留在"AI 思考中..."造成假卡死
            SetText(replyText, "⚠ " + error);
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

        // ---------- 权限 ----------

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
                case VoiceAIState.Listening: text = "正在录音..."; break;
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
