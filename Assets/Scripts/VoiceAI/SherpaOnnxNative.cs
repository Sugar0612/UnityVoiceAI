using System;
using System.Runtime.InteropServices;

namespace VoiceAI
{
    /// <summary>
    /// sherpa-onnx 关键词检测（Keyword Spotter）C API 的 P/Invoke 封装。
    /// 原生库：libsherpa-onnx-c-api.so（另依赖 libonnxruntime.so），位于
    /// Assets/Plugins/Android/arm64-v8a/。
    /// 下方结构体布局必须与 sherpa-onnx 的 c-api.h 完全一致（按值/引用传递）。
    /// </summary>
    public static class SherpaOnnxNative
    {
        private const string Lib = "sherpa-onnx-c-api";

        // ---------- 配置结构体（与 c-api.h 对齐） ----------

        [StructLayout(LayoutKind.Sequential)]
        public struct FeatureConfig
        {
            public int sample_rate;
            public int feature_dim;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TransducerModelConfig
        {
            public IntPtr encoder;
            public IntPtr decoder;
            public IntPtr joiner;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ParaformerModelConfig
        {
            public IntPtr encoder;
            public IntPtr decoder;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Zipformer2CtcModelConfig
        {
            public IntPtr model;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NemoCtcModelConfig
        {
            public IntPtr model;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ToneCtcModelConfig
        {
            public IntPtr model;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OnlineModelConfig
        {
            public TransducerModelConfig transducer;
            public ParaformerModelConfig paraformer;
            public Zipformer2CtcModelConfig zipformer2_ctc;
            public IntPtr tokens;
            public int num_threads;
            public IntPtr provider;
            public int debug;
            public IntPtr model_type;
            public IntPtr modeling_unit;
            public IntPtr bpe_vocab;
            public IntPtr tokens_buf;
            public int tokens_buf_size;
            public NemoCtcModelConfig nemo_ctc;
            public ToneCtcModelConfig t_one_ctc;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeywordSpotterConfig
        {
            public FeatureConfig feat_config;
            public OnlineModelConfig model_config;
            public int max_active_paths;
            public int num_trailing_blanks;
            public float keywords_score;
            public float keywords_threshold;
            public IntPtr keywords_file;
            public IntPtr keywords_buf;
            public int keywords_buf_size;
        }

        // ---------- 原生接口 ----------

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxCreateKeywordSpotter(ref KeywordSpotterConfig config);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyKeywordSpotter(IntPtr spotter);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxCreateKeywordStream(IntPtr spotter);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyOnlineStream(IntPtr stream);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxOnlineStreamAcceptWaveform(IntPtr stream, int sampleRate, [In] float[] samples, int n);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SherpaOnnxIsKeywordStreamReady(IntPtr spotter, IntPtr stream);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDecodeKeywordStream(IntPtr spotter, IntPtr stream);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxResetKeywordStream(IntPtr spotter, IntPtr stream);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxGetKeywordResultAsJson(IntPtr spotter, IntPtr stream);

        // ---------- 在线识别（流式 ASR，可拿到完整转写文本） ----------

        [StructLayout(LayoutKind.Sequential)]
        public struct EndpointConfig
        {
            public int rule;
            public int must_contain_nonsilence;
            public float min_trailing_silence;
            public float min_utterance_length;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OnlineRecognizerConfig
        {
            public FeatureConfig feat_config;
            public OnlineModelConfig model_config;
            public int decoding_method;
            public int max_active_paths;
            public int enable_endpoint;
            public EndpointConfig rule1;
            public EndpointConfig rule2;
            public EndpointConfig rule3;
            public IntPtr hotwords_file;
            public float hotwords_score;
            public int num_trailing_blanks;
            public IntPtr provider;
            public int num_threads;
            public IntPtr lm_config;
            public IntPtr rules;
            public IntPtr tokens_buf;
            public int tokens_buf_size;
            public IntPtr model_type;
            public IntPtr keywords_score;
            public float keywords_threshold;
            public IntPtr blank_penalty;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxCreateOnlineRecognizer(ref OnlineRecognizerConfig config);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyOnlineRecognizer(IntPtr recognizer);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxCreateOnlineStream(IntPtr recognizer);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SherpaOnnxIsOnlineStreamReady(IntPtr recognizer, IntPtr stream);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDecodeOnlineStream(IntPtr recognizer, IntPtr stream);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxGetOnlineStreamResultAsJson(IntPtr recognizer, IntPtr stream);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyOnlineStreamResultJson(IntPtr json);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxOnlineStreamReset(IntPtr stream);
    }
}
