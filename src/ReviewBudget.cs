using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class ReviewBudgetException : Exception
    {
        public ReviewBudgetException(string message) : base(message) { }
    }

    // Reservations include failed requests and the client's output-limit retry; they are NOT a bill.
    internal sealed class ReviewBudget
    {
        private readonly TranslationReviewReport report;
        private readonly Action save;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly double previousSeconds;
        public ReviewBudget(TranslationReviewReport report, Action save)
        { this.report = report; this.save = save; previousSeconds = report.ElapsedSeconds; }
        public int RemainingMilliseconds
        {
            get { return report.Intensive && report.TimeLimitSeconds == 0 ? int.MaxValue : (int)Math.Max(0, Math.Min(int.MaxValue, (report.TimeLimitSeconds - previousSeconds - clock.Elapsed.TotalSeconds) * 1000)); }
        }
        public void Reserve(int maxTokens, int inputCharacters)
        {
            if (RemainingMilliseconds <= 0 || ((report.RequestLimit > 0 || !report.Intensive) && report.RequestCount >= report.RequestLimit)
                || ((report.OutputTokenLimit > 0 || !report.Intensive) && (long)report.ReservedOutputTokens + maxTokens > report.OutputTokenLimit)
                || inputCharacters > 60000 || (!report.Intensive && (long)report.InputCharacters + inputCharacters > 480000)
                || (long)report.ReservedOutputTokens + maxTokens > int.MaxValue || (long)report.InputCharacters + inputCharacters > int.MaxValue)
                throw new ReviewBudgetException("重点复核已到调用、时间或输出预算上限");
            report.RequestCount++;
            report.ReservedOutputTokens += maxTokens;
            report.InputCharacters += inputCharacters;
            Checkpoint();
        }
        public void Observe(string json)
        {
            report.ResponseCount++;
            try
            {
                var root = AtomicJson.DeserializeObject(json) as Dictionary<string, object>;
                var usage = root != null && root.ContainsKey("usage") ? root["usage"] as Dictionary<string, object> : null;
                long prompt, completion;
                if (usage == null || !usage.ContainsKey("prompt_tokens") || !usage.ContainsKey("completion_tokens")
                    || !long.TryParse(Convert.ToString(usage["prompt_tokens"], CultureInfo.InvariantCulture), out prompt)
                    || !long.TryParse(Convert.ToString(usage["completion_tokens"], CultureInfo.InvariantCulture), out completion))
                    report.UnknownUsageResponses++;
                else
                {
                    report.PromptTokens += Math.Max(0, prompt); report.CompletionTokens += Math.Max(0, completion);
                    var details = usage.ContainsKey("completion_tokens_details") ? usage["completion_tokens_details"] as Dictionary<string, object> : null;
                    long reasoning;
                    if (details != null && details.ContainsKey("reasoning_tokens") && long.TryParse(Convert.ToString(details["reasoning_tokens"], CultureInfo.InvariantCulture), out reasoning))
                    { report.ReasoningTokens += Math.Max(0, reasoning); report.ResponsesWithReasoningUsage++; }
                }
            }
            catch { report.UnknownUsageResponses++; }
            Checkpoint();
        }
        public void Checkpoint()
        {
            report.ElapsedSeconds = Math.Round(previousSeconds + clock.Elapsed.TotalSeconds, 3);
            save();
        }
    }
}
