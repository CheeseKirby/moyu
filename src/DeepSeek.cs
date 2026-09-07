using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class TranslationResult
    {
        public Dictionary<string, string> Translations = new Dictionary<string, string>();
        public Dictionary<string, string> GlossaryUpdates = new Dictionary<string, string>();
    }

    internal sealed class DeepSeekClient
    {
        private readonly AppConfig config;
        private readonly string apiKey;

        public DeepSeekClient(AppConfig config, string apiKey)
        {
            this.config = config;
            this.apiKey = apiKey;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }

        public TranslationResult TranslateScene(IList<SubtitleCue> cues, SubtitleScene scene, int contextCount, IDictionary<string, string> glossary, CancellationToken cancellation)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = config.Model;
            payload["temperature"] = 0.2;
            payload["max_tokens"] = 4096;
            payload["thinking"] = new Dictionary<string, object> { { "type", "disabled" } };
            payload["response_format"] = new Dictionary<string, object> { { "type", "json_object" } };

            List<object> messages = new List<object>();
            messages.Add(new Dictionary<string, object>
            {
                { "role", "system" },
                { "content", BuildSystemPrompt() }
            });
            messages.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", BuildUserPrompt(cues, scene, contextCount, glossary) }
            });
            payload["messages"] = messages;

            string endpoint = ApiEndpoint.ChatCompletions(config.ApiBaseUrl);
            byte[] requestBody = Encoding.UTF8.GetBytes(AtomicJson.Serialize(payload));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
            request.Timeout = 120000;
            request.ReadWriteTimeout = 120000;
            request.ContentLength = requestBody.Length;

            using (cancellation.Register(delegate { try { request.Abort(); } catch { } }))
            {
                using (Stream stream = request.GetRequestStream()) stream.Write(requestBody, 0, requestBody.Length);
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        return ParseResponse(reader.ReadToEnd(), cues, scene);
                    }
                }
                catch (WebException ex)
                {
                    string detail = ex.Message;
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        try
                        {
                            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                            {
                                string body = reader.ReadToEnd();
                                if (body.Length > 1000) body = body.Substring(0, 1000);
                                detail = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + body;
                            }
                        }
                        catch { }
                    }
                    throw new InvalidOperationException("DeepSeek API 请求失败：" + detail, ex);
                }
            }
        }

        internal string BuildSystemPrompt()
        {
            return "你是" + SourceLanguages.Get(config.SourceLanguage).Name + "影视字幕翻译器。把目标字幕翻译为自然、简洁、符合场景的简体中文。" +
                   "必须保持每个目标字幕的 id，一条不漏，不合并，不添加时间轴。" +
                   "上下文仅用于理解，不能把上下文作为目标重复输出。" +
                   "人名、称呼、语气和术语在同一作品中保持一致。重复的拟声词或语气音只保留两到三次，每条译文不超过八十个汉字。" +
                   "只返回 JSON 对象，格式为 {\"translations\":[{\"id\":1,\"zh\":\"译文\"}],\"glossary_updates\":{\"源语言术语\":\"中文译法\"}}。";
        }

        internal string BuildUserPrompt(IList<SubtitleCue> cues, SubtitleScene scene, int contextCount, IDictionary<string, string> glossary)
        {
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["task"] = "translate_target_cues_to_Simplified_Chinese";
            request["source_language"] = config.SourceLanguage;
            request["glossary"] = glossary;
            request["context_before"] = CueObjects(cues, Math.Max(0, scene.StartCueIndex - contextCount), scene.StartCueIndex - 1);
            request["target_cues"] = CueObjects(cues, scene.StartCueIndex, scene.EndCueIndex);
            request["context_after"] = CueObjects(cues, scene.EndCueIndex + 1, Math.Min(cues.Count - 1, scene.EndCueIndex + contextCount));
            return AtomicJson.Serialize(request);
        }

        private static List<object> CueObjects(IList<SubtitleCue> cues, int start, int end)
        {
            List<object> list = new List<object>();
            if (start > end || start < 0 || end >= cues.Count) return list;
            for (int i = start; i <= end; i++)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "id", cues[i].Id },
                    { "text", CompactCueText(cues[i].Text) }
                });
            }
            return list;
        }

        private static TranslationResult ParseResponse(string responseJson, IList<SubtitleCue> cues, SubtitleScene scene)
        {
            Dictionary<string, object> root = AtomicJson.DeserializeObject(responseJson) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("DeepSeek 返回内容不是 JSON 对象。");
            object choicesObject;
            if (!root.TryGetValue("choices", out choicesObject)) throw new InvalidDataException("DeepSeek 返回中缺少 choices。");
            object[] choices = choicesObject as object[];
            if (choices == null || choices.Length == 0) throw new InvalidDataException("DeepSeek 没有返回翻译结果。");
            Dictionary<string, object> choice = choices[0] as Dictionary<string, object>;
            Dictionary<string, object> message = choice == null ? null : choice["message"] as Dictionary<string, object>;
            string content = message == null || !message.ContainsKey("content") ? null : Convert.ToString(message["content"], CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("DeepSeek 返回的翻译内容为空。");
            content = StripCodeFence(content.Trim());

            Dictionary<string, object> translatedRoot;
            try { translatedRoot = AtomicJson.DeserializeObject(content) as Dictionary<string, object>; }
            catch { throw new InvalidDataException("DeepSeek 翻译结果 JSON 不完整或格式错误。"); }
            if (translatedRoot == null) throw new InvalidDataException("DeepSeek 翻译结果不是要求的 JSON 对象。");
            object translationsObject;
            if (!translatedRoot.TryGetValue("translations", out translationsObject)) throw new InvalidDataException("DeepSeek 翻译结果缺少 translations。");
            object[] rows = translationsObject as object[];
            if (rows == null) throw new InvalidDataException("DeepSeek translations 格式错误。");

            TranslationResult result = new TranslationResult();
            foreach (object rowObject in rows)
            {
                Dictionary<string, object> row = rowObject as Dictionary<string, object>;
                if (row == null || !row.ContainsKey("id") || !row.ContainsKey("zh")) continue;
                string id = Convert.ToString(row["id"], CultureInfo.InvariantCulture);
                string text = Convert.ToString(row["zh"], CultureInfo.InvariantCulture).Trim();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(text)) result.Translations[id] = text;
            }

            for (int i = scene.StartCueIndex; i <= scene.EndCueIndex; i++)
            {
                string id = cues[i].Id.ToString(CultureInfo.InvariantCulture);
                if (!result.Translations.ContainsKey(id)) throw new InvalidDataException("DeepSeek 漏译字幕编号 " + id + "。");
            }
            if (result.Translations.Count != scene.EndCueIndex - scene.StartCueIndex + 1)
                throw new InvalidDataException("DeepSeek 返回的字幕数量与目标场景不一致。");

            object glossaryObject;
            if (translatedRoot.TryGetValue("glossary_updates", out glossaryObject))
            {
                Dictionary<string, object> updates = glossaryObject as Dictionary<string, object>;
                if (updates != null)
                {
                    foreach (KeyValuePair<string, object> item in updates)
                    {
                        string value = Convert.ToString(item.Value, CultureInfo.InvariantCulture).Trim();
                        if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(value)) result.GlossaryUpdates[item.Key] = value;
                    }
                }
            }
            return result;
        }


        private static string CompactCueText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string trimmed = text.Trim();
            if (trimmed.Length <= 80) return trimmed;

            HashSet<char> meaningful = new HashSet<char>();
            foreach (char c in trimmed)
            {
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)) meaningful.Add(c);
                if (meaningful.Count > 6) break;
            }
            if (meaningful.Count <= 6)
            {
                string sample = new string(trimmed.Where(delegate(char c) { return !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c); }).Take(6).ToArray());
                return (string.IsNullOrEmpty(sample) ? "拟声" : sample) + "（原文为大量重复拟声）";
            }
            return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500) + "…";
        }
        private static string StripCodeFence(string content)
        {
            if (!content.StartsWith("```", StringComparison.Ordinal)) return content;
            int firstNewline = content.IndexOf('\n');
            int lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline) return content.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            return content;
        }
    }
}
