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

        [Header("唤醒词")]
        [Tooltip("true=常驻麦克风监听唤醒词，说唤醒词自动开始录音（无需点击按钮）")]
        [SerializeField] private bool enableWakeWord = true;
        [Tooltip("唤醒词文本（仅用于界面提示；实际匹配由 StreamingAssets/KwsModel/keywords.txt 决定）")]
        [SerializeField] private string wakeWordText = "何夕月";
        [Tooltip("true=仅空闲时响应唤醒词，思考/朗读中忽略")]
        [SerializeField] private bool wakeOnlyWhenIdle = true;
        [Tooltip("唤醒词命中后的语音应答（如\"我在\"），留空则不应答直接开始录音")]
        [SerializeField] private string wakeAckText = "我在";

        [Header("录音")]
        [Tooltip("检测到静音多少秒后自动结束录音并上传识别")]
        [SerializeField] private float silenceConfirmSeconds = 1.2f;
        [Tooltip("静音判定阈值（最近0.3秒音频的RMS），底噪大的设备可调高")]
        [SerializeField] private float silenceRmsThreshold = 0.015f;
        [Tooltip("单次录音最长秒数")]
        [SerializeField] private float maxRecordSeconds = 25f;
        [Tooltip("开始说话判定阈值（RMS）：连续超过此值才算真正开口，静音结束判定才启动")]
        [SerializeField] private float speechStartRms = 0.025f;
        [Tooltip("唤醒后等待用户开口的最长秒数：超时未说话则取消本轮并恢复唤醒监听")]
        [SerializeField] private float noSpeechTimeoutSeconds = 12f;

        [Header("本地识别")]
        [Tooltip("优先使用 Android 系统自带识别（国行 ROM 内置引擎，无需联网 Key、速度快）；不可用或失败自动回退云端 STT")]
        [SerializeField] private bool preferSystemStt = true;

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
        private float[] _winBuf;      // 静音检测窗口缓冲（仅 0.3s 数据，避免整段 GetData）
        private int _silentCount;
        private bool _speechStarted;   // 是否已检测到用户真正开口（开口前静音判定不生效）
        private int _speechStartTicks; // 连续超开口阈值的计数值（防底噪毛刺误判）
        private float _speechStartElapsed; // 开口时刻（距录音开始的秒数，用于上传裁剪）
        private bool _processing;
        private AudioSource _audioSource;
        private AudioClip _wakeAckClip;  // 唤醒应答音频（云端TTS，合成一次缓存）
        private float[] _glowBuf;        // 回答态采样 TTS 播放音量的缓冲（喂给边缘流光）
        private const int GlowSampleCount = 1024;
        private Text _btnLabel; // 录音按钮上的文字（状态联动）
        private WakeWordDetector _wake; // 唤醒词检测器（运行时自动挂载）
        private EdgeGlowEffect _glow;   // 边缘光效（运行时自动挂载）
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

            // 文字显示优化：自动换行 + 溢出可见 + 高度自适应 + 排版（字号/行距/颜色）
            ConfigureText(statusText, TextAnchor.MiddleCenter, 26, 1.4f, new Color(1f, 1f, 1f, 0.85f));
            ConfigureText(recognizedText, TextAnchor.UpperCenter, 34, 1.5f, Color.white);
            ConfigureText(replyText, TextAnchor.UpperCenter, 34, 1.5f, new Color(0.92f, 0.98f, 1f));

            // 按钮美化：透明圆角 + 半透明描边（运行时生成圆角 Sprite，无需美术资源）
            PolishButton();

            // 唤醒词 + 边缘光效（无需点击按钮即可唤醒）
            InitWakeWordAndGlow();
            UpdateStatusText(); // 唤醒模式下立即刷新状态提示
        }

        /// <summary>让 Text 自动换行、不截断、高度随内容增长；并统一排版参数</summary>
        private static void ConfigureText(Text t, TextAnchor align,
            int fontSize = 30, float lineSpacing = 1.4f, Color? color = null)
        {
            if (t == null) return;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.alignment = align;
            t.fontSize = Mathf.Max(t.fontSize, 1); // 保留 Inspector 里的设置，仅确保合法
            if (color.HasValue) t.color = color.Value;

            // 排版：仅在 Inspector 没有专门设置过时应用默认（不覆盖已有布局意图）
            if (Mathf.Approximately(t.lineSpacing, 1f)) t.lineSpacing = lineSpacing;

            var fitter = t.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = t.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var rt = t.rectTransform;
            rt.pivot = new Vector2(0.5f, 1f); // 顶部锚定，向下生长，避免文字被裁切
        }

        /// <summary>
        /// 按钮运行时美化：透明圆角（程序生成圆角 Sprite）。
        /// 同时处理按钮下所有 Image（含 Background 等子物体——按钮视觉主体常是子 Image，
        /// 只改根 Image 会导致按钮看起来仍不透明）。文字不受影响。
        /// </summary>
        private void PolishButton()
        {
            var btn = GetComponentInChildren<Button>(true);
            if (btn == null) return;

            var sprite = MakeRoundedSprite(96, 96, 22);
            if (sprite == null) return;

            // 根 Image + 所有子 Image 统一换成半透明圆角皮肤
            // （不动 raycastTarget，保持原有可点击区域）
            var images = btn.GetComponentsInChildren<Image>(true);
            int polished = 0;
            foreach (var img in images)
            {
                if (img == null) continue;
                img.sprite = sprite;
                img.type = Image.Type.Sliced;   // 九宫格拉伸，圆角不随尺寸变形
                img.pixelsPerUnitMultiplier = 2f;
                img.color = new Color(1f, 1f, 1f, 0.18f); // 半透明底
                polished++;
            }

            // 按钮文字统一成浅色
            if (_btnLabel != null)
            {
                _btnLabel.color = new Color(1f, 1f, 1f, 0.95f);
                _btnLabel.fontSize = Mathf.Max(_btnLabel.fontSize, 30);
            }

            // 状态色过渡（按下/悬停由 Button 的 ColorTint 自动叠加在半透明底上）
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1.15f);
            colors.pressedColor = new Color(0.8f, 0.9f, 1f, 0.8f);
            colors.fadeDuration = 0.12f;
            btn.colors = colors;

            Debug.Log("[VoiceAI] 按钮已美化：透明圆角（处理了 " + polished + " 个 Image）");
        }

        /// <summary>程序生成圆角矩形 Sprite：中心 1f、边缘柔和衰减至 0，带 2px 软描边</summary>
        private static Sprite MakeRoundedSprite(int w, int h, int radius)
        {
            try
            {
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                var px = new Color[w * h];
                float r = radius;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // 到圆角矩形边缘的带符号距离（SDF）
                        float cx = Mathf.Max(Mathf.Abs(x - (w - 1) * 0.5f) - (w - 1) * 0.5f + r, 0);
                        float cy = Mathf.Max(Mathf.Abs(y - (h - 1) * 0.5f) - (h - 1) * 0.5f + r, 0);
                        float dist = Mathf.Sqrt(cx * cx + cy * cy) - r; // <0 内部，>0 外部
                        // 2px 软过渡：无锯齿圆角
                        float a = Mathf.Clamp01(-dist / 2f + 0.5f);
                        px[y * w + x] = new Color(1f, 1f, 1f, a);
                    }
                }
                tex.SetPixels(px);
                tex.Apply();
                // border 四边 = radius，保证 Sliced 模式拉伸时圆角不变形
                return Sprite.Create(tex, new Rect(0, 0, w, h),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                    new Vector4(radius, radius, radius, radius));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VoiceAI] 圆角 Sprite 生成失败: " + e.Message);
                return null;
            }
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

        // ---------- 唤醒词 ----------

        /// <summary>挂载唤醒词检测器与边缘光效（运行时自动创建，无需手动配置）</summary>
        private void InitWakeWordAndGlow()
        {
            var canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null) canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            _glow = EdgeGlowEffect.AttachToCanvas(canvas);
            if (_glow != null) _glow.SetState((int)State);

            _wake = GetComponent<WakeWordDetector>();
            if (_wake == null) _wake = gameObject.AddComponent<WakeWordDetector>();
            _wake.OnWakeWordDetected += OnWakeWordDetected;
            _wake.ListenEnabled = enableWakeWord;
        }

        /// <summary>命中唤醒词：自动开始录音（等价于点击按钮开始说话）</summary>
        private void OnWakeWordDetected(string keyword)
        {
            if (!enableWakeWord) return;

            if (wakeOnlyWhenIdle && State != VoiceAIState.Idle)
            {
                Debug.Log("[VoiceAI] 对话进行中，忽略唤醒词: " + keyword);
                return;
            }

            Debug.Log("[VoiceAI] 唤醒词命中 -> 开始录音: " + keyword);
            // 先停唤醒监听麦克风，再启动录音麦克风：
            // Android 上两个 AudioRecord 同时打开会互相冲突（第二次唤醒时录音启动失败并卡死）
            if (_wake != null) _wake.ListenEnabled = false;

            if (string.IsNullOrEmpty(wakeAckText))
            {
                StartListening();
                return;
            }
            StartCoroutine(PlayWakeAckThenListen());
        }

        /// <summary>
        /// 唤醒应答：先说一句"我在"再开始录音。
        /// 必须等应答播完再开麦，否则应答声会被录进用户语音一起送去识别。
        /// </summary>
        private IEnumerator PlayWakeAckThenListen()
        {
            SetState(VoiceAIState.Speaking);
            SetText(statusText, wakeAckText);

            if (useCloudTts)
            {
                if (_audioSource == null) { AckFallbackToListen(); yield break; }

                // 应答语固定，合成一次后缓存复用
                if (_wakeAckClip == null)
                {
                    bool failed = false;
                    yield return CloudTtsClient.Synthesize(tts, wakeAckText,
                        clip => { SaveTtsTime(); _wakeAckClip = clip; },
                        err => { failed = true; Debug.LogWarning("[VoiceAI] 唤醒应答合成失败，直接录音: " + err); });
                    if (failed) { AckFallbackToListen(); yield break; }
                }

                if (_wakeAckClip != null)
                {
                    _audioSource.clip = _wakeAckClip;
                    _audioSource.Play();
                    while (_audioSource != null && _audioSource.isPlaying)
                        yield return null;
                    yield return new WaitForSeconds(0.15f); // 留出尾音，避免录进余响
                }
            }
            else
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (_tts != null && _tts.IsReady && _tts.Speak(wakeAckText))
                {
                    // 轮询 isSpeaking 等待播完（该设备 OnUtteranceCompleted 回调已失效）
                    yield return new WaitForSeconds(0.1f); // speak 后状态置位有延迟
                    float timeout = Time.unscaledTime + 3f; // 防御：TTS 异常时不永久卡住
                    while (_tts.IsSpeaking() && Time.unscaledTime < timeout)
                        yield return new WaitForSeconds(0.05f);
                    yield return new WaitForSeconds(0.15f); // 尾音
                }
                else
                {
                    Debug.LogWarning("[VoiceAI] 唤醒应答播放失败，直接开始录音");
                }
#else
                // 编辑器无系统TTS：跳过应答直接录音
                Debug.Log("[VoiceAI] 编辑器模式跳过唤醒应答");
#endif
            }

            // 竞态防护：若应答期间用户手动点了按钮（Speaking→已直接进入 Listening），
            // 不再把状态拉回 Idle（否则会打断已开始的录音监控）
            if (State == VoiceAIState.Speaking) SetState(VoiceAIState.Idle);
            StartListening(); // 非 Idle 时内部会忽略
        }

        /// <summary>应答失败时的兜底：回到 Idle 并直接开始录音</summary>
        private void AckFallbackToListen()
        {
            SetState(VoiceAIState.Idle);
            StartListening();
        }

        /// <summary>仅在空闲时开启常驻麦克风，避免与录音麦克风冲突</summary>
        private void UpdateWakeListening()
        {
            if (_wake != null)
                _wake.ListenEnabled = enableWakeWord && State == VoiceAIState.Idle;
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

            // 优先走系统自带识别（SpeechRecognizer 自管麦克风与断句，无需上传音频）
            if (preferSystemStt)
            {
                bool requestFallback = false;
                yield return RunSystemStt(v => requestFallback = v);
                if (!requestFallback) yield break;
                Debug.LogWarning("[VoiceAI] 系统识别不可用，回退录音+云端STT流程");
                SetText(statusText, "切换云端识别...");
            }
#endif

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                RaiseError("未检测到麦克风设备");
                SetState(VoiceAIState.Idle);
                yield break;
            }

            // 麦克风启动（带重试）：唤醒监听可能刚释放 AudioRecord，
            // Android 上立即重新打开偶发失败（返回 null），需短暂等待后重试
            _processing = false;
            _silentCount = 0;
            _speechStarted = false;
            _speechStartTicks = 0;
            _recDevice = Microphone.devices[0];
            _recClip = null;
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                _recClip = Microphone.Start(_recDevice, false, (int)maxRecordSeconds + 5, 16000);
                if (_recClip != null && Microphone.IsRecording(_recDevice)) break;
                Debug.LogWarning("[VoiceAI] 麦克风启动失败，重试 " + attempt + "/8");
                _recClip = null;
                yield return new WaitForSeconds(0.1f);
            }

            if (_recClip == null)
            {
                RaiseError("麦克风启动失败，请再唤醒一次");
                SetState(VoiceAIState.Idle);
                yield break;
            }

            _recStartTime = Time.unscaledTime;
            Debug.Log("[VoiceAI] 开始录音: " + _recDevice);
            SetText(statusText, "正在录音 0.0s（说完了再点一次）");
            StartCoroutine(RecordingMonitor());
        }

        /// <summary>
        /// 系统识别流程（Android 真机）：SpeechRecognizer 自管麦克风、自断句，
        /// 中间结果实时上屏、onRmsChanged 喂流光；结束/失败/超时统一收尾。
        /// requestFallback=true 表示需要回退到录音+云端STT管线。
        /// </summary>
        private IEnumerator RunSystemStt(Action<bool> requestFallback)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var rec = AndroidSpeechRecognizer.Create();
            if (rec == null)
            {
                requestFallback(true);
                yield break;
            }

            string result = null, error = null;
            bool done = false;
            rec.OnResult += t => { result = t; done = true; };
            rec.OnError += e => { error = e; done = true; };
            rec.OnPartial += p =>
            {
                SetText(recognizedText, "你说: " + p);
            };
            rec.OnRms += r =>
            {
                if (_glow != null) _glow.SetSpeechLevel(Mathf.Clamp01(r / 8f));
                SetText(statusText, "正在聆听...");
            };

            if (!rec.Start())
            {
                rec.Dispose();
                requestFallback(true);
                yield break;
            }
            Debug.Log("[VoiceAI] 系统识别已启动（本地优先）");

            float t0 = Time.unscaledTime;
            while (!done)
            {
                float el = Time.unscaledTime - t0;
                if (el >= maxRecordSeconds) rec.Stop();               // 截尾识别（仍会回调结果）
                if (el >= maxRecordSeconds + 3f) { error = "识别超时"; done = true; } // 防挂死
                yield return null;
            }
            rec.Dispose();

            if (!string.IsNullOrEmpty(result))
            {
                OnSttSuccess(result);
                yield break;
            }

            // 无结果：区分"没说话超时"（正常取消）与"识别失败"（回退云端再试一轮）
            bool noSpeech = error != null && (error.Contains("speech-timeout") || error.Contains("没听到"));
            if (noSpeech)
            {
                Debug.Log("[VoiceAI] 系统识别：未检测到说话，取消本轮");
                RaiseError("没有听到您说话，唤醒词重新唤醒即可");
                SetState(VoiceAIState.Idle);
            }
            else
            {
                Debug.LogWarning("[VoiceAI] 系统识别失败(" + error + ")，请再说一次（走云端）");
                RaiseError("系统识别失败，请再唤醒说一次");
                SetState(VoiceAIState.Idle);
            }
