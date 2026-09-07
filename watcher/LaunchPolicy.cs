using System;
using System.Collections.Generic;

namespace PotPlayerAiSubtitle
{
    internal sealed class LaunchPolicy
    {
        private HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        private int attempts;
        private long nextAttempt;

        // Snapshot changes, not the absence of the main app, trigger launches. Explicit app exit is respected.
        internal void Observe(HashSet<string> players, bool mainRunning, long milliseconds, Func<bool> launch)
        {
            pending.IntersectWith(players);
            bool added = false;
            foreach (string player in players)
                if (!known.Contains(player)) { pending.Add(player); added = true; }
            known = new HashSet<string>(players, StringComparer.Ordinal);
            if (added) attempts = 0;
            if (mainRunning) { pending.Clear(); attempts = 0; return; }
            if (pending.Count == 0 || milliseconds < nextAttempt) return;
            attempts++;
            bool started = launch();
            nextAttempt = milliseconds + 5000;
            if (started || attempts >= 3) pending.Clear();
        }
    }
}
