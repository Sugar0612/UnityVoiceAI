using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace VoiceAI
{
    /// <summary>
    /// 常驻麦克风唤醒词检测：使用 sherpa-onnx 关键词检测（Keyword Spotter，流式 zipformer transducer + 拼音 tokens）。
    /// 命中 StreamingAssets/KwsModel/keywords.txt 中配置的唤醒词时触发 OnWakeWordDetected。
    ///
    /// Android 适配要点：
    /// 1. 麦克风实际采样率由系统决定（通常 44100/48000），使用线性插值重采样到 16kHz；
    /// 2. 若麦克风为立体声，先抽取第 0 声道再重采样；
    /// 3. 解码遵循官方模式：喂音频 → while(IsReady) 连续解码 → 结果非空才 Reset，
    ///    绝不能每个音频块后无条件 Reset（会清空跨块的声学状态导致永远检测不到）；
    /// 4. C# 配置结构体与 c-api.h（nemo_ctc/t_one_ctc 位于 OnlineModelConfig 末尾，
    ///    即当前 master 与项目自带 .so/.dll 的实际布局）完全一致；
    ///    注意错误布局不会返回空指针而是直接 SIGSEGV 崩溃，不存在"失败重试"的机会。
    /// </summary>
    public class WakeWordDetector : MonoBehaviour
    {
        public event Action<string> OnWakeWordDetected;

        [Tooltip("打印调试日志")]
        [SerializeField] private bool debugLog = true;

        private const int SampleRate = 16000;
        private const int ChunkSamples = 2560;   // 160ms @16k

        private static readonly string[] ModelFiles =
        {
            "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "decoder-epoch-12-avg-2-chunk-16-left-64.onnx",
            "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "tokens.txt",
            "keywords.txt",
        };

        // 与关键词条目形如 {"keyword":"..."} 匹配
        private static readonly Regex KeywordJsonRegex =
            new Regex("\"keyword\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        public bool IsReady { get; private set; }

        private bool _listenEnabled = true;
        public bool ListenEnabled
        {
            get => _listenEnabled;
            set
            {
                if (_listenEnabled == value) return;
                _listenEnabled = value;
                if (value) { if (IsReady && _micClip == null) StartMic(); }
                else { StopMic(); }
            }
        }

        private IntPtr _spotter;
        private IntPtr _stream;

        private AudioClip _micClip;
        private string _micDevice;
        private float _micRetryAt;      // 麦克风启动失败后的下次自愈重试时间
        private int _lastPos;
        private float[] _readBuf;      // 麦克风原始数据缓冲
        private float[] _monoBuf;      // 第 0 声道抽取缓冲

        // 重采样 / 分块状态
        private float[] _chunkBuf = new float[ChunkSamples];
        private int _chunkFill;
        private double _resamplePos;   // 线性插值重采样的分数位置（相对当前块起点）

        // 调试统计
        private long _chunkCount;
        private long _detectCount;
        private float _startTime;
        private double _diagSrcAvail;  // 本诊断周期内的源采样点数
        private float _diagTime;

        private void Start()
        {
            StartCoroutine(InitRoutine());
        }

        private IEnumerator InitRoutine()
        {
            yield return null;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                bool granted = false, denied = false;
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => granted = true;
                callbacks.PermissionDenied += _ => denied = true;
                callbacks.PermissionDeniedAndDontAskAgain += _ => denied = true;
                Permission.RequestUserPermission(Permission.Microphone, callbacks);
                while (!granted && !denied) yield return null;
                if (!granted)
                {
                    Debug.LogWarning("[WakeWord] 未获得麦克风权限，唤醒监听不可用");
                    yield break;
                }
            }
#endif

            yield return EnsureModelFiles();
            if (!CreateSpotter())
            {
                // 编辑器缺 Windows DLL（DllNotFoundException）时仅禁用唤醒，不抛异常：
                // 这样编辑器里仍可正常测试 UI/流程（唤醒功能仅真机可用）
                Debug.LogWarning("[WakeWord] KWS 初始化失败，唤醒功能不可用（编辑器需 x86_64 DLL 才能测试唤醒）");
                yield break;
            }

            if (_listenEnabled) StartMic();
            IsReady = true;
            Log("[WakeWord] 唤醒监听已启动 persistent=" + Application.persistentDataPath);
        }

        /// <summary>
        /// 把 StreamingAssets 中的模型复制到 persistentDataPath（Android 上 StreamingAssets 无法直接用 File API）。
        /// 每次启动强制覆盖：避免旧构建留下的残缺文件或更新后的 keywords.txt 不生效。
        /// </summary>
        private IEnumerator EnsureModelFiles()
        {
            string srcDir = Application.streamingAssetsPath + "/KwsModel/";
            string dstDir = Path.Combine(Application.persistentDataPath, "KwsModel");
            if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

            foreach (string file in ModelFiles)
            {
                using (var req = UnityWebRequest.Get(srcDir + file))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError("[WakeWord] 模型文件读取失败: " + file + " " + req.error);
                        yield break;
                    }
                    byte[] data = req.downloadHandler.data;
                    File.WriteAllBytes(Path.Combine(dstDir, file), data);
                    Log("[WakeWord] 已复制模型文件: " + file + " (" + data.Length + " bytes)");
                }
            }
            Log("[WakeWord] 模型文件已就绪: " + dstDir);
        }

        private bool CreateSpotter()
        {
            string dir = Path.Combine(Application.persistentDataPath, "KwsModel");
            string enc = Path.Combine(dir, ModelFiles[0]);
            string dec = Path.Combine(dir, ModelFiles[1]);
            string joiner = Path.Combine(dir, ModelFiles[2]);
            string tokens = Path.Combine(dir, ModelFiles[3]);
            string keywords = Path.Combine(dir, ModelFiles[4]);

            if (!File.Exists(enc) || !File.Exists(dec) || !File.Exists(joiner) ||
                !File.Exists(tokens) || !File.Exists(keywords))
            {
                Debug.LogError("[WakeWord] 模型文件缺失");
                return false;
            }

            // 为各字符串字段分配非托管内存
            IntPtr pEnc = Marshal.StringToHGlobalAnsi(enc);
            IntPtr pDec = Marshal.StringToHGlobalAnsi(dec);
            IntPtr pJoiner = Marshal.StringToHGlobalAnsi(joiner);
            IntPtr pTokens = Marshal.StringToHGlobalAnsi(tokens);
            IntPtr pProvider = Marshal.StringToHGlobalAnsi("cpu");
            IntPtr pUnit = Marshal.StringToHGlobalAnsi("bpe");
            IntPtr pKeywords = Marshal.StringToHGlobalAnsi(keywords);

            try
            {
                var cfg = new SherpaOnnxNative.KeywordSpotterConfig();
                cfg.feat_config.sample_rate = SampleRate;
                cfg.feat_config.feature_dim = 80;
                cfg.model_config.transducer.encoder = pEnc;
                cfg.model_config.transducer.decoder = pDec;
                cfg.model_config.transducer.joiner = pJoiner;
                cfg.model_config.tokens = pTokens;
                cfg.model_config.num_threads = 4;
                cfg.model_config.provider = pProvider;
                cfg.model_config.debug = 1;
                cfg.model_config.modeling_unit = pUnit;
                cfg.max_active_paths = 8;
                cfg.num_trailing_blanks = 1;
                cfg.keywords_score = 4.5f;          // 关键词路径加分（越高越容易命中）
                cfg.keywords_threshold = 0.05f;     // 触发门槛（越低越灵敏）
                cfg.keywords_file = pKeywords;

                _spotter = SherpaOnnxNative.SherpaOnnxCreateKeywordSpotter(ref cfg);
                if (_spotter != IntPtr.Zero)
                    Log("[WakeWord] KWS 创建成功");
            }
            catch (DllNotFoundException e)
            {
                // Windows/编辑器缺少 sherpa-onnx-c-api.dll（或其依赖 onnxruntime.dll）：
                // 降级返回（finally 仍会释放句柄），仅禁用唤醒，UI/流程在编辑器仍可测试
                Debug.LogWarning("[WakeWord] 原生库缺失，唤醒功能仅真机可用: " + e.Message);
                _spotter = IntPtr.Zero;
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pEnc);
                Marshal.FreeHGlobal(pDec);
                Marshal.FreeHGlobal(pJoiner);
                Marshal.FreeHGlobal(pTokens);
                Marshal.FreeHGlobal(pProvider);
                Marshal.FreeHGlobal(pUnit);
                Marshal.FreeHGlobal(pKeywords);
            }

            if (_spotter == IntPtr.Zero)
            {
                Debug.LogError("[WakeWord] SherpaOnnxCreateKeywordSpotter 返回空");
                return false;
            }

            _stream = SherpaOnnxNative.SherpaOnnxCreateKeywordStream(_spotter);
            if (_stream == IntPtr.Zero)
            {
                Debug.LogError("[WakeWord] SherpaOnnxCreateKeywordStream 返回空");
                return false;
            }
            return true;
        }

        private void StartMic()
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                Debug.LogError("[WakeWord] 无麦克风设备");
                return;
            }

            _micDevice = Microphone.devices[0];
            _micClip = Microphone.Start(_micDevice, true, 30, SampleRate);
            ResetPipeline();

            if (_micClip == null)
            {
                // 保持未运行状态，由 Update 中的自愈逻辑定期重试（麦克风资源竞争场景）
                _micRetryAt = Time.realtimeSinceStartup + 0.5f;
                Debug.LogWarning("[WakeWord] 麦克风启动失败，将自动重试");
                return;
            }
            Log("[WakeWord] 麦克风启动: " + _micDevice +
                " 实际采样率=" + _micClip.frequency + "(请求" + SampleRate + ")" +
                " ch=" + _micClip.channels + " samples=" + _micClip.samples);
        }

        private void StopMic()
        {
            if (_micDevice != null && _micClip != null && Microphone.IsRecording(_micDevice))
                Microphone.End(_micDevice);
            _micClip = null;
            ResetPipeline();

            if (_spotter != IntPtr.Zero && _stream != IntPtr.Zero)
                SherpaOnnxNative.SherpaOnnxResetKeywordStream(_spotter, _stream);
        }

        private void ResetPipeline()
        {
            _lastPos = 0;
            _chunkFill = 0;
            _resamplePos = 0;
            _chunkCount = 0;
            _detectCount = 0;
            _startTime = Time.realtimeSinceStartup;
            _diagSrcAvail = 0;
            _diagTime = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            // 自愈：监听开启但麦克风未运行（与录音麦克风竞争失败等）时定期重试
            if (IsReady && _listenEnabled && _micClip == null &&
                Time.realtimeSinceStartup >= _micRetryAt)
            {
                _micRetryAt = Time.realtimeSinceStartup + 0.5f;
                if (Microphone.devices != null && Microphone.devices.Length > 0)
                    StartMic();
            }

            if (!IsReady || !_listenEnabled || _micClip == null ||
                _spotter == IntPtr.Zero || _stream == IntPtr.Zero)
                return;

            int pos = Microphone.GetPosition(_micDevice);
            if (pos < 0) return;

            int avail = (pos - _lastPos + _micClip.samples) % _micClip.samples;
            if (avail <= 0) return;

            if (_readBuf == null || _readBuf.Length < avail)
                _readBuf = new float[avail];
            _micClip.GetData(_readBuf, _lastPos);
            _lastPos = pos;

            _diagSrcAvail += avail * _micClip.channels;
            if (Time.realtimeSinceStartup - _diagTime >= 5f)
            {
                float rate = (float)_diagSrcAvail / Mathf.Max(0.001f, Time.realtimeSinceStartup - _diagTime);
                Log("[WakeWord] DIAG 源采样率=" + (int)rate +
                    " clip频率=" + _micClip.frequency + " ch=" + _micClip.channels +
                    " chunks=" + _chunkCount);
                _diagSrcAvail = 0;
                _diagTime = Time.realtimeSinceStartup;
            }

            DownmixAndResample(avail);
        }

        /// <summary>抽取第 0 声道并线性插值重采样到 16kHz 后送入分块队列。</summary>
        private void DownmixAndResample(int srcCount)
        {
            int ch = Mathf.Max(1, _micClip.channels);
            int count = ch > 1 ? srcCount / ch : srcCount;

            if (ch > 1)
            {
                if (_monoBuf == null || _monoBuf.Length < count)
                    _monoBuf = new float[count];
                for (int i = 0; i < count; i++)
                    _monoBuf[i] = _readBuf[i * ch];
            }
            float[] src = ch > 1 ? _monoBuf : _readBuf;

            if (_micClip.frequency == SampleRate)
            {
                for (int i = 0; i < count; i++) PushSample(src[i]);
                return;
            }

            float ratio = SampleRate / (float)_micClip.frequency; // 输出样本数/输入样本数
            while ((int)_resamplePos < count)
            {
                int i0 = (int)_resamplePos;
                float t = (float)(_resamplePos - i0);
                float v = i0 + 1 < count ? Mathf.Lerp(src[i0], src[i0 + 1], t) : src[i0];
                PushSample(v);
                _resamplePos += ratio;
            }
            _resamplePos -= count; // 余量带到下一块
        }

        /// <summary>累积成 160ms 块后送入 KWS 流并驱动解码。</summary>
        private void PushSample(float v)
        {
            _chunkBuf[_chunkFill++] = v;
            if (_chunkFill < ChunkSamples) return;

            _chunkFill = 0;
            _chunkCount++;
            SherpaOnnxNative.SherpaOnnxOnlineStreamAcceptWaveform(_stream, SampleRate, _chunkBuf, ChunkSamples);
            DecodeStep();
        }

        /// <summary>
        /// 官方推荐的 KWS 解码循环：连续解码直到原生库提示需要更多音频；
        /// 只有真正命中关键词时才 Reset（保留跨块声学状态）。
        /// </summary>
        private void DecodeStep()
        {
            int guard = 64; // 单块最多解码次数保护
            while (guard-- > 0 &&
                   SherpaOnnxNative.SherpaOnnxIsKeywordStreamReady(_spotter, _stream) != 0)
            {
                SherpaOnnxNative.SherpaOnnxDecodeKeywordStream(_spotter, _stream);
            }

            IntPtr jsonPtr = SherpaOnnxNative.SherpaOnnxGetKeywordResultAsJson(_spotter, _stream);
            if (jsonPtr == IntPtr.Zero) return;

            string json = Marshal.PtrToStringAnsi(jsonPtr);
            if (string.IsNullOrEmpty(json)) return;

            Match m = KeywordJsonRegex.Match(json);
            if (m.Success && m.Groups[1].Value.Length > 0)
            {
                _detectCount++;
                Log("[WakeWord] 检测到唤醒词: " + m.Groups[1].Value + " json=" + Truncate(json, 200));
                OnWakeWordDetected?.Invoke(m.Groups[1].Value);
                SherpaOnnxNative.SherpaOnnxResetKeywordStream(_spotter, _stream); // 触发后重启下一段监听
                return;
            }

            if (_chunkCount % 25 == 0) // 约10秒一条心跳日志，便于真机确认流水线在跑
            {
                float rtf = _chunkCount * ChunkSamples /
                            Mathf.Max(0.01f, Time.realtimeSinceStartup - _startTime);
                Log("[WakeWord] 心跳 chunks=" + _chunkCount + " rtf≈" + rtf.ToString("F2") +
                    " json=" + Truncate(json, 120));
            }
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private void OnDestroy()
        {
            IsReady = false;

            if (_micDevice != null && _micClip != null && Microphone.IsRecording(_micDevice))
                Microphone.End(_micDevice);
            _micClip = null;

            if (_stream != IntPtr.Zero)
            {
                SherpaOnnxNative.SherpaOnnxDestroyOnlineStream(_stream);
                _stream = IntPtr.Zero;
            }
            if (_spotter != IntPtr.Zero)
            {
                SherpaOnnxNative.SherpaOnnxDestroyKeywordSpotter(_spotter);
                _spotter = IntPtr.Zero;
            }
        }

        private void Log(string msg)
        {
            if (debugLog) Debug.Log(msg);
        }
    }
}
