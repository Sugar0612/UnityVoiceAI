using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace VoiceAI
{
    /// <summary>云端语音合成(TTS)配置——MiniMax TTS，在 Inspector 中填写</summary>
    [Serializable]
    public class TtsSettings
    {
        [Tooltip("MiniMax TTS v2 接口地址（新接口不需要 GroupId）")]
        public string apiUrl = "https://api.minimaxi.com/v1/t2a_v2";

        [Tooltip("MiniMax API Key（控制台：基本信息 → 接口密钥 创建，sk- 开头）")]
        public string apiKey = "";

        [Tooltip("声音ID：声音复刻生成的 voice_id，或官方音色 id（如 male-qn-qingse）；换声音只改这里")]
        public string voiceId = "";

        [Tooltip("合成模型，如 speech-2.8-hd / speech-02-hd")]
        public string model = "speech-2.8-hd";

        [Tooltip("语速 0.5~2.0")]
        public float speed = 1.0f;

        [Tooltip("采样率：16000 / 24000 / 32000")]
        public int sampleRate = 24000;
    }

    /// <summary>
    /// 云端语音合成客户端（MiniMax T2A v2）。
    /// 实测接口返回 hex 编码的音频（format=url 会被忽略），采用 pcm 格式：
    /// hex 解码 → 裸 PCM16 → 直接构建 AudioClip 播放（无需文件、无需 MP3 解码器）。
    /// </summary>
    public static class CloudTtsClient
    {
        [Serializable]
        private class TtsRequest
        {
            public string model;
            public string text;
            public bool stream;
            public VoiceSetting voice_setting;
            public AudioSetting audio_setting;
        }

        [Serializable]
        private class VoiceSetting
        {
            public string voice_id;
            public float speed;
            public float vol;
            public int pitch;
        }

        [Serializable]
        private class AudioSetting
        {
            public int sample_rate;
            public int bitrate;
            public string format;          // mp3 / pcm / flac
            public int channel;
        }

        [Serializable]
        private class TtsResponse
        {
            public Data data;
            public BaseResp base_resp;
        }

        [Serializable]
        private class Data
        {
            public string audio;
            public int status;
        }

        [Serializable]
        private class BaseResp
        {
            public int status_code;
            public string status_msg;
        }

        /// <summary>
        /// 合成一段语音，成功回调 onSuccess(AudioClip)，失败回调 onError(信息)。
        /// 用法：StartCoroutine(CloudTtsClient.Synthesize(settings, text, clip => {...}, err => {...}));
        /// </summary>
        public static IEnumerator Synthesize(TtsSettings s, string text,
            Action<AudioClip> onSuccess, Action<string> onError)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.apiKey))
            {
                onError?.Invoke("请先配置 TTS 的 API Key");
                yield break;
            }
            if (string.IsNullOrWhiteSpace(s.voiceId))
            {
                onError?.Invoke("请先在「语音合成(TTS) 配置」里填写 voiceId（官方音色或声音复刻得到的ID）");
                yield break;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                onError?.Invoke("没有可合成的文本");
                yield break;
            }

            var req = new TtsRequest
            {
                model = s.model,
                text = text,
                stream = false,
                voice_setting = new VoiceSetting { voice_id = s.voiceId.Trim(), speed = s.speed, vol = 1f, pitch = 0 },
                audio_setting = new AudioSetting
                {
                    sample_rate = s.sampleRate,
                    bitrate = 128000,
                    format = "pcm",
                    channel = 1,
                },
            };

            string json = JsonUtility.ToJson(req);

            using var uwr = new UnityWebRequest(s.apiUrl.Trim(), "POST");
            uwr.timeout = 30;
            uwr.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            uwr.SetRequestHeader("Authorization", "Bearer " + s.apiKey.Trim());

            yield return uwr.SendWebRequest();

            if (uwr.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"TTS 请求失败(HTTP {uwr.responseCode}): " + Truncate(uwr.downloadHandler?.text ?? uwr.error, 200));
                yield break;
            }

            byte[] pcm = null;
            int sampleRate = s.sampleRate;
            try
            {
                var resp = JsonUtility.FromJson<TtsResponse>(uwr.downloadHandler.text);
                if (resp == null || resp.base_resp == null || resp.base_resp.status_code != 0)
                {
                    string msg = resp?.base_resp != null
                        ? $"status_code={resp.base_resp.status_code}, {resp.base_resp.status_msg}"
                        : "响应格式异常";
                    onError?.Invoke("TTS 合成失败: " + msg);
                    yield break;
                }
                if (string.IsNullOrEmpty(resp.data?.audio))
                {
                    onError?.Invoke("TTS 返回音频为空");
                    yield break;
                }

                // 接口返回 hex 编码的音频数据
                pcm = HexToBytes(resp.data.audio);
                if (pcm.Length == 0)
                {
                    onError?.Invoke("TTS 音频数据为空");
                    yield break;
                }
            }
            catch (Exception e)
            {
                onError?.Invoke("解析 TTS 响应失败: " + e.Message);
                yield break;
            }

            // 兼容：万一带 WAV 头（RIFF），截取 data 块（MiniMax 正常返回裸 PCM，这里兜底）
            if (pcm.Length > 44 && pcm[0] == (byte)'R' && pcm[1] == (byte)'I' && pcm[2] == (byte)'F' && pcm[3] == (byte)'F')
            {
                for (int i = 12; i < pcm.Length - 8; i++)
                {
                    if (pcm[i] == (byte)'d' && pcm[i + 1] == (byte)'a' && pcm[i + 2] == (byte)'t' && pcm[i + 3] == (byte)'a')
                    {
                        int dataLen = BitConverter.ToInt32(pcm, i + 4);
                        var trimmed = new byte[Math.Min(dataLen, pcm.Length - i - 8)];
                        Array.Copy(pcm, i + 8, trimmed, 0, trimmed.Length);
                        pcm = trimmed;
                        break;
                    }
                }
            }

            int sampleCount = pcm.Length / 2;
            if (sampleCount <= 0)
            {
                onError?.Invoke("TTS 音频数据为空");
                yield break;
            }

            try
            {
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short v = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                    samples[i] = v / 32768f;
                }

                var clip = AudioClip.Create("tts_" + DateTime.Now.Ticks, sampleCount, 1, sampleRate, false);
                clip.SetData(samples, 0);
                onSuccess?.Invoke(clip);
            }
            catch (Exception e)
            {
                onError?.Invoke("音频解码失败: " + e.Message);
            }
        }

        /// <summary>hex 字符串 → 字节数组</summary>
        private static byte[] HexToBytes(string hex)
        {
            int len = hex.Length / 2;
            byte[] bytes = new byte[len];
            for (int i = 0; i < len; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
