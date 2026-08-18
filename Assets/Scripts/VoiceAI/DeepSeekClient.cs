using System;
using System.Collections;
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

        [TextArea(1, 4)]
        public string systemPrompt = "你是一个友好的中文语音助手，回答请简洁、口语化，控制在100字以内。";
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
                    new ChatMessage { role = "system", content = settings.systemPrompt },
                    new ChatMessage { role = "user", content = userText },
                },
            };

            string json = JsonUtility.ToJson(request);
            byte[] body = Encoding.UTF8.GetBytes(json);

            using var uwr = new UnityWebRequest(settings.apiUrl.Trim(), "POST");
            uwr.timeout = 60;
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

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
