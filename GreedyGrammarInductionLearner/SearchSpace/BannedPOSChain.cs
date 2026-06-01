// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// Immutable singly-linked list of banned POS assignment bitsets.
    /// Children share the parent's tail — O(1) prepend, no copying.
    /// Thread-safe by immutability: once created, a node is never modified.
    /// </summary>
    public sealed class BannedPOSChain
    {
        public const int DefaultMaxPersistedBans = 256;

        public readonly POSBitset Banned;
        public readonly BannedPOSChain Next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BannedPOSChain(POSBitset banned, BannedPOSChain next)
        {
            Banned = banned;
            Next = next;
        }

        /// <summary>
        /// Returns true if any banned bitset in the chain is a subset of the candidate.
        /// If so, the candidate POS assignment is guaranteed to overgenerate (by monotonicity).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBanned(BannedPOSChain chain, in POSBitset candidate)
        {
            var node = chain;
            while (node != null)
            {
                if (node.Banned.IsSubsetOf(candidate))
                    return true;
                node = node.Next;
            }
            return false;
        }

        public static BannedPOSChain MergeCompact(
            BannedPOSChain first,
            BannedPOSChain second,
            int maxCount = DefaultMaxPersistedBans)
        {
            if (maxCount <= 0) return null;

            var bans = new List<POSBitset>(maxCount);
            AddUsefulBans(first, bans, maxCount);
            AddUsefulBans(second, bans, maxCount);

            BannedPOSChain result = null;
            for (int i = bans.Count - 1; i >= 0; i--)
                result = new BannedPOSChain(bans[i], result);

            return result;
        }

        private static void AddUsefulBans(BannedPOSChain source, List<POSBitset> bans, int maxCount)
        {
            for (var node = source; node != null; node = node.Next)
            {
                var candidate = node.Banned;

                bool alreadyCovered = false;
                for (int i = 0; i < bans.Count; i++)
                {
                    var existing = bans[i];
                    if (existing.IsSubsetOf(in candidate))
                    {
                        alreadyCovered = true;
                        break;
                    }
                }
                if (alreadyCovered) continue;

                for (int i = bans.Count - 1; i >= 0; i--)
                {
                    var existing = bans[i];
                    if (candidate.IsSubsetOf(in existing))
                        bans.RemoveAt(i);
                }

                if (bans.Count < maxCount)
                    bans.Add(candidate);
            }
        }
    }
}
