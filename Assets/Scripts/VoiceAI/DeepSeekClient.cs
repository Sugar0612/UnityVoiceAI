using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace VoiceAI
{
    /// <summary>DeepSeek 接口配置（在 Inspector 中填写）</summary>
    [Serializable]
    public class DeepSeekSettings
    {
        [Tooltip("DeepSeek 平台申请的 API Key，格式 sk-...（注意：打包进 APK 会被反编译，正式发布建议放服务器中转）")]
        public string apiKey = "";

        [Tooltip("接口地址（官方 OpenAI 兼容端点）")]
        public string apiUrl = "https://api.deepseek.com/chat/completions";

        [Tooltip("模型：deepseek-chat(对话) / deepseek-reasoner(推理)")]
        public string model = "deepseek-chat";

        public int maxTokens = 1024;
        public float temperature = 0.7f;

        [TextArea(2, 6)]
        [Tooltip("AI 角色设定（医院场景：专业医师助手）")]
        public string personaPrompt =
            "你是医院里的一名专业医师语音助手，名叫'何夕月'，为患者和家属解答健康问题。要求：" +
            "1.站在专业医师角度回答，用语严谨但通俗，不堆砌术语；" +
            "2.回答简洁口语化，适合语音播报，控制在100字以内；" +
            "3.给出用药、诊断类建议时，提醒患者以医生当面诊断为准；" +
            "4.若患者描述胸痛、呼吸困难、大出血、意识不清等急症症状，立即建议拨打120或马上前往急诊，不做多余展开；" +
            "5.基于循证医学客观回答，不夸大病情、不制造恐慌，也不回避问题。";
    }

    /// <summary>DeepSeek Chat API 的轻量客户端（OpenAI 兼容格式，非流式）</summary>
    public static class DeepSeekClient
    {
        [Serializable]
        private class ChatRequest
        {
            public string model;
            public ChatMessage[] messages;
            public int max_tokens;
            public float temperature;
            public bool stream;
        }

        [Serializable]
        private class ChatMessage
        {
            public string role;
            public string content;
        }

        [Serializable]
        public class ChatResponse
        {
            public string id;
            public string model;
            public Choice[] choices;
        }

        [Serializable]
        public class Choice
        {
            public int index;
            public RespMessage message;
            public string finish_reason;
        }

        [Serializable]
        public class RespMessage
        {
            public string role;
            public string content;
        }

        /// <summary>
        /// 发送一条用户消息，收到完整回复后回调 onSuccess(reply)。
        /// 用法：StartCoroutine(DeepSeekClient.SendMessage(settings, text, ok => {...}, err => {...}));
        /// </summary>
        public static IEnumerator SendMessage(DeepSeekSettings settings, string userText,
            Action<string> onSuccess, Action<string> onError)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.apiKey))
            {
                onError?.Invoke("请先在 Inspector 里配置 DeepSeek API Key");
                yield break;
            }

            var request = new ChatRequest
            {
                model = settings.model,
                max_tokens = Mathf.Max(1, settings.maxTokens),
                temperature = settings.temperature,
                stream = false,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = settings.personaPrompt },
                    new ChatMessage { role = "user", content = userText },
                },
            };

            string json = JsonUtility.ToJson(request);
            byte[] body = Encoding.UTF8.GetBytes(json);

            using var uwr = new UnityWebRequest(settings.apiUrl.Trim(), "POST");
            uwr.timeout = 30;
            uwr.uploadHandler = new UploadHandlerRaw(body);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            uwr.SetRequestHeader("Authorization", "Bearer " + settings.apiKey.Trim());

            yield return uwr.SendWebRequest();

            if (uwr.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var resp = JsonUtility.FromJson<ChatResponse>(uwr.downloadHandler.text);
                    string content = resp != null && resp.choices != null && resp.choices.Length > 0
                        ? resp.choices[0].message?.content
                        : null;
                    if (!string.IsNullOrWhiteSpace(content))
                        onSuccess?.Invoke(content.Trim());
                    else
                        onError?.Invoke("DeepSeek 返回内容为空");
                }
                catch (Exception e)
                {
                    onError?.Invoke("解析 DeepSeek 响应失败: " + e.Message);
                }
            }
            else
            {
                string detail = string.IsNullOrEmpty(uwr.downloadHandler?.text)
                    ? uwr.error
                    : uwr.downloadHandler.text;
                onError?.Invoke($"DeepSeek 请求失败(HTTP {uwr.responseCode}): {Truncate(detail, 200)}");
            }
        }

        // ================= 流式回复（SSE） =================

        [Serializable]
        private class StreamChunk
        {
            public StreamChoice[] choices;
        }

        [Serializable]
        private class StreamChoice
        {
            public StreamDelta delta;
            public string finish_reason;
        }

        [Serializable]
        private class StreamDelta
        {
            public string content;
        }

        /// <summary>
        /// 流式发送：回复内容边生成边回调 onDelta(增量文字)，
        /// 全部完成后回调 onComplete(完整文字)。失败回调 onError。
        /// 用法：StartCoroutine(DeepSeekClient.SendMessageStream(settings, text,
        ///        delta => { ... }, full => { ... }, err => { ... }));
        /// </summary>
        public static IEnumerator SendMessageStream(DeepSeekSettings settings, string userText,
            Action<string> onDelta, Action<string> onComplete, Action<string> onError)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.apiKey))
            {
                onError?.Invoke("请先在 Inspector 里配置 DeepSeek API Key");
                yield break;
            }

            var request = new ChatRequest
            {
                model = settings.model,
                max_tokens = Mathf.Max(1, settings.maxTokens),
                temperature = settings.temperature,
                stream = true,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = settings.personaPrompt },
                    new ChatMessage { role = "user", content = userText },
                },
            };

            string json = JsonUtility.ToJson(request);
            byte[] body = Encoding.UTF8.GetBytes(json);

            using var uwr = new UnityWebRequest(settings.apiUrl.Trim(), "POST");
            uwr.timeout = 60;
            uwr.uploadHandler = new UploadHandlerRaw(body);
            uwr.downloadHandler = new SseDownloadHandler();
            uwr.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            uwr.SetRequestHeader("Authorization", "Bearer " + settings.apiKey.Trim());
            uwr.SetRequestHeader("Accept", "text/event-stream");

            var fullText = new StringBuilder();
            bool done = false;

            var sse = (SseDownloadHandler)uwr.downloadHandler;
            var op = uwr.SendWebRequest();

            // 边收边处理：SSE 数据到达即解析；收到 [DONE] 或 finish_reason=stop 立即结束，
            // 不等待连接关闭（DeepSeek 会保持连接，等待会导致超时）
            float deadline = Time.unscaledTime + 90f;
            while (!sse.IsFinished && Time.unscaledTime < deadline)
            {
                if (op.isDone && uwr.result != UnityWebRequest.Result.InProgress)
                    break; // 请求结束（成功或失败），处理剩余事件
                yield return null;
            }

            if (Time.unscaledTime >= deadline && !sse.IsFinished)
            {
                onError?.Invoke("DeepSeek 响应超时（90秒）");
                yield break;
            }

            if (uwr.result != UnityWebRequest.Result.Success && !sse.IsFinished)
            {
                string detail = string.IsNullOrEmpty(uwr.downloadHandler?.text)
                    ? uwr.error
                    : uwr.downloadHandler.text;
                onError?.Invoke($"DeepSeek 请求失败(HTTP {uwr.responseCode}): {Truncate(detail, 200)}");
                yield break;
            }

            // 处理所有已到达的 SSE 事件
            foreach (string payload in sse.Events)
            {
                if (payload == "[DONE]") { done = true; break; }

                try
                {
                    var chunk = JsonUtility.FromJson<StreamChunk>(payload);
                    if (chunk?.choices == null || chunk.choices.Length == 0) continue;

                    string delta = chunk.choices[0].delta?.content;
                    if (!string.IsNullOrEmpty(delta))
                    {
                        fullText.Append(delta);
                        onDelta?.Invoke(delta);
                    }

                    if (chunk.choices[0].finish_reason == "stop")
                    {
                        done = true;
                        break;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[VoiceAI] 流式解析跳过一段: " + e.Message);
                }
            }

            string result = fullText.ToString().Trim();
            if (string.IsNullOrEmpty(result) && !done)
            {
                // 可能服务端没走流式，直接返回了完整 JSON（兜底解析）
                string body2 = sse.FallbackBody;
                if (!string.IsNullOrEmpty(body2))
                {
                    try
                    {
                        var resp = JsonUtility.FromJson<ChatResponse>(body2);
                        result = resp != null && resp.choices != null && resp.choices.Length > 0
                            ? resp.choices[0].message?.content
                            : null;
                        if (!string.IsNullOrEmpty(result)) result = result.Trim();
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke("解析 DeepSeek 响应失败: " + e.Message);
                        yield break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(result))
                onComplete?.Invoke(result);
            else
                onError?.Invoke("DeepSeek 返回内容为空");
        }

        /// <summary>
        /// SSE 下载处理器：按行累积 data: 负载，结束后可通过 Events 逐条读取。
        /// 若响应不是 SSE（如错误 JSON），原始内容保存在 FallbackBody。
        /// </summary>
        private class SseDownloadHandler : DownloadHandlerScript
        {
            private readonly System.Text.StringBuilder _lineBuffer = new System.Text.StringBuilder();
            private readonly System.Text.StringBuilder _fallback = new System.Text.StringBuilder();
            private readonly List<string> _events = new List<string>();

            public IReadOnlyList<string> Events => _events;
            public string FallbackBody => _fallback.ToString();
            public bool IsFinished { get; private set; }

            public SseDownloadHandler() : base(new byte[8192]) { }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return false;

                string chunk = Encoding.UTF8.GetString(data, 0, dataLength);
                _fallback.Append(chunk);
                _lineBuffer.Append(chunk);

                string s = _lineBuffer.ToString();
                int idx;
                while ((idx = s.IndexOf('\n')) >= 0)
                {
                    string line = s.Substring(0, idx).TrimEnd('\r');
                    s = s.Substring(idx + 1);

                    if (line.StartsWith("data:"))
                    {
                        string payload = line.Substring(5).Trim();
                        if (payload.Length > 0)
                        {
                            _events.Add(payload);
                            // 流结束标记：收到 [DONE] 或 finish_reason=stop
                            if (payload == "[DONE]") IsFinished = true;
                            else if (payload.IndexOf("\"finish_reason\":\"stop\"", StringComparison.Ordinal) >= 0)
                                IsFinished = true;
                        }
                    }
                }
                _lineBuffer.Clear();
                _lineBuffer.Append(s);
                return true;
            }

            protected override byte[] GetData() => null;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
