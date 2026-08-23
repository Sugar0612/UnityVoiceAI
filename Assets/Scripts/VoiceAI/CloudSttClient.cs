using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace VoiceAI
{
    /// <summary>云端语音识别(STT)配置——OpenAI 兼容接口，在 Inspector 中填写</summary>
    [Serializable]
    public class SttSettings
    {
        [Tooltip("OpenAI 兼容的 /audio/transcriptions 接口地址")]
        public string apiUrl = "https://api.siliconflow.cn/v1/audio/transcriptions";

        [Tooltip("API Key（sk- 开头，硅基流动 https://cloud.siliconflow.cn 免费申请；OpenAI 用 openai 的 key）")]
        public string apiKey = "";

        [Tooltip("模型：硅基流动用 FunAudioLLM/SenseVoiceSmall；OpenAI 用 whisper-1；阿里云百炼用 paraformer-v2 或 paraformer-realtime-v2")]
        public string model = "FunAudioLLM/SenseVoiceSmall";

        [Tooltip("识别语言，中文 zh；留空则不传")]
        public string language = "zh";

        [Tooltip("方言提示（如 zh-shaanxi 陕西话、zh-yue 粤语、zh-sichuan 四川话）；留空则不传")]
        public string languageHints = "";

        [Header("识别引擎")]
        [Tooltip("0=OpenAI兼容接口(硅基流动等，默认)；1=火山引擎录音文件识别(方言候选)")]
        public int provider = 0;

        [Tooltip("讯飞开放平台 AppID（xfyun.cn 控制台）")]
        public string iflyAppId = "";
        [Tooltip("讯飞 APIKey")]
        public string iflyApiKey = "";
        [Tooltip("讯飞 APISecret")]
        public string iflyApiSecret = "";
        [Tooltip("讯飞方言 accent：方言大模型默认 mulacc（多口音自动识别，含陕西话）")]
        public string iflyAccent = "mulacc";
        [Tooltip("讯飞引擎类型，默认 16k_zh")]
        public string iflyEngineType = "16k_zh";

        [Tooltip("讯飞协议：slm=方言大模型(默认,陕西话)；iat=流式版听写(免费额度,备用)")]
        public string iflyDomain = "slm";
    }

    /// <summary>
    /// 云端语音识别客户端（OpenAI 兼容 /audio/transcriptions，multipart 上传 WAV）。
    /// 兼容：硅基流动、OpenAI Whisper、以及任何 OpenAI 兼容的识别服务。
    /// </summary>
    public static class CloudSttClient
    {
        [Serializable]
        public class Response
        {
            public string text;
        }

        /// <summary>
        /// 上传一段 WAV 音频，识别完成后回调 onSuccess(text)。
        /// 用法：StartCoroutine(CloudSttClient.Transcribe(settings, wavBytes, ok => {...}, err => {...}));
        /// </summary>
        public static IEnumerator Transcribe(SttSettings settings, byte[] wavBytes,
            Action<string> onSuccess, Action<string> onError)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.apiKey))
            {
                onError?.Invoke("请先在 Inspector 的「语音识别(STT) 配置」里填写 API Key");
                yield break;
            }
            if (wavBytes == null || wavBytes.Length == 0)
            {
                onError?.Invoke("录音数据为空，请重试");
                yield break;
            }

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("model", settings.model),
                new MultipartFormFileSection("file", wavBytes, "audio.wav", "audio/wav"),
            };
            // 语言/方言仅在填写时传入（兼容不同服务商）
            if (!string.IsNullOrWhiteSpace(settings.language))
                form.Add(new MultipartFormDataSection("language", settings.language));
            if (!string.IsNullOrWhiteSpace(settings.languageHints))
                form.Add(new MultipartFormDataSection("language_hints", settings.languageHints));

            using var uwr = UnityWebRequest.Post(settings.apiUrl.Trim(), form);
            uwr.timeout = 60;
            uwr.SetRequestHeader("Authorization", "Bearer " + settings.apiKey.Trim());

            yield return uwr.SendWebRequest();

            if (uwr.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var resp = JsonUtility.FromJson<Response>(uwr.downloadHandler.text);
                    if (resp != null && !string.IsNullOrWhiteSpace(resp.text))
                        onSuccess?.Invoke(resp.text.Trim());
                    else
                        onError?.Invoke("语音识别返回为空: " + Truncate(uwr.downloadHandler.text, 200));
                }
                catch (Exception e)
                {
                    onError?.Invoke("解析识别结果失败: " + e.Message);
                }
            }
            else
            {
                onError?.Invoke($"语音识别请求失败(HTTP {uwr.responseCode}): " + Truncate(uwr.downloadHandler?.text ?? uwr.error, 200));
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
