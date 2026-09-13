using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace PotPlayerAiSubtitle
{
    internal sealed class TranslationResult
    {
        public Dictionary<string, string> Translations = new Dictionary<string, string>();
        public Dictionary<string, string> GlossaryUpdates = new Dictionary<string, string>();
    }

    internal sealed class TranslationReviewIssue
    {
        public int CueId { get; set; }
        public string Kind { get; set; }
        public string Suggestion { get; set; }
        public bool NeedsSourceCheck { get; set; }
    }

    internal sealed class TranslationOutputLimitException : Exception
    {
        public TranslationOutputLimitException(string message) : base(message) { }
    }

    internal sealed class DeepSeekClient
    {
        private readonly AppConfig config;
        private readonly string apiKey;
        private readonly bool enableThinking;
        private readonly ReviewBudget reviewBudget;

        public DeepSeekClient(AppConfig config, string apiKey, bool enableThinking = false, ReviewBudget reviewBudget = null)
        {
            this.config = config;
            this.reviewBudget = reviewBudget;
            this.apiKey = apiKey;
            // Keep the saved preference, but never spend reasoning tokens in the fast tier.
            this.enableThinking = enableThinking && string.Equals(config.TranslationQuality, "quality", StringComparison.OrdinalIgnoreCase);
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }

        public TranslationResult TranslateScene(IList<SubtitleCue> cues, SubtitleScene scene, int contextCount, IDictionary<string, string> glossary, CancellationToken cancellation)
        {
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
            string content = PostChat(messages, cancellation);
            return ParseResponse(content, cues, scene);
        }

        // Probe the raw message content without strict parsing (used by the thinking-mode spike).
        public string ProbeTranslation(IList<SubtitleCue> cues, SubtitleScene scene, int contextCount, IDictionary<string, string> glossary, CancellationToken cancellation)
        {
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
            return PostChat(messages, cancellation);
        }

        public Dictionary<string, string> RepairCues(IList<SubtitleCue> cues, IList<int> cueIds, int contextCount, IDictionary<string, string> glossary, IDictionary<string, string> translations, CancellationToken cancellation, IDictionary<int, string> suggestions = null)
        {
            if (cues == null || cueIds == null || cueIds.Count == 0) return new Dictionary<string, string>();
            List<object> messages = new List<object>();
            messages.Add(new Dictionary<string, object>
            {
                { "role", "system" },
                { "content", BuildRepairSystemPrompt() }
            });
            messages.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", BuildRepairUserPrompt(cues, cueIds, contextCount, glossary, translations, suggestions) }
            });
            string content = PostChat(messages, cancellation);
            return ParseRepairResponse(content, cues, cueIds);
        }

        public List<TranslationReviewIssue> ReviewWindow(IList<SubtitleCue> cues, SubtitleScene scene, IDictionary<string, string> translations, IDictionary<string, string> glossary, int contextCount, CancellationToken cancellation)
        {
            List<object> messages = new List<object>();
            messages.Add(new Dictionary<string, object>
            {
                { "role", "system" },
                { "content", BuildReviewSystemPrompt() }
            });
            messages.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", BuildReviewUserPrompt(cues, scene, translations, glossary, contextCount) }
            });
            string content = PostChat(messages, cancellation);
            return ParseReviewResponse(content, cues, scene);
        }

        private string PostChat(List<object> messages, CancellationToken cancellation)
        {
            if (reviewBudget == null) return PostChatWithRetry(messages, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (reviewBudget.RemainingMilliseconds <= 0) throw new ReviewBudgetException("重点复核时间预算已用尽");
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(reviewBudget.RemainingMilliseconds);
                try { return PostChatWithRetry(messages, deadline.Token); }
                catch (OperationCanceledException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    throw new ReviewBudgetException("重点复核时间预算已用尽，保留已有译文");
                }
            }
        }

        private string PostChatWithRetry(List<object> messages, CancellationToken cancellation)
        {
            int budget = enableThinking ? 16384 : 4096;
            int limit = budget * 2;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                try { return PostChat(messages, cancellation, budget); }
                catch (TranslationOutputLimitException ex)
                {
                    if (budget >= limit)
                        throw new TranslationOutputLimitException(ex.Message + " 已达到本次请求预算上限；请减小场景字幕数，或关闭质量档思考后重试。");
                    budget *= 2;
                    Logger.Write(ex.Message + " Retrying once with max_tokens=" + budget);
                }
            }
        }

        private string PostChat(List<object> messages, CancellationToken cancellation, int maxTokens)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = config.Model;
            payload["temperature"] = 0.2;
            payload["max_tokens"] = maxTokens;
            payload["thinking"] = new Dictionary<string, object> { { "type", enableThinking ? "enabled" : "disabled" } };
            payload["response_format"] = new Dictionary<string, object> { { "type", "json_object" } };
            payload["messages"] = messages;

            string endpoint = ApiEndpoint.ChatCompletions(config.ApiBaseUrl);
            byte[] requestBody = Encoding.UTF8.GetBytes(AtomicJson.Serialize(payload));
            cancellation.ThrowIfCancellationRequested();
            if (reviewBudget != null) reviewBudget.Reserve(maxTokens, Encoding.UTF8.GetCharCount(requestBody));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
            request.Timeout = enableThinking ? 300000 : 120000;
            request.ReadWriteTimeout = request.Timeout;
            request.ContentLength = requestBody.Length;

            using (cancellation.Register(delegate { try { request.Abort(); } catch { } }))
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    using (Stream stream = request.GetRequestStream()) stream.Write(requestBody, 0, requestBody.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        if (reviewBudget != null) reviewBudget.Observe(json);
                        return ExtractContent(json, maxTokens);
                    }
                }
                catch (WebException ex)
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Never put provider bodies in ordinary logs: gateways may echo submitted text or keys.
                    string detail = ex.Status.ToString();
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        detail = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                        response.Close();
                    }
                    throw new InvalidOperationException("DeepSeek API 请求失败：" + detail, ex);
                }
                catch (IOException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        private static string ExtractContent(string responseJson, int maxTokens)
        {
            Dictionary<string, object> root = AtomicJson.DeserializeObject(responseJson) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("DeepSeek 返回内容不是 JSON 对象。");
            object choicesObject;
            if (!root.TryGetValue("choices", out choicesObject)) throw new InvalidDataException("DeepSeek 返回中缺少 choices。");
            object[] choices = choicesObject as object[];
            if (choices == null || choices.Length == 0) throw new InvalidDataException("DeepSeek 没有返回翻译结果。");
            Dictionary<string, object> choice = choices[0] as Dictionary<string, object>;
            Dictionary<string, object> message = choice == null || !choice.ContainsKey("message") ? null : choice["message"] as Dictionary<string, object>;
            string content = message == null || !message.ContainsKey("content") ? null : Convert.ToString(message["content"], CultureInfo.InvariantCulture);
            string finishReason = choice == null || !choice.ContainsKey("finish_reason") ? "" : Convert.ToString(choice["finish_reason"], CultureInfo.InvariantCulture);
            Dictionary<string, object> usage = root.ContainsKey("usage") ? root["usage"] as Dictionary<string, object> : null;
            Dictionary<string, object> details = usage != null && usage.ContainsKey("completion_tokens_details") ? usage["completion_tokens_details"] as Dictionary<string, object> : null;
            // Diagnostics contain only counts, never prompts, reasoning text or credentials.
            string diagnostic = string.Format(CultureInfo.InvariantCulture, "max_tokens={0}, completion_tokens={1}, reasoning_tokens={2}, content_chars={3}",
                maxTokens, TokenCount(usage, "completion_tokens"), TokenCount(details, "reasoning_tokens"), content == null ? 0 : content.Length);
            if (finishReason == "length")
                throw new TranslationOutputLimitException("DeepSeek 输出预算耗尽，译文未完整生成（finish_reason=length, " + diagnostic + "）。");
            if (!string.IsNullOrEmpty(finishReason) && finishReason != "stop")
                throw new InvalidDataException("DeepSeek 未正常完成译文，请检查模型响应状态（" + diagnostic + "）。");
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException("DeepSeek 未返回译文，响应未标记为输出截断（" + diagnostic + "）。");
            return StripCodeFence(content.Trim());
        }

        private static string TokenCount(Dictionary<string, object> values, string name)
        {
            long count;
            return values != null && values.ContainsKey(name) && long.TryParse(Convert.ToString(values[name], CultureInfo.InvariantCulture), out count)
                ? count.ToString(CultureInfo.InvariantCulture) : "unknown";
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

        internal string BuildRepairSystemPrompt()
        {
            return "你是" + SourceLanguages.Get(config.SourceLanguage).Name + "影视字幕修复器。只修改提供的目标字幕，保持 id 与顺序不变，不新增、不删除、不合并字幕。" +
                   "1) 把仍残留在译文里的源语言词（日文假名或韩文）替换成对应简体中文；" +
                   "2) 明显过长（超过九十六字）或内容异常时压缩到八十字以内，保留原意；" +
                   "3) 大段重复的拟声词或语气音只保留两到三次。" +
                   "参考每条字幕给出的 current_zh；若提供了 suggestion，仅在现有源文和上下文支持时修改，不要猜测误听的原文，不确定则返回 current_zh。尽量贴近原译法、不要整句重写。" +
                   "只返回 JSON 对象，格式为 {\"translations\":[{\"id\":1,\"zh\":\"译文\"}]}。";
        }

        internal string BuildRepairUserPrompt(IList<SubtitleCue> cues, IList<int> cueIds, int contextCount, IDictionary<string, string> glossary, IDictionary<string, string> translations, IDictionary<int, string> suggestions)
        {
            HashSet<int> wanted = new HashSet<int>(cueIds);
            List<object> targets = new List<object>();
            int minPos = int.MaxValue;
            int maxPos = int.MinValue;
            for (int i = 0; i < cues.Count; i++)
            {
                if (!wanted.Contains(cues[i].Id)) continue;
                Dictionary<string, object> target = new Dictionary<string, object>
                {
                    { "id", cues[i].Id },
                    { "text", CompactCueText(cues[i].Text) }
                };
                string current;
                if (translations != null && translations.TryGetValue(cues[i].Id.ToString(CultureInfo.InvariantCulture), out current))
                    target["current_zh"] = current;
                string suggestion;
                if (suggestions != null && suggestions.TryGetValue(cues[i].Id, out suggestion))
                    target["suggestion"] = suggestion;
                target["context_before"] = ReviewCueObjects(cues, Math.Max(0, i - Math.Min(2, contextCount)), i - 1, translations);
                target["context_after"] = ReviewCueObjects(cues, i + 1, Math.Min(cues.Count - 1, i + Math.Min(2, contextCount)), translations);
                targets.Add(target);
                if (i < minPos) minPos = i;
                if (i > maxPos) maxPos = i;
            }

            Dictionary<string, object> request = new Dictionary<string, object>();
            request["task"] = "repair_target_cues";
            request["source_language"] = config.SourceLanguage;
            request["glossary"] = glossary;
            request["context_before"] = CueObjects(cues, Math.Max(0, minPos - contextCount), minPos - 1);
            request["target_cues"] = targets;
            request["context_after"] = CueObjects(cues, maxPos + 1, Math.Min(cues.Count - 1, maxPos + contextCount));
            return AtomicJson.Serialize(request);
        }

        internal string BuildReviewSystemPrompt()
        {
            return "你是" + SourceLanguages.Get(config.SourceLanguage).Name + "影视字幕翻译复核员。仅审校 window_cues 内的目标字幕，issues 中的 id 必须来自 window_cues。context_before 与 context_after 仅辅助理解，绝对不要报告这些上下文条目的问题。" +
                   "重点检查：1) 专名、称呼、术语是否全程统一（glossary 是候选参考，先确认词义与对象是否相同）；" +
                   "2) 错译、漏意、语气或角色声线不对、指代或代词错乱；3) 生硬或辞不达意。" +
                   "只标记确定有问题的条目，不因个人文风偏好改写；不得凭猜测纠正识别原文，原文疑似误听、残缺或无法确定时必须标记 needs_source_check=true；可仅凭现有文本确定修正时标记 false。术语表只是候选，遇到词义、称呼或指代不适用时不要强行替换。给出简洁的修改建议。只返回 JSON 对象，格式为 {\"issues\":[{\"id\":1,\"kind\":\"问题类型\",\"suggestion\":\"修改建议\",\"needs_source_check\":false}]}。";
        }

        internal string BuildReviewUserPrompt(IList<SubtitleCue> cues, SubtitleScene scene, IDictionary<string, string> translations, IDictionary<string, string> glossary, int contextCount)
        {
            List<object> window = new List<object>();
            for (int i = scene.StartCueIndex; i <= scene.EndCueIndex; i++)
            {
                window.Add(ReviewCueObject(cues[i], translations));
            }
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["task"] = "review_translations";
            request["source_language"] = config.SourceLanguage;
            request["glossary"] = glossary ?? new Dictionary<string, string>();
            request["context_before"] = ReviewCueObjects(cues, Math.Max(0, scene.StartCueIndex - contextCount), scene.StartCueIndex - 1, translations);
            request["window_cues"] = window;
            request["context_after"] = ReviewCueObjects(cues, scene.EndCueIndex + 1, Math.Min(cues.Count - 1, scene.EndCueIndex + contextCount), translations);
            return AtomicJson.Serialize(request);
        }

        private static List<object> ReviewCueObjects(IList<SubtitleCue> cues, int start, int end, IDictionary<string, string> translations)
        {
            List<object> list = new List<object>();
            if (start > end || start < 0 || end >= cues.Count) return list;
            for (int i = start; i <= end; i++) list.Add(ReviewCueObject(cues[i], translations));
            return list;
        }

        private static object ReviewCueObject(SubtitleCue cue, IDictionary<string, string> translations)
        {
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["id"] = cue.Id;
            item["text"] = CompactCueText(cue.Text);
            string zh;
            if (translations != null && translations.TryGetValue(cue.Id.ToString(CultureInfo.InvariantCulture), out zh))
                item["zh"] = zh;
            return item;
        }

        private static List<TranslationReviewIssue> ParseReviewResponse(string content, IList<SubtitleCue> cues, SubtitleScene scene)
        {
            Dictionary<string, object> root;
            if (!TryParseJsonObject(content, out root))
                throw new InvalidDataException("DeepSeek 复核结果 JSON 不完整或格式错误。");
            object issuesObject;
            if (!root.TryGetValue("issues", out issuesObject)) throw new InvalidDataException("DeepSeek 复核结果缺少 issues。");
            object[] rows = issuesObject as object[];
            if (rows == null) throw new InvalidDataException("DeepSeek 复核结果格式错误。");

            List<TranslationReviewIssue> issues = new List<TranslationReviewIssue>();
            foreach (object rowObject in rows)
            {
                Dictionary<string, object> row = rowObject as Dictionary<string, object>;
                if (row == null || !row.ContainsKey("id") || !row.ContainsKey("suggestion"))
                    throw new InvalidDataException("DeepSeek 复核条目缺少 id 或 suggestion。");
                int id;
                if (!int.TryParse(Convert.ToString(row["id"], CultureInfo.InvariantCulture).Trim(), out id)
                    || !cues.Skip(scene.StartCueIndex).Take(scene.EndCueIndex - scene.StartCueIndex + 1).Any(c => c.Id == id))
                    throw new InvalidDataException("DeepSeek 复核条目超出目标窗口。");
                string kind = row.ContainsKey("kind") ? Convert.ToString(row["kind"], CultureInfo.InvariantCulture) : "";
                string suggestion = Convert.ToString(row["suggestion"], CultureInfo.InvariantCulture).Trim();
                if (string.IsNullOrWhiteSpace(suggestion)) throw new InvalidDataException("DeepSeek 复核建议为空。");
                bool needsSource = !row.ContainsKey("needs_source_check") || !(row["needs_source_check"] is bool)
                    || (bool)row["needs_source_check"]
                    || Regex.IsMatch(kind + " " + suggestion, "疑为|疑似|误听|核对原文|核对音频|语义不明|意思不完整|可能为|识别错误|transcription|misheard|uncertain", RegexOptions.IgnoreCase);
                issues.Add(new TranslationReviewIssue { CueId = id, Kind = kind, Suggestion = suggestion, NeedsSourceCheck = needsSource });
            }
            return issues;
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

        private static TranslationResult ParseResponse(string content, IList<SubtitleCue> cues, SubtitleScene scene)
        {
            Dictionary<string, object> translatedRoot;
            if (!TryParseJsonObject(content, out translatedRoot))
                throw new InvalidDataException("DeepSeek 翻译结果 JSON 不完整或格式错误。");
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

        private static Dictionary<string, string> ParseRepairResponse(string content, IList<SubtitleCue> cues, IList<int> cueIds)
        {
            HashSet<int> wanted = new HashSet<int>(cueIds);
            Dictionary<string, object> translatedRoot;
            if (!TryParseJsonObject(content, out translatedRoot))
                throw new InvalidDataException("DeepSeek 修复结果 JSON 不完整或格式错误。");
            object translationsObject;
            if (!translatedRoot.TryGetValue("translations", out translationsObject)) throw new InvalidDataException("DeepSeek 修复结果缺少 translations。");
            object[] rows = translationsObject as object[];
            if (rows == null) throw new InvalidDataException("DeepSeek translations 格式错误。");

            Dictionary<string, string> result = new Dictionary<string, string>();
            foreach (object rowObject in rows)
            {
                Dictionary<string, object> row = rowObject as Dictionary<string, object>;
                if (row == null || !row.ContainsKey("id") || !row.ContainsKey("zh")) continue;
                string id = Convert.ToString(row["id"], CultureInfo.InvariantCulture).Trim();
                string text = Convert.ToString(row["zh"], CultureInfo.InvariantCulture).Trim();
                int parsed;
                if (int.TryParse(id, out parsed) && wanted.Contains(parsed) && !string.IsNullOrEmpty(text))
                    result[parsed.ToString(CultureInfo.InvariantCulture)] = text;
            }
            return result;
        }

        public List<TermIndexEntry> BuildTermIndex(IList<string> candidates, IList<string> contextLines, IDictionary<string, string> glossary, CancellationToken cancellation)
        {
            List<object> messages = new List<object>();
            messages.Add(new Dictionary<string, object>
            {
                { "role", "system" },
                { "content", BuildTermIndexSystemPrompt() }
            });
            messages.Add(new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", BuildTermIndexUserPrompt(candidates, contextLines, glossary) }
            });
            string content = PostChat(messages, cancellation);
            return ParseTermIndexResponse(content);
        }

        internal string BuildTermIndexSystemPrompt()
        {
            return "你是" + SourceLanguages.Get(config.SourceLanguage).Name + "影视字幕术语整理器。根据上下文台词，整理出一张规范术语表。" +
                   "只保留人名、称呼、地点、品牌或产品名，以及反复出现的固定术语；同一人物或术语只保留一条。" +
                   "source 给出规范的源语写法；target 给出自然、简洁的简体中文译法；variants 列出识别中常见的错写或变体（可为空数组）。" +
                   "context_lines 是台词样例，candidates 仅作提示可为空。请从台词中识别出角色名和固定称呼。" +
                   "只返回 JSON 对象，格式为 {\"terms\":[{\"source\":\"..\",\"target\":\"..\",\"variants\":[\"..\"]}]}。";
        }

        internal string BuildTermIndexUserPrompt(IList<string> candidates, IList<string> contextLines, IDictionary<string, string> glossary)
        {
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["task"] = "build_term_index";
            request["source_language"] = config.SourceLanguage;
            request["candidates"] = candidates ?? new List<string>();
            request["context_lines"] = contextLines ?? new List<string>();
            request["glossary"] = glossary ?? new Dictionary<string, string>();
            return AtomicJson.Serialize(request);
        }

        private static List<TermIndexEntry> ParseTermIndexResponse(string content)
        {
            Dictionary<string, object> root;
            if (!TryParseJsonObject(content, out root))
                throw new InvalidDataException("DeepSeek 术语表 JSON 不完整或格式错误。");
            object termsObject;
            if (!root.TryGetValue("terms", out termsObject)) throw new InvalidDataException("DeepSeek 术语表缺少 terms。");
            object[] rows = termsObject as object[];
            if (rows == null) throw new InvalidDataException("DeepSeek 术语表格式错误。");

            List<TermIndexEntry> entries = new List<TermIndexEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object rowObject in rows)
            {
                Dictionary<string, object> row = rowObject as Dictionary<string, object>;
                if (row == null || !row.ContainsKey("source") || !row.ContainsKey("target")) continue;
                string source = Convert.ToString(row["source"], CultureInfo.InvariantCulture).Trim();
                string target = Convert.ToString(row["target"], CultureInfo.InvariantCulture).Trim();
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) continue;
                if (!seen.Add(source)) continue;
                TermIndexEntry entry = new TermIndexEntry { Source = source, Target = target, Variants = new List<string>() };
                object variantsObject;
                if (row.TryGetValue("variants", out variantsObject))
                {
                    object[] variants = variantsObject as object[];
                    if (variants != null)
                    {
                        foreach (object variant in variants)
                        {
                            string value = Convert.ToString(variant, CultureInfo.InvariantCulture).Trim();
                            if (!string.IsNullOrWhiteSpace(value) && !entry.Variants.Contains(value)) entry.Variants.Add(value);
                        }
                    }
                }
                entries.Add(entry);
            }
            return entries;
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

        // Robust JSON-object extraction. Handles the occasional reasoning chain or prose that
        // a thinking-enabled model returns before the actual JSON answer.
        private static bool TryParseJsonObject(string text, out Dictionary<string, object> result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string cleaned = StripCodeFence(text.Trim());

            object direct = null;
            try { direct = AtomicJson.DeserializeObject(cleaned); }
            catch { direct = null; }
            Dictionary<string, object> map = direct as Dictionary<string, object>;
            if (map != null) { result = map; return true; }

            int start = cleaned.IndexOf('{');
            if (start < 0) return false;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = start; i < cleaned.Length; i++)
            {
                char c = cleaned[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        string candidate = cleaned.Substring(start, i - start + 1);
                        try { map = AtomicJson.DeserializeObject(candidate) as Dictionary<string, object>; }
                        catch { map = null; }
                        if (map != null) { result = map; return true; }
                    }
                }
            }
            return false;
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
