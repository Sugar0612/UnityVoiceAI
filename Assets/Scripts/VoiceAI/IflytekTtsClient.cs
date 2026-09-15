using System;
using System.Collections;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VoiceAI
{
    /// <summary>
    /// 讯飞在线语音合成（WebSocket v2，tt s-api.xfyun.cn/v2/tts）。
    /// 复用 SttSettings 里同一讯飞账号的 AppID/APIKey/APISecret；
    /// 需在讯飞控制台为该应用开通"在线语音合成"服务（有免费试用包）。
    /// 默认音色 xiaoyan（小燕·女声），输出 raw PCM 16kHz 单声道。
    /// </summary>
    public static class IflytekTtsClient
    {
        private const string Voice = "xiaoyan"; // 小燕 · 女声（基础免费音色）

        [Serializable]
        private class TtsReq
        {
            public Common common;
            public Business business;
            public DataReq data;

            [Serializable] public class Common { public string app_id; }
            [Serializable] public class Business { public string aue; public int sfl; public string vcn; public string tte; public int speed; }
            [Serializable] public class DataReq { public int status; public string text; }
        }

        [Serializable]
        private class TtsResp
        {
            public int code;
            public string message;
            public DataBody data;

            [Serializable] public class DataBody { public int status; public string audio; }
        }

        public static IEnumerator Synthesize(SttSettings s, string text,
            Action<AudioClip> onSuccess, Action<string> onError)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.iflyAppId) ||
                string.IsNullOrWhiteSpace(s.iflyApiKey) || string.IsNullOrWhiteSpace(s.iflyApiSecret))
            {
                onError?.Invoke("讯飞 TTS 未配置（AppID/APIKey/APISecret）");
                yield break;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                onError?.Invoke("没有可合成的文本");
                yield break;
            }

            byte[] pcm = null;
            string error = null;
            var task = Task.Run(() => RunAsync(s, text, b => pcm = b, e => error = e));
            while (!task.IsCompleted)
                yield return null;

            if (error != null)
            {
                onError?.Invoke(error);
                yield break;
            }
            if (pcm == null || pcm.Length == 0)
            {
                onError?.Invoke("讯飞 TTS 未返回音频数据");
                yield break;
            }

            int sampleCount = pcm.Length / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;

            var clip = AudioClip.Create("ifly_tts_" + DateTime.Now.Ticks, sampleCount, 1, 16000, false);
            clip.SetData(samples, 0);
            onSuccess?.Invoke(clip);
        }

        private static async Task RunAsync(SttSettings s, string text,
            Action<byte[]> onPcm, Action<string> onError)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var ws = new ClientWebSocket();
                string url = BuildAuthUrl(s);
                await ws.ConnectAsync(new Uri(url), cts.Token);

                var req = new TtsReq
                {
                    common = new TtsReq.Common { app_id = s.iflyAppId.Trim() },
                    business = new TtsReq.Business { aue = "raw", sfl = 1, vcn = Voice, tte = "UTF8", speed = 50 },
                    data = new TtsReq.DataReq
                    {
                        status = 2, // 一次性发送整段文本
                        text = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                    }
                };
                var reqBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(req));
                await ws.SendAsync(new ArraySegment<byte>(reqBytes), WebSocketMessageType.Text, true, cts.Token);

                var pcm = new MemoryStream();
                var buf = new byte[64 * 1024];
                var msg = new MemoryStream();
                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    WebSocketReceiveResult res;
                    msg.SetLength(0);
                    do
                    {
                        res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                        msg.Write(buf, 0, res.Count);
                    } while (!res.EndOfMessage);

                    if (res.MessageType == WebSocketMessageType.Close)
                        break;

                    var resp = JsonUtility.FromJson<TtsResp>(Encoding.UTF8.GetString(msg.ToArray()));
                    if (resp == null) continue;
                    if (resp.code != 0)
                    {
                        onError?.Invoke("讯飞 TTS 错误 " + resp.code + ": " + resp.message +
                                        "（请到 xfyun.cn 控制台确认已开通「在线语音合成」服务）");
                        return;
                    }
                    if (resp.data != null && !string.IsNullOrEmpty(resp.data.audio))
                    {
                        var audioBytes = Convert.FromBase64String(resp.data.audio);
                        pcm.Write(audioBytes, 0, audioBytes.Length);
                    }
                    if (resp.data != null && resp.data.status == 2)
                        break; // 音频结束
                }

                var outBytes = pcm.ToArray();
                if (outBytes.Length == 0)
                {
                    onError?.Invoke("讯飞 TTS 未返回音频数据");
                    return;
                }
                onPcm?.Invoke(outBytes);
            }
            catch (Exception e)
            {
                onError?.Invoke("讯飞 TTS 失败: " + e.Message);
            }
        }

        /// <summary>与 IflytekSttClient 相同的鉴权方案，主机/路径换成 TTS 服务</summary>
        private static string BuildAuthUrl(SttSettings s)
        {
            string host = "tts-api.xfyun.cn";
            string path = "/v2/tts";
            string date = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);
            string signatureOrigin = "host: " + host + "\ndate: " + date + "\nGET " + path + " HTTP/1.1";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(s.iflyApiSecret.Trim()));
            var sigBody = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin));
            string signature = Convert.ToBase64String(sigBody);
            string authOrigin = "api_key=\"" + s.iflyApiKey.Trim() + "\", algorithm=\"hmac-sha256\", " +
                                "headers=\"host date request-line\", signature=\"" + signature + "\"";
            string authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authOrigin));

            return "wss://" + host + path +
                   "?authorization=" + Uri.EscapeDataString(authorization) +
                   "&date=" + Uri.EscapeDataString(date) +
                   "&host=" + host;
        }
    }
}