#else
            requestFallback(true);
            yield break;
#endif
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

            // 上传前裁剪：只保留"开口前0.3s → 结束"的实际语音段，
            // 剔除等待期静音（可能数秒）→ 上传数据量与耗时等比下降
            byte[] wav = _speechStarted
                ? WavUtility.ToWav16kMono(_recClip, Mathf.Max(0f, _speechStartElapsed - 0.3f), elapsed)
                : WavUtility.ToWav16kMono(_recClip);
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
            // 0.1s 细粒度轮询：说完话后能更快进入静音判定与累计
            var wait = new WaitForSeconds(0.1f);
            const int win = 4800; // 检测窗口 0.3s @16kHz
            if (_winBuf == null || _winBuf.Length != win)
                _winBuf = new float[win];
            int silentTicksNeeded = Mathf.Max(1, Mathf.RoundToInt(silenceConfirmSeconds / 0.1f));

            // 底噪自适应校准：开场 0.8s 取最小 RMS 作为底噪估计。
            // Android AGC 会在"唤醒麦克↔录音麦克"交替后改变增益（第二轮与第一轮不同），
            // 固定阈值会失配：增益调低→说话判定不到开口；调高→底噪被当成说话。都导致不自动停止。
            float noiseFloor = 0.06f;  // 底噪估计（上限钳制）
            int calibTicks = 0;
            const int CalibTotal = 8;  // 8 tick × 0.1s
            int tick = 0;

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

                SetText(statusText, _speechStarted
                    ? "正在聆听..."
                    : "请说话 " + Mathf.Max(0, noSpeechTimeoutSeconds - elapsed).ToString("0") + "s");

                // 静音检测：只读最近 0.3 秒窗口，计算 RMS
                // （RMS 对瞬时咔哒声免疫、对持续语音敏感，比峰值判定稳定得多；
                //   旧实现每 0.25s GetData 整个 clip 且峰值阈值 0.02 极易被底噪重置）
                int pos = Microphone.GetPosition(_recDevice);
                if (pos >= win)
                {
                    _recClip.GetData(_winBuf, pos - win);
                }
                else if (pos > 0)
                {
                    // 环形缓冲回绕（仅录音超过 clip 总长时出现，罕见）：分两段读取
                    int tail = win - pos;
                    var b1 = new float[tail];
                    var b2 = new float[pos];
                    _recClip.GetData(b1, _recClip.samples - tail);
                    _recClip.GetData(b2, 0);
                    Array.Copy(b1, 0, _winBuf, 0, tail);
                    Array.Copy(b2, 0, _winBuf, tail, pos);
                }

                double sum = 0;
                for (int i = 0; i < win; i++)
                {
                    float s = _winBuf[i];
                    sum += s * s;
                }
                float rms = (float)Math.Sqrt(sum / win);

                // 监听态：把用户说话音量实时喂给边缘流光（波澜随声音起伏）
                if (_glow != null) _glow.SetSpeechLevel(Mathf.Clamp01(rms * 4f));

                // 开场底噪校准：取前 0.8s 的最小 RMS（用户若立即开口，最小值仍接近真实底噪）
                if (calibTicks < CalibTotal)
                {
                    calibTicks++;
                    noiseFloor = Mathf.Min(noiseFloor, Mathf.Max(rms, 0.002f));
                    if (calibTicks == CalibTotal)
                        Debug.Log("[VoiceAI] 底噪校准完成: " + noiseFloor.ToString("0.000"));
                }

                // 有效阈值（随底噪自适应）：静音线=底噪×1.8，开口线=底噪×2.6
                float effSilence = Mathf.Max(silenceRmsThreshold, noiseFloor * 1.8f);
                float effSpeech  = Mathf.Max(speechStartRms, noiseFloor * 2.6f, effSilence + 0.006f);

                // 每 3s 输出一次判定状态，真机可用 logcat 直接排查
                if (++tick % 30 == 0)
                    Debug.Log("[VoiceAI] 判定状态 RMS=" + rms.ToString("0.000")
                        + " 静音线=" + effSilence.ToString("0.000")
                        + " 开口线=" + effSpeech.ToString("0.000")
                        + " 已开口=" + _speechStarted + " 静音累计=" + _silentCount);

                if (rms >= effSpeech)
                {
                    // 连续 2 tick（0.2s）超过开口阈值才算真正说话，滤掉底噪毛刺
                    if (++_speechStartTicks >= 2 && !_speechStarted)
                    {
                        _speechStarted = true;
                        _speechStartElapsed = elapsed; // 记录开口时刻：上传时裁掉之前的静音段
                    }
                    _silentCount = 0;
                }
                else
                {
                    _speechStartTicks = 0;

                    if (_speechStarted)
                    {
                        // 静音结束判定仅在已开口后生效；
                        // 过渡带（静音线~开口线之间）保持计数不累计，防止说话尾音被误判
                        if (rms < effSilence && elapsed > 0.8f && ++_silentCount >= silentTicksNeeded)
                        {
                            Debug.Log("[VoiceAI] 检测到静音(" + rms.ToString("0.000") + ")，自动结束录音");
                            StopRecordingAndProcess();
                            yield break;
                        }
                    }
                    else if (elapsed >= noSpeechTimeoutSeconds)
                    {
                        // 未开口超时：取消本轮并恢复唤醒监听（重新说唤醒词即可）
                        Debug.Log("[VoiceAI] 等待说话超时(" + elapsed.ToString("0.0") + "s)，取消本轮录音");
                        StopRecordingInternal();
                        _recClip = null;
                        _processing = true; // 防止 StopRecordingAndProcess 相关分支再触发
                        RaiseError("没有听到您说话，唤醒词重新唤醒即可");
                        SetState(VoiceAIState.Idle); // SetState 内部会恢复唤醒监听
                        yield break;
                    }
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

        private void Update()
        {
            // 回答态（云端TTS）：采样正在播放的音频音量，实时喂给边缘流光
            // （系统TTS 无 AudioSource 可读，由光效内置的音节节律模拟兜底）
            if (State == VoiceAIState.Speaking && _glow != null && useCloudTts &&
                _audioSource != null && _audioSource.isPlaying)
            {
                AudioClip clip = _audioSource.clip;
                if (clip != null)
                {
                    int start = _audioSource.timeSamples;
                    int n = Mathf.Min(GlowSampleCount, clip.samples - start);
                    if (n > 128)
                    {
                        if (_glowBuf == null || _glowBuf.Length != GlowSampleCount)
                            _glowBuf = new float[GlowSampleCount];
                        clip.GetData(_glowBuf, start);
                        double sum = 0;
                        for (int i = 0; i < n; i++)
                        {
                            float s = _glowBuf[i];
                            sum += s * s;
                        }
                        float rms = (float)Math.Sqrt(sum / n);
                        _glow.SetSpeechLevel(Mathf.Clamp01(rms * 4f));
                    }
                }
            }
        }

        private void SetState(VoiceAIState state)
        {
            if (State == state) return;
            State = state;
            OnStateChanged?.Invoke(state);
            if (_glow != null) _glow.SetState((int)state); // 边缘流光随状态切换
            UpdateStatusText();
            UpdateWakeListening();
        }

        private void UpdateStatusText()
        {
            // 状态提示精简化：一句话、不重复界面已有的信息
            string text;
            switch (State)
            {
                case VoiceAIState.Idle:
                    text = enableWakeWord ? "说 \"" + wakeWordText + "\" 唤醒" : "点击按钮开始";
                    break;
                case VoiceAIState.Listening: text = "正在聆听..."; break;
                case VoiceAIState.Thinking: text = "思考中..."; break;
                case VoiceAIState.Speaking: text = "正在回答..."; break;
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
