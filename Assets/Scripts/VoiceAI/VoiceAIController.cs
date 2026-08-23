using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Networking;
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

        [Header("识别备用引擎(STT 降级)")]
        [Tooltip("主识别引擎失败时自动切换（不填则不用）。示例：讯飞流式听写 provider=2 + iflyDomain=iat")]
        [SerializeField] private SttSettings sttFallback = null;

        [Header("语音合成(TTS) 配置")]
        [Tooltip("true=用云端TTS(MiniMax，可自定义音色)；false=用系统TTS(免费)")]
        [SerializeField] private bool useCloudTts = false;
        [SerializeField] private TtsSettings tts = new TtsSettings();

        [Header("保活与自愈（防克隆音色被删除）")]
        [Tooltip("打开App时若超过N天未合成，自动静默合成一次保活（刷新MiniMax的7天计时）")]
        [SerializeField] private bool autoKeepAlive = true;
        [Tooltip("保活间隔天数，必须小于 MiniMax 的 7 天删除线，建议 5")]
        [SerializeField] private float keepAliveDays = 5f;
        [Tooltip("检测到声音被删除时，自动用内置样本重新克隆并重试")]
        [SerializeField] private bool autoReclone = true;

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
        private AudioSource _audioSource;
        private Text _btnLabel; // 录音按钮上的文字（状态联动）
        // 保活与自愈状态
        private const string LastTtsTimeKey = "voiceai_last_tts_time";
        private bool _recloneDone;   // 本次会话是否已尝试过自动重克隆

        private void Awake()
        {
            Debug.Log("[VoiceAI] Awake，holdToTalk=" + holdToTalk + "，sttModel=" + stt.model + "，useCloudTts=" + useCloudTts);

            // 云端 TTS 播放需要 AudioSource（自动挂载，无需手动配置）
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
            {
                _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
            }
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
                    _btnLabel = btn.GetComponentInChildren<Text>();
                    Debug.Log("[VoiceAI] 已自动绑定按钮: " + btn.name + " → ToggleListening");
                }
                else
                {
                    Debug.LogWarning("[VoiceAI] 未找到可绑定的 Button（请把本组件挂在 Canvas 上，按钮作为其子物体）");
                }
            }

            // 声音保活：闲置超过阈值则静默合成一次，防止 MiniMax 7 天删除克隆音色
            StartCoroutine(TryKeepAlive());

            // 文字显示优化：自动换行 + 溢出可见 + 高度自适应
            ConfigureText(statusText, TextAnchor.MiddleCenter);
            ConfigureText(recognizedText, TextAnchor.UpperCenter);
            ConfigureText(replyText, TextAnchor.UpperCenter);
        }

        /// <summary>让 Text 自动换行、不截断、高度随内容增长（向下延伸）</summary>
        private static void ConfigureText(Text t, TextAnchor align)
        {
            if (t == null) return;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.alignment = align;

            var fitter = t.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = t.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var rt = t.rectTransform;
            rt.pivot = new Vector2(0.5f, 1f); // 顶部锚定，向下生长，避免文字被裁切
        }

        // ---------- 声音保活与自愈 ----------

        private const string SamplePath = "/VoiceSample/clone_sample.mp3";

        private void SaveTtsTime()
        {
            PlayerPrefs.SetString(LastTtsTimeKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            PlayerPrefs.Save();
        }

        private long GetLastTtsTime()
        {
            string s = PlayerPrefs.GetString(LastTtsTimeKey, "0");
            return long.TryParse(s, out long v) ? v : 0;
        }

        /// <summary>静默保活：距上次合成超过阈值时，合成一个极短文本刷新 MiniMax 的 7 天计时</summary>
        private IEnumerator TryKeepAlive()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            yield return null; // 等一帧，让 TTS 配置就绪
            if (!useCloudTts || !autoKeepAlive) yield break;
            if (string.IsNullOrWhiteSpace(tts.voiceId)) yield break;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long last = GetLastTtsTime();
            if (now - last < (long)(keepAliveDays * 86400)) yield break;

            Debug.Log("[VoiceAI] 触发声音保活（闲置超过 " + keepAliveDays + " 天）");
            yield return CloudTtsClient.Synthesize(tts, "。", _ =>
            {
                SaveTtsTime();
                Debug.Log("[VoiceAI] 保活成功，计时已刷新");
            }, err =>
            {
                Debug.LogWarning("[VoiceAI] 保活失败: " + err);
                // 声音可能已被删除 → 自动重克隆
                if (autoReclone && !_recloneDone && CloudTtsClient.IsVoiceMissingError(err))
                    StartCoroutine(RecloneAndRetry(null));
            });
#endif
            yield break;
        }

        /// <summary>
        /// 自动恢复被删除的克隆音色：读取内置样本 → 上传 → 克隆（复用原 voice_id）→ 可选重试原文本。
        /// retryText 为 null 时只做保活合成。
        /// </summary>
        private IEnumerator RecloneAndRetry(string retryText)
        {
            _recloneDone = true;
            SetText(statusText, "检测到声音被清理，正在自动恢复...");

            // 1) 读取内置样本（StreamingAssets；Android 打包时打进 APK）
            byte[] sample = null;
            string path = Application.streamingAssetsPath + SamplePath;
            using (var req = UnityWebRequest.Get(path))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success && req.downloadHandler != null)
                    sample = req.downloadHandler.data;
            }

            if (sample == null || sample.Length == 0)
            {
                SetText(replyText, "⚠ 声音已过期且未内置恢复样本（请把样本放到 StreamingAssets/VoiceSample/clone_sample.mp3）");
                RaiseError("声音恢复失败：缺少内置样本文件");
                SetState(VoiceAIState.Idle);
                yield break;
            }

            // 2) 上传样本
            long fileId = 0;
            bool failed = false;
            string failMsg = "";
            yield return CloudTtsClient.UploadCloneSample(tts, sample, "clone_sample.mp3",
                id => fileId = id,
                err => { failed = true; failMsg = err; });

            if (failed || fileId <= 0)
            {
                SetText(replyText, "⚠ 声音恢复失败: " + failMsg);
                RaiseError("声音恢复失败: " + failMsg);
                SetState(VoiceAIState.Idle);
                yield break;
            }

            // 3) 克隆（复用同一个 voice_id，App 配置无需改动）
            failed = false;
            failMsg = "";
            yield return CloudTtsClient.CloneVoice(tts, fileId, tts.voiceId.Trim(),
                () => { },
                err => { failed = true; failMsg = err; });

            if (failed)
            {
                SetText(replyText, "⚠ 声音恢复失败: " + failMsg);
                RaiseError("声音恢复失败: " + failMsg);
                SetState(VoiceAIState.Idle);
                yield break;
            }

            Debug.Log("[VoiceAI] 声音已重新克隆: " + tts.voiceId.Trim());
            SetText(statusText, "声音已恢复，继续合成...");

            if (!string.IsNullOrEmpty(retryText))
            {
                Speak(retryText);   // 重试原来的合成
            }
            else
            {
                // 保活场景：克隆后再合成一次极短文本刷新计时
                yield return CloudTtsClient.Synthesize(tts, "。", _ =>
                {
                    SaveTtsTime();
                    SetState(VoiceAIState.Idle);
                }, err =>
                {
                    SetText(replyText, "⚠ 声音恢复后保活失败: " + err);
                    SetState(VoiceAIState.Idle);
                });
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

            // 主识别引擎；失败时自动尝试备用引擎（sttFallback）
            RunStt(stt, wav);
        }

        private void StopRecordingInternal()
        {
            if (_recDevice != null && _recClip != null && Microphone.IsRecording(_recDevice))
                Microphone.End(_recDevice);
        }

        /// <summary> 录音监控：更新秒数、静音自动停止、最长时长限制 </summary>
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

        private void RunStt(SttSettings settings, byte[] wav)
        {
            StartCoroutine(BuildSttRoutine(settings, wav, OnSttSuccess, err => TryFallbackStt(wav, err)));
        }

        /// <summary>按 provider 构建识别协程：0=OpenAI兼容(硅基流动等)，2=讯飞</summary>
        private static IEnumerator BuildSttRoutine(SttSettings settings, byte[] wav,
            Action<string> onOk, Action<string> onFail)
        {
            switch (settings.provider)
            {
                case 2: return IflytekSttClient.Transcribe(settings, wav, onOk, onFail);
                default: return CloudSttClient.Transcribe(settings, wav, onOk, onFail);
            }
        }

        /// <summary>主引擎失败 → 尝试备用引擎；备用也没配则直接报错</summary>
        private void TryFallbackStt(byte[] wav, string primaryError)
        {
            if (sttFallback != null && IsSttConfigured(sttFallback))
            {
                Debug.LogWarning("[VoiceAI] 主识别失败(" + primaryError + ")，切换备用引擎");
                SetText(statusText, "切换备用识别...");
                StartCoroutine(BuildSttRoutine(sttFallback, wav, OnSttSuccess, OnSttError));
            }
            else
            {
                OnSttError(primaryError);
            }
        }

        private static bool IsSttConfigured(SttSettings settings)
        {
            if (settings == null) return false;
            switch (settings.provider)
            {
                case 2:
                    return !string.IsNullOrWhiteSpace(settings.iflyAppId)
                        && !string.IsNullOrWhiteSpace(settings.iflyApiKey)
                        && !string.IsNullOrWhiteSpace(settings.iflyApiSecret);
                default:
                    return !string.IsNullOrWhiteSpace(settings.apiKey);
            }
        }

        private void OnSttError(string err)
        {
            SetText(replyText, "⚠ 语音识别失败: " + err);
            RaiseError("语音识别失败: " + err);
            SetState(VoiceAIState.Idle);
        }

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
            SetText(replyText, "");
            SetState(VoiceAIState.Thinking);

            // 流式回复：文字逐个出现（SSE），生成完再进入朗读
            var sb = new StringBuilder();
            StartCoroutine(DeepSeekClient.SendMessageStream(deepSeek, text,
                delta =>
                {
                    sb.Append(delta);
                    SetText(replyText, sb.ToString());
                },
                full => OnDeepSeekSuccess(full),
                OnDeepSeekError));
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
            // 方案一（默认）：云端 TTS（MiniMax 声音复刻，可自定义音色）
            if (useCloudTts)
            {
                if (_audioSource == null)
                {
                    RaiseError("AudioSource 不可用");
                    SetState(VoiceAIState.Idle);
                    return;
                }
                SetText(statusText, "正在合成语音...");
                StartCoroutine(CloudTtsClient.Synthesize(tts, text, clip =>
                {
                    SaveTtsTime(); // 记录使用时间，刷新保活计时
                    _audioSource.clip = clip;
                    _audioSource.Play();
                    SetText(statusText, "正在朗读回复...");
                    StartCoroutine(WaitPlaybackEnd());
                }, err =>
                {
                    // 声音被 MiniMax 删除（7天未用）→ 自动重克隆并重试
                    if (autoReclone && !_recloneDone && CloudTtsClient.IsVoiceMissingError(err))
                    {
                        Debug.LogWarning("[VoiceAI] 检测到声音缺失，尝试自动恢复: " + err);
                        StartCoroutine(RecloneAndRetry(text));
                        return;
                    }
                    SetText(replyText, "⚠ " + err);
                    RaiseError(err);
                    SetState(VoiceAIState.Idle);
                }));
                return;
            }

            // 方案二（备用）：Android 系统 TTS
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
            Debug.LogWarning("[VoiceAI] 编辑器模式下系统TTS不可用，请改用云端TTS（useCloudTts=true）");
            SetState(VoiceAIState.Idle);
#endif
        }

        /// <summary>等待云端 TTS 音频播放完毕，然后复位状态</summary>
        private IEnumerator WaitPlaybackEnd()
        {
            while (_audioSource != null && _audioSource.isPlaying)
                yield return null;
            SetState(VoiceAIState.Idle);
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
            UpdateButtonLabel();
        }

        /// <summary>按钮文字随状态变化（点击说话 ↔ 停止说话 等）</summary>
        private void UpdateButtonLabel()
        {
            if (_btnLabel == null) return;

            if (holdToTalk)
            {
                _btnLabel.text = State == VoiceAIState.Listening ? "松开结束" : "按住说话";
                return;
            }

            switch (State)
            {
                case VoiceAIState.Listening: _btnLabel.text = "停止说话"; break;
                case VoiceAIState.Thinking: _btnLabel.text = "思考中..."; break;
                case VoiceAIState.Speaking: _btnLabel.text = "点击打断"; break;
                default: _btnLabel.text = "点击说话"; break;
            }
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
