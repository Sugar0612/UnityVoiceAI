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
    /// Edge TTS 免费语音合成（微软 Edge 朗读接口，非官方、无需账号）。
    /// 默认女声 zh-CN-XiaoxiaoNeural（晓晓），输出 raw PCM 24kHz 单声道，
    /// 直接构建 AudioClip 播放（无需 MP3 解码）。
    /// 接口为非官方：若微软调整鉴权，需要同步更新 GenGecToken/版本号。
    /// </summary>
    public static class EdgeTtsClient
    {
        private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        private const string WssBase = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=" + TrustedClientToken;
        private const string GecVersion = "1-130.0.2849.68";
        private const string DefaultVoice = "zh-CN-XiaoxiaoNeural"; // 晓晓 · 女声
        private const string OutputFormat = "raw-24khz-16bit-mono-pcm";
        private const int SampleRate = 24000;

        /// <summary>协程入口：合成文本为 AudioClip。voice 传空用默认女声。</summary>
        public static IEnumerator Synthesize(string text, string voice,
            Action<AudioClip> onSuccess, Action<string> onError)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                onError?.Invoke("没有可合成的文本");
                yield break;
            }

            byte[] pcm = null;
            string error = null;
            var task = RunAsync(text, string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim(),
                b => pcm = b, e => error = e);
            while (!task.IsCompleted)
                yield return null;

            if (error != null)
            {
                onError?.Invoke(error);
                yield break;
            }
            if (pcm == null || pcm.Length == 0)
            {
                onError?.Invoke("Edge TTS 未返回音频数据");
                yield break;
            }

            // PCM16LE → float[]
            int sampleCount = pcm.Length / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;

            var clip = AudioClip.Create("edge_tts_" + DateTime.Now.Ticks, sampleCount, 1, SampleRate, false);
            clip.SetData(samples, 0);
            onSuccess?.Invoke(clip);
        }

        private static async Task RunAsync(string text, string voice,
            Action<byte[]> onPcm, Action<string> onError)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
                ws.Options.SetRequestHeader("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

                string url = WssBase
                             + "&Sec-MS-GEC=" + GenGecToken()
                             + "&Sec-MS-GEC-Version=" + GecVersion
                             + "&ConnectionId=" + Guid.NewGuid().ToString("N");
                await ws.ConnectAsync(new Uri(url), cts.Token);

                string reqId = Guid.NewGuid().ToString("N");
                string config = "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
                                "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
                                "\"outputFormat\":\"" + OutputFormat + "\"}}}}";
                await SendText(ws, "speech.config", reqId, config, cts.Token);

                string ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>" +
                              "<voice name=\"" + voice + "\">" +
                              System.Security.SecurityElement.Escape(text) + "</voice></speak>";
                await SendText(ws, "ssml", reqId, ssml, cts.Token);

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

                    var data = msg.ToArray();
                    if (res.MessageType == WebSocketMessageType.Text)
                    {
                        if (Encoding.UTF8.GetString(data).Contains("\"turn.end\""))
                            break;
                    }
                    else if (data.Length > 2)
                    {
                        // 二进制帧：2字节大端头长 + 头 + 音频载荷
                        int headerLen = (data[0] << 8) | data[1];
                        if (data.Length > headerLen + 2)
                            pcm.Write(data, 2 + headerLen, data.Length - headerLen - 2);
                    }
                }

                var outBytes = pcm.ToArray();
                if (outBytes.Length == 0)
                {
                    onError?.Invoke("Edge TTS 未返回音频数据");
                    return;
                }
                onPcm?.Invoke(outBytes);
            }
            catch (Exception e)
            {
                onError?.Invoke("Edge TTS 失败: " + e.Message);
            }
        }

        private static async Task SendText(WebSocket ws, string path, string reqId, string body, CancellationToken ct)
        {
            string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffK");
            string msg = "X-RequestId:" + reqId + "\r\nContent-Type:application/json; charset=utf-8\r\n" +
                         "Path:" + path + "\r\nX-Timestamp:" + ts + "\r\n\r\n" + body;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        /// <summary>Sec-MS-GEC 令牌：Windows FILETIME（1601纪元、100ns）向下取整5分钟窗口后拼接令牌做 SHA256</summary>
        private static string GenGecToken()
        {
            long secs = (long)(DateTime.UtcNow - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            secs -= secs % 300;
            string s = (secs * 10000000L).ToString() + TrustedClientToken;
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(s))).Replace("-", "").ToUpperInvariant();
        }
    }
}
