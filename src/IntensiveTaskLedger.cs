using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class IntensiveLedgerState
    {
        public string Signature { get; set; }
        public bool UnknownRequest { get; set; }
        public string PendingStartedUtc { get; set; }
        public int UnknownRetryConsents { get; set; }
        public TranslationReviewReport Usage { get; set; }
        public Dictionary<string, string> Responses { get; set; }
        public Dictionary<string, int> OperationAttempts { get; set; }
    }
    // One cumulative ledger across new base translation, references and all refinement phases.
    internal sealed class IntensiveTaskLedger : IDisposable
    {
        private readonly string file;
        private string operation;
        public void BeginOperation(string id) { operation = id; }
        public bool CanAttempt { get { return operation == null || !State.OperationAttempts.ContainsKey(operation) || State.OperationAttempts[operation] < 2 || State.Responses.Count > 0; } }
        private readonly ReviewBudget budget;
        private readonly CancellationTokenSource timeout;
        public readonly CancellationToken UserCancellation;
        public readonly IntensiveLedgerState State;
        public CancellationToken Token { get { return timeout.Token; } }
        public IntensiveTaskLedger(AppConfig config, string directory, string signature, CancellationToken cancellation, Func<string, bool> confirmUnknown = null)
        {
            UserCancellation = cancellation;
            file = Path.Combine(directory, "intensive-usage.json");
            State = AtomicJson.Read<IntensiveLedgerState>(file, null);
            if (File.Exists(file) && (State == null || State.Signature != signature || State.Usage == null))
                throw new InvalidDataException("精修用量记录损坏，停止以免重复付费。");
            if (State == null) State = new IntensiveLedgerState { Signature = signature, Usage = new TranslationReviewReport() };
            if (State.Responses == null) State.Responses = new Dictionary<string, string>();
            if (State.OperationAttempts == null) State.OperationAttempts = new Dictionary<string, int>();
            if (State.UnknownRequest)
            {
                DateTime pending;
                if (DateTime.TryParse(State.PendingStartedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out pending))
                    State.Usage.ElapsedSeconds += Math.Min(300, Math.Max(0, (DateTime.UtcNow - pending.ToUniversalTime()).TotalSeconds));
                State.PendingStartedUtc = null; Save();
                if (confirmUnknown == null || !confirmUnknown("上一次精修请求结果未知。重试可能重复收费；是否明确重试并继续？"))
                    throw new InvalidDataException("上次请求结果未知，未自动重复付费。请在界面重新开始并确认重试。");
                State.UnknownRetryConsents++; State.UnknownRequest = false; State.PendingStartedUtc = null;
            }
            State.Usage.RequestLimit = config.IntensiveRequestLimit;
            State.Usage.OutputTokenLimit = config.IntensiveOutputTokenLimit;
            State.Usage.TimeLimitSeconds = config.IntensiveTimeLimitSeconds;
            State.Usage.Intensive = true;
            State.Usage.BudgetExhausted = false;
            budget = new ReviewBudget(State.Usage, Save);
            timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            if (config.IntensiveTimeLimitSeconds > 0) timeout.CancelAfter(budget.RemainingMilliseconds);
            Save();
        }
        public void Reserve(int output, int input)
        {
            if (State.UnknownRequest) throw new ReviewBudgetException("前次请求结果未知，不能自动再次付费");
            Token.ThrowIfCancellationRequested();
            int attempts = operation != null && State.OperationAttempts.ContainsKey(operation) ? State.OperationAttempts[operation] : 0;
            if (operation != null && attempts >= 2) throw new InvalidOperationException("该窗口已达到两次实际请求上限，未重复付费");
            State.UnknownRequest = true; State.PendingStartedUtc = DateTime.UtcNow.ToString("o");
            if (operation != null) State.OperationAttempts[operation] = attempts + 1;
            try { budget.Reserve(output, input); }
            catch { State.UnknownRequest = false; State.PendingStartedUtc = null; if (operation != null) State.OperationAttempts[operation] = attempts; Save(); throw; }
        }
        public void Observe(string json, string requestHash)
        { State.UnknownRequest = false; State.PendingStartedUtc = null; State.Responses[requestHash] = json; budget.Observe(json); }
        public bool TryReplay(string hash, out string json) { return State.Responses.TryGetValue(hash, out json); }
        public void CommitResponse() { State.Responses.Clear(); Save(); }
        public void KnownFailure() { State.UnknownRequest = false; State.PendingStartedUtc = null; Save(); }
        public void Checkpoint() { budget.Checkpoint(); }
        private void Save() { AtomicJson.Write(file, State); }
        public void Dispose() { budget.Checkpoint(); timeout.Dispose(); }
    }
}
