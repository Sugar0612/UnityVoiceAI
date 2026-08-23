using System;
using System.Collections;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VoiceAI
{
    /// <summary>
    /// 讯飞 WebSocket 语音识别客户端，支持两种协议：
    /// 1) 方言识别大模型（默认）：wss://iat.cn-huabei-1.xf-yun.com/v1，domain=slm，accent=mulacc（23种方言含陕西话）
    /// 2) 流式版听写 v2：wss://iat-api.xfyun.cn/v2/iat，domain=iat（免费额度备用）
    /// 鉴权：URL 查询参数 authorization/date/host（HMAC-SHA256，已真机验证）。
    /// 协议切换：SttSettings.iflyDomain = "slm" 或 "iat"，iflyWsUrl 配对应地址。
    /// </summary>
    public static class IflytekSttClient
    {
        private const int AudioChunkBytes = 2560; // 实测 2560B@10ms（8倍实时）服务器可接受

        // ============ 方言大模型 (slm) 帧结构 ============
        [Serializable] private class SlmHeader { public string app_id; public int status; }
        [Serializable] private class SlmIat
        {
            public string language = "zh_cn";
            public string accent = "mulacc";
            public string domain = "slm";
            public int eos = 1800;
            public string dwa = "wpg";
            public int ptt = 1;
            public int nunum = 1;
            public int ltc = 1;
            public SlmResult result = new SlmResult();
        }
        [Serializable] private class SlmResult { public string encoding = "utf8"; public string compress = "raw"; public string format = "json"; }
        [Serializable] private class SlmAudio
        {
            public string encoding = "raw";
            public int sample_rate = 16000;
            public int bit_depth = 16;
            public int status;
            public int seq;
            public string audio = "";
        }
        [Serializable] private class SlmFrame
        {
            public SlmHeader header;
            public SlmParamWrap parameter;
            public SlmPayloadWrap payload;
        }
        [Serializable] private class SlmParamWrap { public SlmIat iat; }
        [Serializable] private class SlmPayloadWrap { public SlmAudio audio; }

        // ============ 流式听写 v2 帧结构 ============
        [Serializable] private class V2Common { public string app_id; }
        [Serializable] private class V2Business
        {
            public string language = "zh_cn";
            public string domain = "iat";
            public string accent = "mandarin";
            public int vad_eos = 1800;
            public string dwa = "wpgs";
            public int ptt = 1;
        }
        [Serializable] private class V2Data
        {
            public int status;
            public string format = "audio/L16;rate=16000";
            public string encoding = "raw";
            public string audio = "";
        }
        [Serializable] private class V2Frame
        {
            public V2Common common;
            public V2Business business;
            public V2Data data;
        }

        // ============ 响应 ============
        [Serializable] private class RecvHeader { public int code; public string message; public int status; }
        [Serializable] private class RecvPayload { public RecvResult result; }
        [Serializable] private class RecvResult { public string text; public int status; }
        [Serializable] private class RecvFrame { public RecvHeader header; public RecvPayload payload; }

        [Serializable] private class V2Response { public int code; public string message; public int status; public V2RespData data; }
        [Serializable] private class V2RespData { public V2RespResult result; }
        [Serializable] private class V2RespResult { public WsItem[] ws; }

        [Serializable] private class InnerResult { public WsItem[] ws; }
        [Serializable] private class WsItem { public CwItem[] cw; }
        [Serializable] private class CwItem { public string w; }

        /// <summary>上传 WAV 识别，成功回调 onSuccess(text)。</summary>
        public static IEnumerator Transcribe(SttSettings s, byte[] wavBytes,
            Action<string> onSuccess, Action<string> onError)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.iflyAppId) ||
                string.IsNullOrWhiteSpace(s.iflyApiKey) || string.IsNullOrWhiteSpace(s.iflyApiSecret))
            {
                onError?.Invoke("请先配置讯飞的 AppID / APIKey / APISecret");
                yield break;
            }
            if (wavBytes == null || wavBytes.Length == 0)
            {
                onError?.Invoke("录音数据为空，请重试");
                yield break;
            }

            byte[] pcm = wavBytes;
            if (wavBytes.Length > 44 && wavBytes[0] == (byte)'R' && wavBytes[1] == (byte)'I' && wavBytes[2] == (byte)'F' && wavBytes[3] == (byte)'F')
            {
                for (int i = 12; i < wavBytes.Length - 8; i++)
                {
                    if (wavBytes[i] == (byte)'d' && wavBytes[i + 1] == (byte)'a' && wavBytes[i + 2] == (byte)'t' && wavBytes[i + 3] == (byte)'a')
                    {
                        int dataLen = BitConverter.ToInt32(wavBytes, i + 4);
                        var data = new byte[Math.Min(dataLen, wavBytes.Length - i - 8)];
                        Array.Copy(wavBytes, i + 8, data, 0, data.Length);
                        pcm = data;
                        break;
                    }
                }
            }
            if (pcm.Length == 0)
            {
                onError?.Invoke("音频数据为空");
                yield break;
            }

            string result = null;
            string error = null;
            bool done = false;

            RunAsync(s, pcm, r => { result = r; done = true; }, e => { error = e; done = true; });

            while (!done) yield return null;

            if (error != null)
                onError?.Invoke(error);
            else
                onSuccess?.Invoke(result);
        }

        private static async Task RunAsync(SttSettings s, byte[] pcm,
            Action<string> onSuccess, Action<string> onError)
        {
            try
            {
                bool v2 = s.iflyDomain.Trim().ToLowerInvariant() == "iat";
                string url = BuildAuthUrl(s, v2);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                using var ws = new ClientWebSocket();

                await ws.ConnectAsync(new Uri(url), cts.Token);
                Debug.Log("[VoiceAI] 讯飞(" + (v2 ? "流式听写" : "方言大模型") + ")已连接");

                string appId = s.iflyAppId.Trim();
                string accent = string.IsNullOrWhiteSpace(s.iflyAccent) ? (v2 ? "mandarin" : "mulacc") : s.iflyAccent.Trim();

                int totalChunks = Mathf.CeilToInt(pcm.Length / (float)AudioChunkBytes);
                int seq = 0;

                for (int i = 0; i < totalChunks; i++)
                {
                    int len = Math.Min(AudioChunkBytes, pcm.Length - i * AudioChunkBytes);
                    var chunk = new byte[len];
                    Array.Copy(pcm, i * AudioChunkBytes, chunk, 0, len);
                    int frameStatus = (i == totalChunks - 1) ? 2 : (i == 0 ? 0 : 1);

                    string json;
                    if (v2)
                    {
                        json = JsonUtility.ToJson(new V2Frame
                        {
                            common = new V2Common { app_id = appId },
                            business = new V2Business { accent = accent },
                            data = new V2Data { status = frameStatus, audio = Convert.ToBase64String(chunk) },
                        });
                    }
                    else
                    {
                        json = JsonUtility.ToJson(new SlmFrame
                        {
                            header = new SlmHeader { app_id = appId, status = frameStatus },
                            parameter = new SlmParamWrap { iat = new SlmIat { accent = accent } },
                            payload = new SlmPayloadWrap
                            {
                                audio = new SlmAudio { status = frameStatus, seq = seq++, audio = Convert.ToBase64String(chunk) },
                            },
                        });
                    }

                    byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                    await ws.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, cts.Token);

                    if (i < totalChunks - 1)
                        await Task.Delay(10, cts.Token); // 10ms 帧间隔（8倍实时速率，实测安全）
                }
                Debug.Log("[VoiceAI] 讯飞音频发送完毕: " + totalChunks + " 帧, " + pcm.Length + " 字节");

                // ---------- 接收结果 ----------
                var buffer = new byte[16384];
                var textSb = new StringBuilder();
                var msgSb = new StringBuilder();

                while (ws.State == WebSocketState.Open)
                {
                    var recv = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (recv.MessageType == WebSocketMessageType.Close) break;

                    msgSb.Append(Encoding.UTF8.GetString(buffer, 0, recv.Count));
                    if (!recv.EndOfMessage) continue;

                    string msg = msgSb.ToString();
                    msgSb.Clear();

                    Debug.Log("[VoiceAI] 讯飞返回: " + (msg.Length > 300 ? msg.Substring(0, 300) + "..." : msg));

                    if (v2)
                    {
                        V2Response r2 = null;
                        try { r2 = JsonUtility.FromJson<V2Response>(msg); } catch { }
                        if (r2 == null) continue;
                        if (r2.code != 0)
                        {
                            onError?.Invoke("讯飞识别失败: code=" + r2.code + ", " + r2.message);
                            return;
                        }
                        if (r2.status == 2)
                        {
                            string final = ExtractWsText(r2.data?.result?.ws);
                            if (!string.IsNullOrEmpty(final)) onSuccess?.Invoke(final);
                            else onError?.Invoke("讯飞识别结果为空");
                            return;
                        }
                    }
                    else
                    {
                        RecvFrame frame = null;
                        try { frame = JsonUtility.FromJson<RecvFrame>(msg); } catch { }
                        if (frame?.header == null) continue;

                        if (frame.header.code != 0)
                        {
                            onError?.Invoke("讯飞识别失败: code=" + frame.header.code + ", " + frame.header.message);
                            return;
                        }

                        string resultText = frame.payload?.result?.text;
                        if (!string.IsNullOrEmpty(resultText))
                        {
                            try
                            {
                                string innerJson = Encoding.UTF8.GetString(Convert.FromBase64String(resultText));
                                var inner = JsonUtility.FromJson<InnerResult>(innerJson);
                                AppendWsText(textSb, inner?.ws);
                            }
                            catch { }
                        }

                        if (frame.payload?.result?.status == 2 || frame.header.status == 2)
                        {
                            string final = textSb.ToString().Trim();
                            if (!string.IsNullOrEmpty(final)) onSuccess?.Invoke(final);
                            else onError?.Invoke("讯飞识别结果为空");
                            return;
                        }
                    }
                }

                onError?.Invoke("讯飞连接提前关闭");
            }
            catch (OperationCanceledException)
            {
                onError?.Invoke("讯飞识别超时（120秒）");
            }
            catch (Exception e)
            {
                onError?.Invoke("讯飞识别失败: " + e.Message);
            }
        }

        private static string ExtractWsText(WsItem[] ws)
        {
            var sb = new StringBuilder();
            AppendWsText(sb, ws);
            return sb.ToString().Trim();
        }

        private static void AppendWsText(StringBuilder sb, WsItem[] ws)
        {
            if (ws == null) return;
            foreach (var item in ws)
                if (item?.cw != null)
                    foreach (var cw in item.cw)
                        if (cw?.w != null) sb.Append(cw.w);
        }

        /// <summary>构建带鉴权参数的 WebSocket URL</summary>
        private static string BuildAuthUrl(SttSettings s, bool v2)
        {
            string host = v2 ? "iat-api.xfyun.cn" : "iat.cn-huabei-1.xf-yun.com";
            string path = v2 ? "/v2/iat" : "/v1";
            string date = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);

            string stringToSign = "host: " + host + "\ndate: " + date + "\nGET " + path + " HTTP/1.1";
            string signature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(s.iflyApiSecret.Trim())))
            {
                signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            }

            string authOrigin = "api_key=\"" + s.iflyApiKey.Trim() + "\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"" + signature + "\"";
            string authB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(authOrigin));

            return "wss://" + host + path +
                "?authorization=" + Uri.EscapeDataString(authB64) +
                "&date=" + Uri.EscapeDataString(date) +
                "&host=" + host;
        }
    }
}
