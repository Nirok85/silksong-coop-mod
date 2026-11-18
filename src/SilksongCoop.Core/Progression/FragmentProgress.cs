using System;

namespace SilksongCoop.Core.Progression
{
    /// <summary>One reconciled maximum and its remaining persistent fragments.</summary>
    public readonly struct FragmentProgressUpdate
    {
        public int Maximum { get; }
        public int Partial { get; }
        public int CompletedUpgrades { get; }

        public FragmentProgressUpdate(int maximum, int partial, int completedUpgrades)
        {
            Maximum = maximum;
            Partial = partial;
            CompletedUpgrades = completedUpgrades;
        }
    }

    /// <summary>
    /// Tracks earned upgrades separately from maximum snapshots. A remote maximum can
    /// arrive before its fragment transaction without being counted as a second upgrade.
    /// The caller deduplicates transactions and defers application during native conversion.
    /// </summary>
    public static class FragmentProgress
    {
        /// <summary>
        /// Conservatively merges a saved maximum and partial count as one value.
        /// Maximum-only vitals are not part of the earned baseline and cannot combine
        /// with an unrelated partial snapshot to fabricate additional saved progress.
        /// </summary>
        public static bool TryMergeBaseline(ref int earnedMaximum, int currentPartial,
            int remoteMaximum, int remotePartial, int unitsPerUpgrade,
            int currentMaximum, int sharedMaximum, int maxReasonable,
            out FragmentProgressUpdate update)
        {
            update = default;
            if (unitsPerUpgrade <= 0 || unitsPerUpgrade > 64 || maxReasonable <= 0 ||
                earnedMaximum < 0 || earnedMaximum > maxReasonable ||
                remoteMaximum < 0 || remoteMaximum > maxReasonable ||
                currentMaximum < 0 || currentMaximum > maxReasonable ||
                sharedMaximum < 0 || sharedMaximum > maxReasonable ||
                currentPartial < 0 || currentPartial >= unitsPerUpgrade ||
                remotePartial < 0 || remotePartial >= unitsPerUpgrade)
                return false;

            long localUnits = (long)earnedMaximum * unitsPerUpgrade + currentPartial;
            long remoteUnits = (long)remoteMaximum * unitsPerUpgrade + remotePartial;
            long merged = Math.Max(localUnits, remoteUnits);
            int previousMaximum = earnedMaximum;
            earnedMaximum = (int)(merged / unitsPerUpgrade);
            update = new FragmentProgressUpdate(
                Math.Max(currentMaximum, Math.Max(earnedMaximum, sharedMaximum)),
                (int)(merged % unitsPerUpgrade), earnedMaximum - previousMaximum);
            return true;
        }


        public static bool TryApply(ref int earnedMaximum, int currentPartial, int delta,
            int unitsPerUpgrade, int currentMaximum, int sharedMaximum, int maxReasonable,
            out FragmentProgressUpdate update)
        {
            update = default;
            if (unitsPerUpgrade <= 0 || unitsPerUpgrade > 64 || maxReasonable <= 0 ||
                earnedMaximum < 0 || earnedMaximum > maxReasonable ||
                currentMaximum < 0 || currentMaximum > maxReasonable ||
                sharedMaximum < 0 || sharedMaximum > maxReasonable ||
                currentPartial < 0 || currentPartial >= unitsPerUpgrade ||
                delta < -unitsPerUpgrade * 2 || delta > unitsPerUpgrade * 2)
                return false;

            // Negative deltas are native counter resets, not lost acquisitions.
            int total = currentPartial + Math.Max(0, delta);
            int completed = total / unitsPerUpgrade;
            earnedMaximum = (int)Math.Min((long)earnedMaximum + completed, maxReasonable);
            int maximum = Math.Max(currentMaximum, Math.Max(earnedMaximum, sharedMaximum));
            update = new FragmentProgressUpdate(maximum, total % unitsPerUpgrade, completed);
            return true;
        }

        /// <summary>
        /// Records the amount actually awarded by a local native upgrade. Observed maximum
        /// differences are unsuitable because they may include an earlier remote snapshot.
        /// </summary>
        public static bool NoteLocal(ref int earnedMaximum, int amount, int currentMaximum,
            int maxReasonable)
        {
            if (amount < 0 || amount > maxReasonable || maxReasonable <= 0 ||
                currentMaximum < amount || currentMaximum > maxReasonable ||
                earnedMaximum < -1 || earnedMaximum > maxReasonable)
                return false;

            if (earnedMaximum == -1) earnedMaximum = currentMaximum - amount;
            earnedMaximum = (int)Math.Min((long)earnedMaximum + amount, maxReasonable);
            return true;
        }
    }
}
