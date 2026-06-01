// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using EarleyParserForGreedyGrammarInduction;
using Microsoft.Extensions.Logging;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// Outcome of the legacy per-candidate wildcard evaluator and internal wildcard checks.
    /// </summary>
    public enum WildcardPruneOutcome
    {
        /// <summary>The pattern proved no valid future grammar can use the new rules to derive
        /// next-unparsed; caller should drop this adjacent.</summary>
        Pruned,
        /// <summary>The pattern matched next-unparsed; caller proceeds with normal pipeline.</summary>
        KeptMatched,
        /// <summary>A matched wildcard completion forces an earlier shadow POS string absent from evidence.</summary>
        PrunedShadowNoEvidence,
        /// <summary>A matched wildcard completion forces too many earlier shadow POS strings for an evidence length.</summary>
        PrunedShadowCount,
        /// <summary>RootPruneContext was null (build failed for this root, e.g. encoding overflow).</summary>
        SkippedNoContext,
        /// <summary>No unparsed sentence available (everything parsed, or POS unknown).</summary>
        SkippedNoUnparsed,
        /// <summary>The candidate hit a defensive/unsupported path and is conservatively kept.</summary>
        SkippedUnsupported,
    }

    /// <summary>
    /// Precomputed root-grammar data driving the wildcard-pattern prune.
    /// Built once per root in ProcessRootNode and reused for every depth-1 adjacent.
    /// The accompanying manuscript gives the soundness argument.
    /// </summary>
    public sealed class RootPruneContext
    {
        public List<Rule> BaseRules { get; init; }
        public Dictionary<int, int> PosToIndex { get; init; }
        public int BitsPerSymbol { get; init; }
        internal Dictionary<int, int> RootNtToIdx { get; init; }
        internal int RootNtCount { get; init; }
        internal int StartIdx { get; init; }
        internal int[] RootMinLengths { get; init; }
        internal int[] RootEpsilonLhs { get; init; }
        internal (int lhs, ulong posEncoded)[] RootTerminalRules { get; init; }
        internal (int lhs, int y)[] RootUnitRules { get; init; }
        internal (int lhs, int y, int z)[] RootBinaryRules { get; init; }
        internal FrozenRootStringDp FrozenRootDp { get; init; }
        internal ShadowEvidenceIndex ShadowEvidence { get; init; }
    }

    internal sealed class FrozenRootPatternSet
    {
        public readonly ulong[] FixedBits;
        public int Count => FixedBits.Length;

        public FrozenRootPatternSet(ulong[] fixedBits)
        {
            FixedBits = fixedBits;
        }
    }

    public sealed class ShadowEvidenceIndex
    {
        public readonly int[] MaxEvidenceByLength;
        public readonly HashSet<ulong>[] ExactPosByLength;
        public readonly bool HasExactPosEvidence;

        public ShadowEvidenceIndex(
            int[] maxEvidenceByLength,
            HashSet<ulong>[] exactPosByLength,
            bool hasExactPosEvidence)
        {
            MaxEvidenceByLength = maxEvidenceByLength;
            ExactPosByLength = exactPosByLength;
            HasExactPosEvidence = hasExactPosEvidence;
        }
    }

    internal sealed class FrozenRootStringDp
    {
        public readonly FrozenRootPatternSet[][] Cells;
        public readonly int[][] PopulatedLengths;
        public readonly int MaxLen;
        public readonly long EntryCount;

        public FrozenRootStringDp(
            FrozenRootPatternSet[][] cells,
            int[][] populatedLengths,
            int maxLen,
            long entryCount)
        {
            Cells = cells;
            PopulatedLengths = populatedLengths;
            MaxLen = maxLen;
            EntryCount = entryCount;
        }
    }

    /// <summary>
    /// Wildcard-pattern prune: at depth-1 (one rule added relative to the root), reject the
    /// adjacent if its newly-added rule cannot derive the next-unparsed sentence in any valid
    /// descendant grammar.
    /// </summary>
    public static class WildcardPrune
    {
        // Cap for the shortest-yield DP. If maxMinLen * bitsPerSymbol > 64, skip the prune.
        private const int MaxBitsPerLong = 64;
        private const long RootDpMaxEntries = 32_000_000;
        private const int MaxShadowPatterns = 4096;
        private const int MaxShadowSegmentations = 256;
        private const int MaxShadowSubstitutionsPerSegmentation = 2048;

        // Scratch for the lightweight wildcard-string DP. This mirrors GenerateAllStringsCore_Fast:
        // keep the hot arrays/lists/sets attached to the worker thread and clear/reuse them.
        [ThreadStatic] private static Dictionary<int, int> t_stringDpNtToIdx;
        [ThreadStatic] private static int[] t_stringDpMinLengths;
        [ThreadStatic] private static PatternSet[][] t_stringDp;
        [ThreadStatic] private static List<int>[] t_stringDpPopulatedLens;
        [ThreadStatic] private static int[] t_stringDpShiftAmounts;
        [ThreadStatic] private static int[] t_stringDpLabelShiftAmounts;
        [ThreadStatic] private static int t_stringDpSize;
        [ThreadStatic] private static int t_stringDpMaxLen;
        [ThreadStatic] private static List<(int lhs, int mask)> t_stringDpEpsilonRules;
        [ThreadStatic] private static List<(int lhs, ulong posEncoded, int mask)> t_stringDpTerminalRules;
        [ThreadStatic] private static List<(int lhs, int y, int mask)> t_stringDpUnitRules;
        [ThreadStatic] private static List<(int lhs, int y, int z, int mask)> t_stringDpBinaryRules;
        [ThreadStatic] private static Stack<PatternSet> t_patternSetPool;
        [ThreadStatic] private static HashSet<ulong>[][] t_rootBuildDp;
        [ThreadStatic] private static List<int>[] t_rootBuildPopulatedLens;
        [ThreadStatic] private static int t_rootBuildDpSize;
        [ThreadStatic] private static int t_rootBuildDpMaxLen;
        [ThreadStatic] private static Stack<HashSet<ulong>> t_ulongSetPool;
        [ThreadStatic] private static Stack<List<int>> t_intListPool;
        [ThreadStatic] private static int[] t_matchReach;
        [ThreadStatic] private static int[] t_matchNextReach;
        [ThreadStatic] private static int t_matchStampEpoch;
        [ThreadStatic] private static int[] t_nextUnparsedIndices;
        [ThreadStatic] private static ulong[] t_sentenceTokenMasks;
        [ThreadStatic] private static int[] t_sentenceTokenMaskTouched;
        [ThreadStatic] private static int t_sentenceTokenMaskTouchedCount;
        [ThreadStatic] private static ShadowPatternBuffer t_shadowPatterns;
        [ThreadStatic] private static int[] t_shadowForcedLabels;
        [ThreadStatic] private static int[] t_shadowForcedLengths;
        [ThreadStatic] private static ulong[] t_shadowForcedBits;
        [ThreadStatic] private static HashSet<ulong>[] t_shadowGeneratedByLength;
        [ThreadStatic] private static int[] t_shadowGeneratedTouchedLengths;
        [ThreadStatic] private static int t_shadowGeneratedTouchedCount;
        [ThreadStatic] private static int[] t_guidedMinLengths;
        [ThreadStatic] private static ulong[] t_guidedProductiveBits;
        [ThreadStatic] private static ulong[] t_guidedNoUse;
        [ThreadStatic] private static ulong[] t_guidedUsed;
        [ThreadStatic] private static ulong[] t_guidedAllCandidates;
        [ThreadStatic] private static ulong[] t_guidedUnproductiveBits;
        [ThreadStatic] private static int[] t_guidedCandidateKinds;
        [ThreadStatic] private static int[] t_guidedCandidateLhs;
        [ThreadStatic] private static int[] t_guidedCandidateY;
        [ThreadStatic] private static int[] t_guidedCandidateZ;
        [ThreadStatic] private static int[] t_guidedTerminalCandidates;
        [ThreadStatic] private static int[] t_guidedUnitCandidates;
        [ThreadStatic] private static int[] t_guidedBinaryCandidates;
        [ThreadStatic] private static ulong[] t_guidedTerminalCandidateBits;
        [ThreadStatic] private static int[] t_guidedLeftLiveMasks;
        [ThreadStatic] private static int[] t_guidedRightLiveMasks;
        [ThreadStatic] private static int[] t_guidedLeftLiveMaskCounts;
        [ThreadStatic] private static int[] t_guidedRightLiveMaskCounts;
        [ThreadStatic] private static int[] t_guidedLeftLiveMaskStamps;
        [ThreadStatic] private static int[] t_guidedRightLiveMaskStamps;
        [ThreadStatic] private static int t_guidedLiveMaskStamp;

        private static HashSet<int> s_cachedPosSource;
        private static Dictionary<int, int> s_cachedPosToIndex;
        private static int s_cachedBitsPerSymbol;
        private static readonly object s_posCacheLock = new object();
        private static int s_loggedBitsetFallback64;

        private static (Dictionary<int, int> posToIndex, int bitsPerSymbol) GetCachedPosToIndex()
        {
            var source = Grammar.PartsOfSpeech;
            if (!ReferenceEquals(source, Volatile.Read(ref s_cachedPosSource)))
            {
                lock (s_posCacheLock)
                {
                    if (!ReferenceEquals(source, Volatile.Read(ref s_cachedPosSource)))
                    {
                        int posCount = source.Count;
                        int bitsPerSymbol = posCount <= 1 ? 1 : (32 - BitOperations.LeadingZeroCount((uint)(posCount - 1)));
                        var posToIndex = new Dictionary<int, int>(posCount);
                        int p = 0;
                        foreach (int pos in source) posToIndex[pos] = p++;
                        Volatile.Write(ref s_cachedPosToIndex, posToIndex);
                        s_cachedBitsPerSymbol = bitsPerSymbol;
                        Volatile.Write(ref s_cachedPosSource, source);
                    }
                }
            }

            return (Volatile.Read(ref s_cachedPosToIndex), s_cachedBitsPerSymbol);
        }

        public static ShadowEvidenceIndex BuildShadowEvidenceIndex(
            int[][] sentencePosSequences,
            int[] maxEvidenceByLength,
            bool exactPosEvidence)
        {
            var (posToIndex, bitsPerSymbol) = GetCachedPosToIndex();
            HashSet<ulong>[] exactByLength = null;
            bool hasExact = exactPosEvidence && sentencePosSequences != null;

            if (hasExact)
            {
                int maxLen = maxEvidenceByLength == null ? 0 : maxEvidenceByLength.Length - 1;
                exactByLength = new HashSet<ulong>[Math.Max(0, maxLen + 1)];
                for (int i = 0; i < sentencePosSequences.Length; i++)
                {
                    var sentence = sentencePosSequences[i];
                    if (sentence == null ||
                        sentence.Length >= exactByLength.Length ||
                        (long)bitsPerSymbol * sentence.Length > MaxBitsPerLong)
                    {
                        hasExact = false;
                        break;
                    }

                    ulong encoded = 0UL;
                    for (int p = 0; p < sentence.Length; p++)
                    {
                        if (!posToIndex.TryGetValue(sentence[p], out int pIdx))
                        {
                            hasExact = false;
                            break;
                        }

                        encoded |= ((ulong)pIdx) << (p * bitsPerSymbol);
                    }

                    if (!hasExact) break;
                    exactByLength[sentence.Length] ??= new HashSet<ulong>();
                    exactByLength[sentence.Length].Add(encoded);
                }

                if (!hasExact)
                    exactByLength = null;
            }

            return new ShadowEvidenceIndex(maxEvidenceByLength, exactByLength, hasExact);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetBitsNeeded(int valueCount)
        {
            return valueCount <= 1 ? 1 : (32 - BitOperations.LeadingZeroCount((uint)(valueCount - 1)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong EncodeWildcardLabel(int ntIdx, int position, int bitsPerNtLabel)
        {
            if (bitsPerNtLabel <= 0) return 0UL;
            return ((ulong)(ntIdx + 1)) << (position * bitsPerNtLabel);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractWildcardLabel(ulong wildcardLabels, int position, int bitsPerNtLabel)
        {
            if (bitsPerNtLabel <= 0) return 0;
            ulong mask = bitsPerNtLabel >= 64 ? ulong.MaxValue : ((1UL << bitsPerNtLabel) - 1UL);
            return (int)((wildcardLabels >> (position * bitsPerNtLabel)) & mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static List<int> RentIntList()
        {
            var pool = t_intListPool ??= new Stack<List<int>>();
            if (pool.Count > 0)
            {
                var list = pool.Pop();
                list.Clear();
                return list;
            }
            return new List<int>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnIntList(List<int> list)
        {
            (t_intListPool ??= new Stack<List<int>>()).Push(list);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PatternSet RentPatternSet()
        {
            var pool = t_patternSetPool ??= new Stack<PatternSet>();
            if (pool.Count > 0)
            {
                var set = pool.Pop();
                set.Clear();
                return set;
            }
            return new PatternSet();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnPatternSet(PatternSet set)
        {
            (t_patternSetPool ??= new Stack<PatternSet>()).Push(set);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static HashSet<ulong> RentUlongSet()
        {
            var pool = t_ulongSetPool ??= new Stack<HashSet<ulong>>();
            if (pool.Count > 0)
            {
                var set = pool.Pop();
                set.Clear();
                return set;
            }

            return new HashSet<ulong>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnUlongSet(HashSet<ulong> set)
        {
            (t_ulongSetPool ??= new Stack<HashSet<ulong>>()).Push(set);
        }

        /// <summary>
        /// Builds the root-grammar prune context. Returns null if the encoding limits are
        /// exceeded (in which case the prune is silently disabled for this root).
        /// </summary>
        public static RootPruneContext BuildContext(
            List<Rule> rootCoreRules,
            List<HashSet<Rule>> rootPosMappings,
            int maxLen = -1,
            ShadowEvidenceIndex shadowEvidence = null)
        {
            var (posToIndex, bitsPerSymbol) = GetCachedPosToIndex();

            var baseRules = new List<Rule>(rootCoreRules == null ? 0 : rootCoreRules.Count);
            if (rootCoreRules != null)
                baseRules.AddRange(rootCoreRules);

            var seenPosRules = new HashSet<Rule>();
            if (rootPosMappings != null)
            {
                foreach (var mapping in rootPosMappings)
                {
                    if (mapping == null) continue;
                    foreach (var r in mapping)
                    {
                        if (seenPosRules.Add(r)) baseRules.Add(r);
                    }
                }
            }

            int startSymbolId = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);
            var rootNtToIdx = new Dictionary<int, int>();
            AddNonTerminal(rootNtToIdx, posToIndex, startSymbolId);
            AddRuleSymbols(baseRules, posToIndex, rootNtToIdx);

            int rootNtCount = rootNtToIdx.Count;
            int startIdx = rootNtToIdx.TryGetValue(startSymbolId, out int si) ? si : -1;
            var rootMinLengths = new int[rootNtCount];
            Array.Fill(rootMinLengths, int.MaxValue);
            bool minChanged;
            do
            {
                minChanged = RelaxMinLengths(baseRules, posToIndex, rootNtToIdx, rootMinLengths);
            } while (minChanged);

            var rootEpsilonRules = new List<(int lhs, int mask)>();
            var rootTerminalRules = new List<(int lhs, ulong posEncoded, int mask)>();
            var rootUnitRules = new List<(int lhs, int y, int mask)>();
            var rootBinaryRules = new List<(int lhs, int y, int z, int mask)>();
            bool rootSupported = ClassifyRules(
                baseRules,
                0,
                new RootPruneContext { PosToIndex = posToIndex },
                rootNtToIdx,
                rootEpsilonRules,
                rootTerminalRules,
                rootUnitRules,
                rootBinaryRules) == WildcardPruneOutcome.KeptMatched;

            var frozenRootDp = rootSupported && startIdx >= 0 && maxLen >= 0 && (long)bitsPerSymbol * maxLen <= MaxBitsPerLong
                ? BuildFrozenRootDp(
                    rootNtCount,
                    maxLen,
                    bitsPerSymbol,
                    CollectionsMarshal.AsSpan(rootEpsilonRules),
                    CollectionsMarshal.AsSpan(rootTerminalRules),
                    CollectionsMarshal.AsSpan(rootUnitRules),
                    CollectionsMarshal.AsSpan(rootBinaryRules))
                : null;

            return new RootPruneContext
            {
                BaseRules = baseRules,
                PosToIndex = posToIndex,
                BitsPerSymbol = bitsPerSymbol,
                RootNtToIdx = rootNtToIdx,
                RootNtCount = rootNtCount,
                StartIdx = startIdx,
                RootMinLengths = rootMinLengths,
                RootEpsilonLhs = rootSupported ? ToRootEpsilonArray(rootEpsilonRules) : null,
                RootTerminalRules = rootSupported ? ToRootTerminalArray(rootTerminalRules) : null,
                RootUnitRules = rootSupported ? ToRootUnitArray(rootUnitRules) : null,
                RootBinaryRules = rootSupported ? ToRootBinaryArray(rootBinaryRules) : null,
                FrozenRootDp = frozenRootDp,
                ShadowEvidence = shadowEvidence,
            };
        }

        private static int[] ToRootEpsilonArray(List<(int lhs, int mask)> source)
        {
            var result = new int[source.Count];
            for (int i = 0; i < source.Count; i++)
                result[i] = source[i].lhs;
            return result;
        }

        private static (int lhs, ulong posEncoded)[] ToRootTerminalArray(List<(int lhs, ulong posEncoded, int mask)> source)
        {
            var result = new (int lhs, ulong posEncoded)[source.Count];
            for (int i = 0; i < source.Count; i++)
                result[i] = (source[i].lhs, source[i].posEncoded);
            return result;
        }

        private static (int lhs, int y)[] ToRootUnitArray(List<(int lhs, int y, int mask)> source)
        {
            var result = new (int lhs, int y)[source.Count];
            for (int i = 0; i < source.Count; i++)
                result[i] = (source[i].lhs, source[i].y);
            return result;
        }

        private static (int lhs, int y, int z)[] ToRootBinaryArray(List<(int lhs, int y, int z, int mask)> source)
        {
            var result = new (int lhs, int y, int z)[source.Count];
            for (int i = 0; i < source.Count; i++)
                result[i] = (source[i].lhs, source[i].y, source[i].z);
            return result;
        }

        private static FrozenRootStringDp BuildFrozenRootDp(
            int ntCount,
            int maxLen,
            int bitsPerSymbol,
            ReadOnlySpan<(int lhs, int mask)> epsilonRules,
            ReadOnlySpan<(int lhs, ulong posEncoded, int mask)> terminalRules,
            ReadOnlySpan<(int lhs, int y, int mask)> unitRules,
            ReadOnlySpan<(int lhs, int y, int z, int mask)> binaryRules)
        {
            EnsureRootBuildDpCapacity(ntCount, maxLen);
            var dp = t_rootBuildDp;
            var populatedLengths = t_rootBuildPopulatedLens;
            for (int i = 0; i < ntCount; i++)
                populatedLengths[i] = RentIntList();

            if (t_stringDpShiftAmounts == null || t_stringDpShiftAmounts.Length < maxLen + 1)
                t_stringDpShiftAmounts = new int[maxLen + 1];
            var shiftAmounts = t_stringDpShiftAmounts;
            for (int len = 0; len <= maxLen; len++)
                shiftAmounts[len] = bitsPerSymbol * len;

            long entryCount = 0;
            bool aborted = false;
            bool hasUnitRules = unitRules.Length > 0;

            try
            {
                for (int len = 0; len <= maxLen && !aborted; len++)
                {
                    if (len == 0)
                    {
                        for (int i = 0; i < epsilonRules.Length; i++)
                        {
                            if (AddRootPattern(dp, populatedLengths, epsilonRules[i].lhs, 0, 0UL))
                                aborted = ++entryCount > RootDpMaxEntries;
                        }
                    }

                    if (len == 1)
                    {
                        for (int i = 0; i < terminalRules.Length; i++)
                        {
                            if (AddRootPattern(dp, populatedLengths, terminalRules[i].lhs, 1, terminalRules[i].posEncoded))
                                aborted = ++entryCount > RootDpMaxEntries;
                        }
                    }

                    if (len > 0)
                    {
                        for (int bi = 0; bi < binaryRules.Length && !aborted; bi++)
                        {
                            var binary = binaryRules[bi];
                            var dest = dp[binary.lhs][len];
                            var popsY = populatedLengths[binary.y];
                            for (int pi = 0; pi < popsY.Count && !aborted; pi++)
                            {
                                int lenY = popsY[pi];
                                if (lenY > len) break;
                                int lenZ = len - lenY;
                                var stringsY = dp[binary.y][lenY];
                                var stringsZ = dp[binary.z][lenZ];
                                if (stringsY == null || stringsZ == null) continue;

                                if (dest == null)
                                {
                                    dest = RentUlongSet();
                                    dp[binary.lhs][len] = dest;
                                    populatedLengths[binary.lhs].Add(len);
                                }

                                int shiftBits = shiftAmounts[lenY];
                                foreach (ulong fixedY in stringsY)
                                {
                                    foreach (ulong fixedZ in stringsZ)
                                    {
                                        if (dest.Add(fixedY | (fixedZ << shiftBits)))
                                            aborted = ++entryCount > RootDpMaxEntries;
                                    }
                                }
                            }
                        }
                    }

                    if (hasUnitRules && !aborted)
                    {
                        bool changed;
                        do
                        {
                            changed = false;
                            for (int ui = 0; ui < unitRules.Length && !aborted; ui++)
                            {
                                var unit = unitRules[ui];
                                if (unit.lhs == unit.y) continue;
                                var source = dp[unit.y][len];
                                if (source == null) continue;

                                var dest = dp[unit.lhs][len];
                                if (dest == null)
                                {
                                    dest = RentUlongSet();
                                    dp[unit.lhs][len] = dest;
                                    populatedLengths[unit.lhs].Add(len);
                                }

                                foreach (ulong fixedBits in source)
                                {
                                    if (dest.Add(fixedBits))
                                    {
                                        changed = true;
                                        aborted = ++entryCount > RootDpMaxEntries;
                                    }
                                }
                            }
                        } while (changed && !aborted);
                    }
                }

                if (aborted)
                    return null;

                var frozenCells = new FrozenRootPatternSet[ntCount][];
                var frozenPopulated = new int[ntCount][];
                for (int nt = 0; nt < ntCount; nt++)
                {
                    frozenCells[nt] = new FrozenRootPatternSet[maxLen + 1];
                    var pops = populatedLengths[nt];
                    frozenPopulated[nt] = pops.Count == 0 ? Array.Empty<int>() : pops.ToArray();
                    var row = dp[nt];
                    for (int p = 0; p < pops.Count; p++)
                    {
                        int len = pops[p];
                        var set = row[len];
                        if (set == null || set.Count == 0) continue;
                        var fixedBits = new ulong[set.Count];
                        set.CopyTo(fixedBits);
                        frozenCells[nt][len] = new FrozenRootPatternSet(fixedBits);
                    }
                }

                return new FrozenRootStringDp(frozenCells, frozenPopulated, maxLen, entryCount);
            }
            finally
            {
                CleanupRootBuildDp(ntCount, dp, populatedLengths);
            }
        }


        /// <summary>
        /// Evaluates the wildcard-pattern prune for the set of newly-added rules.
        /// Returns the outcome — caller should drop the adjacent iff the result is
        /// <see cref="WildcardPruneOutcome.Pruned"/>; every other value is sound to keep.
        /// The accompanying manuscript gives the proof.
        /// </summary>
        public static WildcardPruneOutcome Evaluate(
            RootPruneContext ctx,
            IReadOnlyList<Rule> newRules,
            ReadOnlySpan<int> nextUnparsed,
            ILogger logger = null)
        {
            if (ctx == null) return WildcardPruneOutcome.SkippedNoContext;
            if (nextUnparsed.IsEmpty) return WildcardPruneOutcome.SkippedNoUnparsed;
            if (newRules == null || newRules.Count == 0) return WildcardPruneOutcome.SkippedUnsupported;
            if ((long)ctx.BitsPerSymbol * nextUnparsed.Length > MaxBitsPerLong)
                return WildcardPruneOutcome.SkippedNoContext;

            if (newRules.Count >= 31)
                return WildcardPruneOutcome.SkippedUnsupported;

            if (ctx.FrozenRootDp != null &&
                ctx.RootNtToIdx != null &&
                ctx.RootMinLengths != null &&
                ctx.FrozenRootDp.MaxLen >= nextUnparsed.Length)
            {
                return EvaluateByRootOverlayStringDp(ctx, newRules, nextUnparsed, logger);
            }

            return EvaluateByWildcardStringDp(ctx, newRules, nextUnparsed, logger);
        }

        /// <summary>
        /// Adds to <paramref name="allowedCandidateBits"/> every candidate rule that can survive the
        /// non-shadow wildcard match for this root/delta/next-unparsed state. The bit index is the
        /// candidate's ordinal in <paramref name="candidateRules"/>. Returns false when the guide
        /// cannot prove a safe result; callers must then fall back to ordinary generation.
        /// </summary>
        public static bool TryAddGuidedCandidates(
            RootPruneContext ctx,
            IReadOnlyList<Rule> deltaRules,
            IReadOnlyList<Rule> candidateRules,
            ReadOnlySpan<int> nextUnparsed,
            ulong[] allowedCandidateBits)
        {
            if (ctx == null ||
                ctx.BaseRules == null ||
                ctx.PosToIndex == null ||
                ctx.RootNtToIdx == null ||
                ctx.RootMinLengths == null ||
                nextUnparsed.IsEmpty ||
                candidateRules == null ||
                candidateRules.Count == 0 ||
                allowedCandidateBits == null ||
                (long)ctx.BitsPerSymbol * nextUnparsed.Length > MaxBitsPerLong)
            {
                return false;
            }

            int deltaCount = deltaRules == null ? 0 : deltaRules.Count;
            if (deltaCount >= 30)
                return false;

            int candidateCount = candidateRules.Count;
            int wordCount = (candidateCount + 63) >> 6;
            if (allowedCandidateBits.Length < wordCount)
                return false;

            if (!TryEncodeSentenceIndices(nextUnparsed, ctx.PosToIndex, out var sentenceIndices))
            {
                return false;
            }

            if (ctx.RootEpsilonLhs != null && ctx.RootEpsilonLhs.Length != 0)
                return false;

            int sentenceLength = nextUnparsed.Length;
            int pointCount = sentenceLength + 1;
            int spanCount = pointCount * pointCount;
            int maskCount = 1 << deltaCount;
            int fullDeltaMask = maskCount - 1;

            var ntToIdx = t_stringDpNtToIdx ??= new Dictionary<int, int>();
            ntToIdx.Clear();
            foreach (var kv in ctx.RootNtToIdx)
                ntToIdx[kv.Key] = kv.Value;
            AddRuleSymbols(deltaRules, ctx.PosToIndex, ntToIdx);
            AddRuleSymbols(candidateRules, ctx.PosToIndex, ntToIdx);

            int ntCount = ntToIdx.Count;
            if (ntCount == 0 || ctx.StartIdx < 0)
                return false;

            var deltaEpsilonRules = t_stringDpEpsilonRules ??= new List<(int lhs, int mask)>();
            deltaEpsilonRules.Clear();
            var deltaTerminalRules = t_stringDpTerminalRules ??= new List<(int lhs, ulong posEncoded, int mask)>();
            deltaTerminalRules.Clear();
            var deltaUnitRules = t_stringDpUnitRules ??= new List<(int lhs, int y, int mask)>();
            deltaUnitRules.Clear();
            var deltaBinaryRules = t_stringDpBinaryRules ??= new List<(int lhs, int y, int z, int mask)>();
            deltaBinaryRules.Clear();

            for (int i = 0; i < deltaCount; i++)
            {
                var classifyOutcome = ClassifyRule(
                    deltaRules[i],
                    1 << i,
                    ctx,
                    ntToIdx,
                    deltaEpsilonRules,
                    deltaTerminalRules,
                    deltaUnitRules,
                    deltaBinaryRules);
                if (classifyOutcome != WildcardPruneOutcome.KeptMatched)
                    return false;
            }

            if (deltaEpsilonRules.Count != 0)
                return false;

            EnsureGuidedCandidateArrays(candidateCount);
            var candidateKinds = t_guidedCandidateKinds;
            var candidateLhs = t_guidedCandidateLhs;
            var candidateY = t_guidedCandidateY;
            var candidateZ = t_guidedCandidateZ;
            var terminalCandidates = t_guidedTerminalCandidates;
            var unitCandidates = t_guidedUnitCandidates;
            var binaryCandidates = t_guidedBinaryCandidates;
            int terminalCandidateCount = 0;
            int unitCandidateCount = 0;
            int binaryCandidateCount = 0;

            for (int c = 0; c < candidateCount; c++)
            {
                if (!TryClassifyGuidedCandidateRule(
                        candidateRules[c],
                        ctx,
                        ntToIdx,
                        candidateKinds,
                        candidateLhs,
                        candidateY,
                        candidateZ,
                        c))
                {
                    return false;
                }

                switch (candidateKinds[c])
                {
                    case 1:
                        terminalCandidates[terminalCandidateCount++] = c;
                        break;
                    case 2:
                        unitCandidates[unitCandidateCount++] = c;
                        break;
                    case 3:
                        binaryCandidates[binaryCandidateCount++] = c;
                        break;
                }
            }

            if (t_guidedMinLengths == null || t_guidedMinLengths.Length < ntCount)
                t_guidedMinLengths = new int[ntCount];

            var baseDeltaMinLengths = t_guidedMinLengths;
            Array.Copy(ctx.RootMinLengths, baseDeltaMinLengths, ctx.RootNtCount);
            if (ntCount > ctx.RootNtCount)
                Array.Fill(baseDeltaMinLengths, int.MaxValue, ctx.RootNtCount, ntCount - ctx.RootNtCount);

            bool minChanged;
            do
            {
                minChanged = false;
                minChanged |= RelaxMinLengths(ctx.BaseRules, ctx.PosToIndex, ntToIdx, baseDeltaMinLengths);
                minChanged |= RelaxMinLengths(deltaRules, ctx.PosToIndex, ntToIdx, baseDeltaMinLengths);
            } while (minChanged);

            var rootTerminalRules = ctx.RootTerminalRules;
            var rootUnitRules = ctx.RootUnitRules;
            var rootBinaryRules = ctx.RootBinaryRules;
            if (rootTerminalRules == null || rootUnitRules == null || rootBinaryRules == null)
                return false;

            var allCandidates = EnsureGuidedAllCandidates(candidateCount, wordCount);
            int unproductiveLength = ntCount * wordCount;
            if (t_guidedUnproductiveBits == null || t_guidedUnproductiveBits.Length < unproductiveLength)
                t_guidedUnproductiveBits = new ulong[unproductiveLength];
            var unproductiveBits = t_guidedUnproductiveBits;
            BuildGuidedUnproductiveBits(
                baseDeltaMinLengths,
                rootUnitRules,
                rootBinaryRules,
                deltaUnitRules,
                deltaBinaryRules,
                candidateLhs,
                candidateY,
                candidateZ,
                terminalCandidates,
                terminalCandidateCount,
                unitCandidates,
                unitCandidateCount,
                binaryCandidates,
                binaryCandidateCount,
                ntCount,
                wordCount,
                allCandidates,
                unproductiveBits);

            long cellWordsLong = (long)ntCount * maskCount * spanCount * wordCount;
            if (cellWordsLong <= 0 || cellWordsLong > int.MaxValue)
                return false;
            int cellWords = (int)cellWordsLong;

            if (t_guidedNoUse == null || t_guidedNoUse.Length < cellWords)
                t_guidedNoUse = new ulong[cellWords];
            else
                Array.Clear(t_guidedNoUse, 0, cellWords);

            if (t_guidedUsed == null || t_guidedUsed.Length < cellWords)
                t_guidedUsed = new ulong[cellWords];
            else
                Array.Clear(t_guidedUsed, 0, cellWords);

            var noUse = t_guidedNoUse;
            var used = t_guidedUsed;
            int posCount = ctx.PosToIndex.Count;
            ulong[] terminalCandidateBits = null;
            if (terminalCandidateCount != 0)
            {
                long terminalCandidateBitLengthLong = (long)ntCount * posCount * wordCount;
                if (terminalCandidateBitLengthLong > int.MaxValue)
                    return false;

                int terminalCandidateBitLength = (int)terminalCandidateBitLengthLong;
                if (t_guidedTerminalCandidateBits == null || t_guidedTerminalCandidateBits.Length < terminalCandidateBitLength)
                    t_guidedTerminalCandidateBits = new ulong[terminalCandidateBitLength];
                else
                    Array.Clear(t_guidedTerminalCandidateBits, 0, terminalCandidateBitLength);

                terminalCandidateBits = t_guidedTerminalCandidateBits;
                for (int ci = 0; ci < terminalCandidateCount; ci++)
                {
                    int c = terminalCandidates[ci];
                    int offset = ((candidateLhs[c] * posCount) + candidateY[c]) * wordCount;
                    SetBit(terminalCandidateBits, offset, c);
                }
            }

            for (int len = 1; len <= sentenceLength; len++)
            {
                for (int i = 0; i + len <= sentenceLength; i++)
                {
                    int j = i + len;

                    for (int nt = 0; nt < ntCount; nt++)
                    {
                        if (nt == ctx.StartIdx) continue;
                        int src = nt * wordCount;
                        if (HasAnyBits(unproductiveBits, src, wordCount))
                        {
                            int dest = GuidedOffset(nt, 0, i, j, maskCount, pointCount, spanCount, wordCount);
                            OrBits(noUse, dest, unproductiveBits, src, wordCount);
                        }
                    }

                    if (len == 1)
                    {
                        int token = sentenceIndices[i];
                        for (int ri = 0; ri < rootTerminalRules.Length; ri++)
                        {
                            var terminal = rootTerminalRules[ri];
                            if ((int)terminal.posEncoded != token) continue;
                            int dest = GuidedOffset(terminal.lhs, 0, i, j, maskCount, pointCount, spanCount, wordCount);
                            OrBits(noUse, dest, allCandidates, 0, wordCount);
                        }

                        var deltaTerminalSpan = CollectionsMarshal.AsSpan(deltaTerminalRules);
                        for (int ri = 0; ri < deltaTerminalSpan.Length; ri++)
                        {
                            ref readonly var terminal = ref deltaTerminalSpan[ri];
                            if ((int)terminal.posEncoded != token) continue;
                            int dest = GuidedOffset(terminal.lhs, terminal.mask, i, j, maskCount, pointCount, spanCount, wordCount);
                            OrBits(noUse, dest, allCandidates, 0, wordCount);
                        }

                        if (terminalCandidateCount != 0)
                        {
                            for (int nt = 0; nt < ntCount; nt++)
                            {
                                int src = ((nt * posCount) + token) * wordCount;
                                if (!HasAnyBits(terminalCandidateBits, src, wordCount))
                                    continue;

                                int dest = GuidedOffset(nt, 0, i, j, maskCount, pointCount, spanCount, wordCount);
                                OrBits(used, dest, terminalCandidateBits, src, wordCount);
                            }
                        }
                    }
                }

                if (len > 1)
                {
                    for (int i = 0; i + len <= sentenceLength; i++)
                    {
                        int j = i + len;
                        for (int split = i + 1; split < j; split++)
                        {
                            int liveMaskStamp = BeginGuidedLiveMaskCache(ntCount, maskCount);
                            for (int ri = 0; ri < rootBinaryRules.Length; ri++)
                            {
                                var binary = rootBinaryRules[ri];
                                AddGuidedExistingBinary(
                                    noUse,
                                    used,
                                    binary.lhs,
                                    binary.y,
                                    binary.z,
                                    0,
                                    i,
                                    split,
                                    j,
                                    maskCount,
                                    pointCount,
                                    spanCount,
                                    wordCount,
                                    liveMaskStamp);
                            }

                            var deltaBinarySpan = CollectionsMarshal.AsSpan(deltaBinaryRules);
                            for (int ri = 0; ri < deltaBinarySpan.Length; ri++)
                            {
                                ref readonly var binary = ref deltaBinarySpan[ri];
                                AddGuidedExistingBinary(
                                    noUse,
                                    used,
                                    binary.lhs,
                                    binary.y,
                                    binary.z,
                                    binary.mask,
                                    i,
                                    split,
                                    j,
                                    maskCount,
                                    pointCount,
                                    spanCount,
                                    wordCount,
                                    liveMaskStamp);
                            }

                            for (int ci = 0; ci < binaryCandidateCount; ci++)
                            {
                                int c = binaryCandidates[ci];
                                AddGuidedCandidateBinary(
                                    noUse,
                                    used,
                                    candidateLhs[c],
                                    candidateY[c],
                                    candidateZ[c],
                                    c,
                                    i,
                                    split,
                                    j,
                                    maskCount,
                                    pointCount,
                                    spanCount,
                                    wordCount);
                            }
                        }
                    }
                }

                bool unitChanged;
                var deltaUnitSpan = CollectionsMarshal.AsSpan(deltaUnitRules);
                do
                {
                    unitChanged = false;
                    for (int i = 0; i + len <= sentenceLength; i++)
                    {
                        int j = i + len;
                        for (int ri = 0; ri < rootUnitRules.Length; ri++)
                        {
                            var unit = rootUnitRules[ri];
                            unitChanged |= AddGuidedExistingUnit(
                                noUse,
                                used,
                                unit.lhs,
                                unit.y,
                                0,
                                i,
                                j,
                                maskCount,
                                pointCount,
                                spanCount,
                                wordCount);
                        }

                        for (int ri = 0; ri < deltaUnitSpan.Length; ri++)
                        {
                            ref readonly var unit = ref deltaUnitSpan[ri];
                            unitChanged |= AddGuidedExistingUnit(
                                noUse,
                                used,
                                unit.lhs,
                                unit.y,
                                unit.mask,
                                i,
                                j,
                                maskCount,
                                pointCount,
                                spanCount,
                                wordCount);
                        }

                        for (int ci = 0; ci < unitCandidateCount; ci++)
                        {
                            int c = unitCandidates[ci];
                            unitChanged |= AddGuidedCandidateUnit(
                                noUse,
                                used,
                                candidateLhs[c],
                                candidateY[c],
                                c,
                                i,
                                j,
                                maskCount,
                                pointCount,
                                spanCount,
                                wordCount);
                        }
                    }
                } while (unitChanged);
            }

            int startOffset = GuidedOffset(ctx.StartIdx, fullDeltaMask, 0, sentenceLength, maskCount, pointCount, spanCount, wordCount);
            OrBits(allowedCandidateBits, 0, used, startOffset, wordCount);
            return true;
        }

        private static void EnsureGuidedCandidateArrays(int candidateCount)
        {
            if (t_guidedCandidateKinds == null || t_guidedCandidateKinds.Length < candidateCount)
            {
                t_guidedCandidateKinds = new int[candidateCount];
                t_guidedCandidateLhs = new int[candidateCount];
                t_guidedCandidateY = new int[candidateCount];
                t_guidedCandidateZ = new int[candidateCount];
            }

            if (t_guidedTerminalCandidates == null || t_guidedTerminalCandidates.Length < candidateCount)
                t_guidedTerminalCandidates = new int[candidateCount];
            if (t_guidedUnitCandidates == null || t_guidedUnitCandidates.Length < candidateCount)
                t_guidedUnitCandidates = new int[candidateCount];
            if (t_guidedBinaryCandidates == null || t_guidedBinaryCandidates.Length < candidateCount)
                t_guidedBinaryCandidates = new int[candidateCount];
        }

        private static ulong[] EnsureGuidedAllCandidates(int candidateCount, int wordCount)
        {
            if (t_guidedAllCandidates == null || t_guidedAllCandidates.Length < wordCount)
                t_guidedAllCandidates = new ulong[wordCount];

            var allCandidates = t_guidedAllCandidates;
            Array.Fill(allCandidates, ulong.MaxValue, 0, wordCount);
            int tailBits = candidateCount & 63;
            if (tailBits != 0)
                allCandidates[wordCount - 1] = (1UL << tailBits) - 1UL;
            return allCandidates;
        }

        private static int BeginGuidedLiveMaskCache(int ntCount, int maskCount)
        {
            int liveMaskCapacity = ntCount * maskCount;
            EnsureGuidedIntArray(ref t_guidedLeftLiveMasks, liveMaskCapacity);
            EnsureGuidedIntArray(ref t_guidedRightLiveMasks, liveMaskCapacity);
            EnsureGuidedIntArray(ref t_guidedLeftLiveMaskCounts, ntCount);
            EnsureGuidedIntArray(ref t_guidedRightLiveMaskCounts, ntCount);
            EnsureGuidedIntArray(ref t_guidedLeftLiveMaskStamps, ntCount);
            EnsureGuidedIntArray(ref t_guidedRightLiveMaskStamps, ntCount);

            int nextStamp = t_guidedLiveMaskStamp + 1;
            if (nextStamp <= 0)
            {
                Array.Clear(t_guidedLeftLiveMaskStamps, 0, t_guidedLeftLiveMaskStamps.Length);
                Array.Clear(t_guidedRightLiveMaskStamps, 0, t_guidedRightLiveMaskStamps.Length);
                nextStamp = 1;
            }

            t_guidedLiveMaskStamp = nextStamp;
            return nextStamp;
        }

        private static void EnsureGuidedIntArray(ref int[] array, int length)
        {
            if (array == null || array.Length < length)
                array = new int[length];
        }

        private static bool TryClassifyGuidedCandidateRule(
            Rule rule,
            RootPruneContext ctx,
            Dictionary<int, int> ntToIdx,
            int[] candidateKinds,
            int[] candidateLhs,
            int[] candidateY,
            int[] candidateZ,
            int candidateIndex)
        {
            if (!ntToIdx.TryGetValue(rule.LeftHandSide, out int lhsIdx))
                return false;

            candidateLhs[candidateIndex] = lhsIdx;
            var rhs = rule.RightHandSide;
            if (rhs.Length == 0)
                return false;

            if (rhs.Length == 1)
            {
                int y = rhs[0];
                if (ctx.PosToIndex.TryGetValue(y, out int pIdx))
                {
                    candidateKinds[candidateIndex] = 1;
                    candidateY[candidateIndex] = pIdx;
                    candidateZ[candidateIndex] = -1;
                    return true;
                }

                if (ntToIdx.TryGetValue(y, out int yIdx))
                {
                    candidateKinds[candidateIndex] = 2;
                    candidateY[candidateIndex] = yIdx;
                    candidateZ[candidateIndex] = -1;
                    return true;
                }

                return false;
            }

            if (rhs.Length == 2 &&
                ntToIdx.TryGetValue(rhs[0], out int rhs0Idx) &&
                ntToIdx.TryGetValue(rhs[1], out int rhs1Idx))
            {
                candidateKinds[candidateIndex] = 3;
                candidateY[candidateIndex] = rhs0Idx;
                candidateZ[candidateIndex] = rhs1Idx;
                return true;
            }

            return false;
        }

        private static void BuildGuidedUnproductiveBits(
            int[] baseDeltaMinLengths,
            (int lhs, int y)[] rootUnitRules,
            (int lhs, int y, int z)[] rootBinaryRules,
            List<(int lhs, int y, int mask)> deltaUnitRules,
            List<(int lhs, int y, int z, int mask)> deltaBinaryRules,
            int[] candidateLhs,
            int[] candidateY,
            int[] candidateZ,
            int[] terminalCandidates,
            int terminalCandidateCount,
            int[] unitCandidates,
            int unitCandidateCount,
            int[] binaryCandidates,
            int binaryCandidateCount,
            int ntCount,
            int wordCount,
            ulong[] allCandidates,
            ulong[] unproductiveBits)
        {
            int bitLength = ntCount * wordCount;
            if (t_guidedProductiveBits == null || t_guidedProductiveBits.Length < bitLength)
                t_guidedProductiveBits = new ulong[bitLength];
            else
                Array.Clear(t_guidedProductiveBits, 0, bitLength);

            var productiveBits = t_guidedProductiveBits;
            for (int nt = 0; nt < ntCount; nt++)
            {
                if (baseDeltaMinLengths[nt] == int.MaxValue)
                    continue;
                Array.Copy(allCandidates, 0, productiveBits, nt * wordCount, wordCount);
            }

            var deltaUnitSpan = CollectionsMarshal.AsSpan(deltaUnitRules);
            var deltaBinarySpan = CollectionsMarshal.AsSpan(deltaBinaryRules);
            bool changed;
            do
            {
                changed = false;

                for (int ci = 0; ci < terminalCandidateCount; ci++)
                {
                    int c = terminalCandidates[ci];
                    int lhsOffset = candidateLhs[c] * wordCount;
                    changed |= SetBitIfChanged(productiveBits, lhsOffset, c);
                }

                for (int ci = 0; ci < unitCandidateCount; ci++)
                {
                    int c = unitCandidates[ci];
                    int lhsOffset = candidateLhs[c] * wordCount;
                    if (HasBit(productiveBits, candidateY[c] * wordCount, c))
                        changed |= SetBitIfChanged(productiveBits, lhsOffset, c);
                }

                for (int ci = 0; ci < binaryCandidateCount; ci++)
                {
                    int c = binaryCandidates[ci];
                    int lhsOffset = candidateLhs[c] * wordCount;
                    if (HasBit(productiveBits, candidateY[c] * wordCount, c) &&
                        HasBit(productiveBits, candidateZ[c] * wordCount, c))
                    {
                        changed |= SetBitIfChanged(productiveBits, lhsOffset, c);
                    }
                }

                for (int ri = 0; ri < rootUnitRules.Length; ri++)
                {
                    var unit = rootUnitRules[ri];
                    changed |= OrBits(
                        productiveBits,
                        unit.lhs * wordCount,
                        productiveBits,
                        unit.y * wordCount,
                        wordCount);
                }

                for (int ri = 0; ri < deltaUnitSpan.Length; ri++)
                {
                    ref readonly var unit = ref deltaUnitSpan[ri];
                    changed |= OrBits(
                        productiveBits,
                        unit.lhs * wordCount,
                        productiveBits,
                        unit.y * wordCount,
                        wordCount);
                }

                for (int ri = 0; ri < rootBinaryRules.Length; ri++)
                {
                    var binary = rootBinaryRules[ri];
                    changed |= OrAndBits(
                        productiveBits,
                        binary.lhs * wordCount,
                        productiveBits,
                        binary.y * wordCount,
                        productiveBits,
                        binary.z * wordCount,
                        wordCount);
                }

                for (int ri = 0; ri < deltaBinarySpan.Length; ri++)
                {
                    ref readonly var binary = ref deltaBinarySpan[ri];
                    changed |= OrAndBits(
                        productiveBits,
                        binary.lhs * wordCount,
                        productiveBits,
                        binary.y * wordCount,
                        productiveBits,
                        binary.z * wordCount,
                        wordCount);
                }
            } while (changed);

            for (int nt = 0; nt < ntCount; nt++)
            {
                int offset = nt * wordCount;
                for (int w = 0; w < wordCount; w++)
                    unproductiveBits[offset + w] = ~productiveBits[offset + w] & allCandidates[w];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GuidedOffset(
            int nt,
            int mask,
            int i,
            int j,
            int maskCount,
            int pointCount,
            int spanCount,
            int wordCount)
        {
            int span = i * pointCount + j;
            return (((nt * spanCount + span) * maskCount) + mask) * wordCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GuidedBaseOffset(
            int nt,
            int i,
            int j,
            int pointCount,
            int maskCount,
            int spanCount,
            int wordCount)
        {
            int span = i * pointCount + j;
            return (nt * spanCount + span) * maskCount * wordCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasAnyBits(ulong[] bits, int offset, int wordCount)
        {
            for (int w = 0; w < wordCount; w++)
            {
                if (bits[offset + w] != 0UL)
                    return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool OrBits(ulong[] dest, int destOffset, ulong[] source, int sourceOffset, int wordCount)
        {
            bool changed = false;
            for (int w = 0; w < wordCount; w++)
            {
                ulong oldValue = dest[destOffset + w];
                ulong newValue = oldValue | source[sourceOffset + w];
                if (newValue != oldValue)
                {
                    dest[destOffset + w] = newValue;
                    changed = true;
                }
            }

            return changed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool OrAndBits(
            ulong[] dest,
            int destOffset,
            ulong[] left,
            int leftOffset,
            ulong[] right,
            int rightOffset,
            int wordCount)
        {
            bool changed = false;
            for (int w = 0; w < wordCount; w++)
            {
                ulong oldValue = dest[destOffset + w];
                ulong newValue = oldValue | (left[leftOffset + w] & right[rightOffset + w]);
                if (newValue != oldValue)
                {
                    dest[destOffset + w] = newValue;
                    changed = true;
                }
            }

            return changed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetBit(ulong[] bits, int offset, int bitIndex)
        {
            bits[offset + (bitIndex >> 6)] |= 1UL << (bitIndex & 63);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SetBitIfChanged(ulong[] bits, int offset, int bitIndex)
        {
            int wordOffset = offset + (bitIndex >> 6);
            ulong oldValue = bits[wordOffset];
            ulong newValue = oldValue | (1UL << (bitIndex & 63));
            if (newValue == oldValue)
                return false;
            bits[wordOffset] = newValue;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasBit(ulong[] bits, int offset, int bitIndex)
        {
            return ((bits[offset + (bitIndex >> 6)] >> (bitIndex & 63)) & 1UL) != 0UL;
        }

        private static bool AddGuidedExistingUnit(
            ulong[] noUse,
            ulong[] used,
            int lhs,
            int y,
            int ruleMask,
            int i,
            int j,
            int maskCount,
            int pointCount,
            int spanCount,
            int wordCount)
        {
            bool changed = false;
            int sourceBase = GuidedBaseOffset(y, i, j, pointCount, maskCount, spanCount, wordCount);
            int destBase = GuidedBaseOffset(lhs, i, j, pointCount, maskCount, spanCount, wordCount);
            for (int sourceMask = 0; sourceMask < maskCount; sourceMask++)
            {
                int destMask = sourceMask | ruleMask;
                int source = sourceBase + sourceMask * wordCount;
                int dest = destBase + destMask * wordCount;
                changed |= OrBits(noUse, dest, noUse, source, wordCount);
                changed |= OrBits(used, dest, used, source, wordCount);
            }

            return changed;
        }

        private static bool AddGuidedCandidateUnit(
            ulong[] noUse,
            ulong[] used,
            int lhs,
            int y,
            int candidateIndex,
            int i,
            int j,
            int maskCount,
            int pointCount,
            int spanCount,
            int wordCount)
        {
            bool changed = false;
            int sourceBase = GuidedBaseOffset(y, i, j, pointCount, maskCount, spanCount, wordCount);
            int destBase = GuidedBaseOffset(lhs, i, j, pointCount, maskCount, spanCount, wordCount);
            int candidateWord = candidateIndex >> 6;
            ulong candidateBit = 1UL << (candidateIndex & 63);
            for (int mask = 0; mask < maskCount; mask++)
            {
                int source = sourceBase + mask * wordCount;
                if (((noUse[source + candidateWord] | used[source + candidateWord]) & candidateBit) == 0UL)
                {
                    continue;
                }

                int wordOffset = destBase + mask * wordCount + candidateWord;
                ulong oldValue = used[wordOffset];
                ulong newValue = oldValue | candidateBit;
                if (newValue != oldValue)
                {
                    used[wordOffset] = newValue;
                    changed = true;
                }
            }

            return changed;
        }

        private static void AddGuidedExistingBinary(
            ulong[] noUse,
            ulong[] used,
            int lhs,
            int y,
            int z,
            int ruleMask,
            int i,
            int split,
            int j,
            int maskCount,
            int pointCount,
            int spanCount,
            int wordCount,
            int liveMaskStamp)
        {
            int leftBase = GuidedBaseOffset(y, i, split, pointCount, maskCount, spanCount, wordCount);
            int rightBase = GuidedBaseOffset(z, split, j, pointCount, maskCount, spanCount, wordCount);
            int destBase = GuidedBaseOffset(lhs, i, j, pointCount, maskCount, spanCount, wordCount);

            var leftLiveMasks = t_guidedLeftLiveMasks;
            var rightLiveMasks = t_guidedRightLiveMasks;
            int leftLiveMaskOffset = y * maskCount;
            int rightLiveMaskOffset = z * maskCount;
            int leftLiveMaskCount = GetGuidedCachedLiveMaskCount(
                noUse,
                used,
                y,
                leftBase,
                maskCount,
                wordCount,
                leftLiveMasks,
                t_guidedLeftLiveMaskCounts,
                t_guidedLeftLiveMaskStamps,
                liveMaskStamp);
            int rightLiveMaskCount = GetGuidedCachedLiveMaskCount(
                noUse,
                used,
                z,
                rightBase,
                maskCount,
                wordCount,
                rightLiveMasks,
                t_guidedRightLiveMaskCounts,
                t_guidedRightLiveMaskStamps,
                liveMaskStamp);
            if (leftLiveMaskCount == 0 || rightLiveMaskCount == 0)
                return;

            AddGuidedExistingBinaryLivePairs(
                noUse,
                used,
                leftBase,
                rightBase,
                destBase,
                ruleMask,
                wordCount,
                leftLiveMasks,
                leftLiveMaskOffset,
                leftLiveMaskCount,
                rightLiveMasks,
                rightLiveMaskOffset,
                rightLiveMaskCount);
        }

        private static int GetGuidedCachedLiveMaskCount(
            ulong[] noUse,
            ulong[] used,
            int nt,
            int baseOffset,
            int maskCount,
            int wordCount,
            int[] liveMasks,
            int[] liveMaskCounts,
            int[] liveMaskStamps,
            int liveMaskStamp)
        {
            if (liveMaskStamps[nt] == liveMaskStamp)
                return liveMaskCounts[nt];

            int liveMaskOffset = nt * maskCount;
            int liveMaskCount = FillGuidedLiveMasks(noUse, used, baseOffset, maskCount, wordCount, liveMasks, liveMaskOffset);
            liveMaskCounts[nt] = liveMaskCount;
            liveMaskStamps[nt] = liveMaskStamp;
            return liveMaskCount;
        }

        private static int FillGuidedLiveMasks(
            ulong[] noUse,
            ulong[] used,
            int baseOffset,
            int maskCount,
            int wordCount,
            int[] liveMasks,
            int liveMaskOffset)
        {
            int liveCount = 0;
            for (int mask = 0; mask < maskCount; mask++)
            {
                int offset = baseOffset + mask * wordCount;
                for (int w = 0; w < wordCount; w++)
                {
                    if ((noUse[offset + w] | used[offset + w]) == 0UL)
                        continue;

                    liveMasks[liveMaskOffset + liveCount] = mask;
                    liveCount++;
                    break;
                }
            }

            return liveCount;
        }

        private static void AddGuidedExistingBinaryLivePairs(
            ulong[] noUse,
            ulong[] used,
            int leftBase,
            int rightBase,
            int destBase,
            int ruleMask,
            int wordCount,
            int[] leftLiveMasks,
            int leftLiveMaskOffset,
            int leftLiveMaskCount,
            int[] rightLiveMasks,
            int rightLiveMaskOffset,
            int rightLiveMaskCount)
        {
            for (int li = 0; li < leftLiveMaskCount; li++)
            {
                int leftMask = leftLiveMasks[leftLiveMaskOffset + li];
                int left = leftBase + leftMask * wordCount;
                for (int ri = 0; ri < rightLiveMaskCount; ri++)
                {
                    int rightMask = rightLiveMasks[rightLiveMaskOffset + ri];
                    int destMask = leftMask | rightMask | ruleMask;
                    int right = rightBase + rightMask * wordCount;
                    int dest = destBase + destMask * wordCount;
                    AddGuidedExistingBinaryPair(noUse, used, left, right, dest, wordCount);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddGuidedExistingBinaryPair(
            ulong[] noUse,
            ulong[] used,
            int left,
            int right,
            int dest,
            int wordCount)
        {
            for (int w = 0; w < wordCount; w++)
            {
                ulong ln = noUse[left + w];
                ulong lu = used[left + w];
                ulong rn = noUse[right + w];
                ulong ru = used[right + w];
                if (((ln | lu) & (rn | ru)) == 0UL)
                    continue;

                ulong addNo = ln & rn;
                ulong addUsed = (lu & (rn | ru)) | (ln & ru);
                if (addNo != 0UL)
                    noUse[dest + w] |= addNo;
                if (addUsed != 0UL)
                    used[dest + w] |= addUsed;
            }
        }

        private static void AddGuidedCandidateBinary(
            ulong[] noUse,
            ulong[] used,
            int lhs,
            int y,
            int z,
            int candidateIndex,
            int i,
            int split,
            int j,
            int maskCount,
            int pointCount,
            int spanCount,
            int wordCount)
        {
            int leftBase = GuidedBaseOffset(y, i, split, pointCount, maskCount, spanCount, wordCount);
            int rightBase = GuidedBaseOffset(z, split, j, pointCount, maskCount, spanCount, wordCount);
            int destBase = GuidedBaseOffset(lhs, i, j, pointCount, maskCount, spanCount, wordCount);
            int candidateWord = candidateIndex >> 6;
            ulong candidateBit = 1UL << (candidateIndex & 63);
            for (int leftMask = 0; leftMask < maskCount; leftMask++)
            {
                int left = leftBase + leftMask * wordCount;
                if (((noUse[left + candidateWord] | used[left + candidateWord]) & candidateBit) == 0UL)
                    continue;

                for (int rightMask = 0; rightMask < maskCount; rightMask++)
                {
                    int right = rightBase + rightMask * wordCount;
                    if (((noUse[right + candidateWord] | used[right + candidateWord]) & candidateBit) == 0UL)
                    {
                        continue;
                    }

                    int destMask = leftMask | rightMask;
                    used[destBase + destMask * wordCount + candidateWord] |= candidateBit;
                }
            }
        }

        private sealed class ShadowPatternBuffer
        {
            public ulong[] FixedBits = new ulong[128];
            public ulong[] WildcardMasks = new ulong[128];
            public ulong[] WildcardLabels = new ulong[128];
            public int[] FloorLengths = new int[128];
            public int Count;
            public bool Disabled;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Clear()
            {
                Count = 0;
                Disabled = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Add(ulong fixedBits, ulong wildcardMask, ulong wildcardLabels, int floorLength)
            {
                if (Disabled) return false;
                if (Count >= MaxShadowPatterns)
                {
                    Disabled = true;
                    return false;
                }

                if (Count == FixedBits.Length)
                {
                    int newSize = FixedBits.Length * 2;
                    Array.Resize(ref FixedBits, newSize);
                    Array.Resize(ref WildcardMasks, newSize);
                    Array.Resize(ref WildcardLabels, newSize);
                    Array.Resize(ref FloorLengths, newSize);
                }

                int idx = Count++;
                FixedBits[idx] = fixedBits;
                WildcardMasks[idx] = wildcardMask;
                WildcardLabels[idx] = wildcardLabels;
                FloorLengths[idx] = floorLength;
                return true;
            }
        }

        private sealed class PatternSet
        {
            private const int InitialEntryCapacity = 4;
            private const int InitialBucketCapacity = 8;

            private int[] _buckets;
            private int[] _next;
            private ulong[] _fixedBits;
            private ulong[] _wildcardMasks;
            private ulong[] _wildcardLabels;
            private int[] _ruleMasks;
            private int[] _usedBuckets;
            private int _count;
            private int _usedBucketCount;

            public PatternSet()
            {
                _buckets = new int[InitialBucketCapacity];
                Array.Fill(_buckets, -1);
                _next = new int[InitialEntryCapacity];
                _fixedBits = new ulong[InitialEntryCapacity];
                _wildcardMasks = new ulong[InitialEntryCapacity];
                _wildcardLabels = new ulong[InitialEntryCapacity];
                _ruleMasks = new int[InitialEntryCapacity];
                _usedBuckets = new int[InitialBucketCapacity];
            }

            public int Count => _count;
            public ulong[] FixedBits => _fixedBits;
            public ulong[] WildcardMasks => _wildcardMasks;
            public ulong[] WildcardLabels => _wildcardLabels;
            public int[] RuleMasks => _ruleMasks;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int ComputeHash(ulong fixedBits, ulong wildcardMask, ulong wildcardLabels, int ruleMask)
            {
                unchecked
                {
                    int hash = (int)fixedBits ^ (int)(fixedBits >> 32);
                    hash = (hash * 397) ^ ((int)wildcardMask ^ (int)(wildcardMask >> 32));
                    hash = (hash * 397) ^ ((int)wildcardLabels ^ (int)(wildcardLabels >> 32));
                    hash = (hash * 397) ^ ruleMask;
                    return hash;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Add(ulong fixedBits, ulong wildcardMask, ulong wildcardLabels, int ruleMask)
            {
                int bucketMask = _buckets.Length - 1;
                int bucket = ComputeHash(fixedBits, wildcardMask, wildcardLabels, ruleMask) & bucketMask;
                for (int i = _buckets[bucket]; i >= 0; i = _next[i])
                {
                    if (_fixedBits[i] == fixedBits &&
                        _wildcardMasks[i] == wildcardMask &&
                        _wildcardLabels[i] == wildcardLabels &&
                        _ruleMasks[i] == ruleMask)
                    {
                        return false;
                    }
                }

                if (_count == _fixedBits.Length)
                {
                    Resize(_fixedBits.Length * 2, _buckets.Length * 2);
                    bucketMask = _buckets.Length - 1;
                    bucket = ComputeHash(fixedBits, wildcardMask, wildcardLabels, ruleMask) & bucketMask;
                }

                int idx = _count++;
                _fixedBits[idx] = fixedBits;
                _wildcardMasks[idx] = wildcardMask;
                _wildcardLabels[idx] = wildcardLabels;
                _ruleMasks[idx] = ruleMask;
                if (_buckets[bucket] < 0)
                {
                    if (_usedBucketCount == _usedBuckets.Length)
                        Array.Resize(ref _usedBuckets, _usedBuckets.Length * 2);
                    _usedBuckets[_usedBucketCount++] = bucket;
                }
                _next[idx] = _buckets[bucket];
                _buckets[bucket] = idx;
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Clear()
            {
                for (int i = 0; i < _usedBucketCount; i++)
                    _buckets[_usedBuckets[i]] = -1;
                _count = 0;
                _usedBucketCount = 0;
            }

            private void Resize(int newEntryCapacity, int newBucketCapacity)
            {
                Array.Resize(ref _fixedBits, newEntryCapacity);
                Array.Resize(ref _wildcardMasks, newEntryCapacity);
                Array.Resize(ref _wildcardLabels, newEntryCapacity);
                Array.Resize(ref _ruleMasks, newEntryCapacity);
                Array.Resize(ref _next, newEntryCapacity);
                if (_usedBuckets.Length < newBucketCapacity)
                    Array.Resize(ref _usedBuckets, newBucketCapacity);

                _buckets = new int[newBucketCapacity];
                Array.Fill(_buckets, -1);
                _usedBucketCount = 0;
                int bucketMask = newBucketCapacity - 1;
                for (int i = 0; i < _count; i++)
                {
                    int bucket = ComputeHash(_fixedBits[i], _wildcardMasks[i], _wildcardLabels[i], _ruleMasks[i]) & bucketMask;
                    if (_buckets[bucket] < 0)
                        _usedBuckets[_usedBucketCount++] = bucket;
                    _next[i] = _buckets[bucket];
                    _buckets[bucket] = i;
                }
            }
        }

        private static WildcardPruneOutcome EvaluateByRootOverlayStringDp(
            RootPruneContext ctx,
            IReadOnlyList<Rule> newRules,
            ReadOnlySpan<int> nextUnparsed,
            ILogger logger)
        {
            int maxLen = nextUnparsed.Length;
            int fullRuleMask = (1 << newRules.Count) - 1;
            if (!TryEncodeSentenceIndices(
                    nextUnparsed,
                    ctx.PosToIndex,
                    ctx.BitsPerSymbol,
                    out var sentenceIndices,
                    out ulong targetEncoded,
                    out var sentenceTokenMasks))
            {
                return WildcardPruneOutcome.SkippedNoUnparsed;
            }

            var ntToIdx = t_stringDpNtToIdx ??= new Dictionary<int, int>();
            ntToIdx.Clear();
            foreach (var kv in ctx.RootNtToIdx)
                ntToIdx[kv.Key] = kv.Value;
            AddRuleSymbols(newRules, ctx.PosToIndex, ntToIdx);

            int ntCount = ntToIdx.Count;
            if (ntCount == 0 || ctx.StartIdx < 0) return WildcardPruneOutcome.Pruned;

            if (t_stringDpMinLengths == null || t_stringDpMinLengths.Length < ntCount)
                t_stringDpMinLengths = new int[ntCount];
            var minLengths = t_stringDpMinLengths;
            Array.Copy(ctx.RootMinLengths, minLengths, ctx.RootNtCount);
            if (ntCount > ctx.RootNtCount)
                Array.Fill(minLengths, int.MaxValue, ctx.RootNtCount, ntCount - ctx.RootNtCount);

            bool minChanged;
            do
            {
                minChanged = false;
                minChanged |= RelaxMinLengths(ctx.BaseRules, ctx.PosToIndex, ntToIdx, minLengths);
                minChanged |= RelaxMinLengths(newRules, ctx.PosToIndex, ntToIdx, minLengths);
            } while (minChanged);

            var deltaEpsilonRules = t_stringDpEpsilonRules ??= new List<(int lhs, int mask)>();
            deltaEpsilonRules.Clear();
            var deltaTerminalRules = t_stringDpTerminalRules ??= new List<(int lhs, ulong posEncoded, int mask)>();
            deltaTerminalRules.Clear();
            var deltaUnitRules = t_stringDpUnitRules ??= new List<(int lhs, int y, int mask)>();
            deltaUnitRules.Clear();
            var deltaBinaryRules = t_stringDpBinaryRules ??= new List<(int lhs, int y, int z, int mask)>();
            deltaBinaryRules.Clear();

            for (int i = 0; i < newRules.Count; i++)
            {
                int ruleMask = 1 << i;
                var classifyOutcome = ClassifyRule(
                    newRules[i],
                    ruleMask,
                    ctx,
                    ntToIdx,
                    deltaEpsilonRules,
                    deltaTerminalRules,
                    deltaUnitRules,
                    deltaBinaryRules);
                if (classifyOutcome != WildcardPruneOutcome.KeptMatched) return classifyOutcome;
            }

            EnsureStringDpCapacity(ntCount, maxLen);
            var overlayDp = t_stringDp;
            var overlayPops = t_stringDpPopulatedLens;
            for (int i = 0; i < ntCount; i++)
                overlayPops[i] = RentIntList();

            if (t_stringDpShiftAmounts == null || t_stringDpShiftAmounts.Length < maxLen + 1)
                t_stringDpShiftAmounts = new int[maxLen + 1];
            var shiftAmounts = t_stringDpShiftAmounts;
            for (int len = 0; len <= maxLen; len++)
                shiftAmounts[len] = ctx.BitsPerSymbol * len;

            bool shadowEnabled =
                ctx.ShadowEvidence != null &&
                (ctx.ShadowEvidence.HasExactPosEvidence || ctx.ShadowEvidence.MaxEvidenceByLength != null);
            int bitsPerNtLabel = 0;
            int[] labelShiftAmounts = null;
            ShadowPatternBuffer shadowPatterns = null;
            if (shadowEnabled)
            {
                bitsPerNtLabel = GetBitsNeeded(ntCount + 1);
                if ((long)bitsPerNtLabel * maxLen > MaxBitsPerLong)
                {
                    shadowEnabled = false;
                    bitsPerNtLabel = 0;
                }
                else
                {
                    if (t_stringDpLabelShiftAmounts == null || t_stringDpLabelShiftAmounts.Length < maxLen + 1)
                        t_stringDpLabelShiftAmounts = new int[maxLen + 1];
                    labelShiftAmounts = t_stringDpLabelShiftAmounts;
                    for (int len = 0; len <= maxLen; len++)
                        labelShiftAmounts[len] = bitsPerNtLabel * len;
                    shadowPatterns = t_shadowPatterns ??= new ShadowPatternBuffer();
                    shadowPatterns.Clear();
                }
            }

            var rootDp = ctx.FrozenRootDp.Cells;
            var rootPops = ctx.FrozenRootDp.PopulatedLengths;
            var rootBinaryRules = ctx.RootBinaryRules;
            var rootUnitRules = ctx.RootUnitRules;
            bool hasUnitRules = rootUnitRules.Length > 0 || deltaUnitRules.Count > 0;
            int startIdx = ctx.StartIdx;
            bool sawShadowNoEvidence = false;
            bool sawShadowCount = false;

            try
            {
                for (int len = 0; len <= maxLen; len++)
                {
                    if (len == 0)
                    {
                        var epsilonSpan = CollectionsMarshal.AsSpan(deltaEpsilonRules);
                        for (int i = 0; i < epsilonSpan.Length; i++)
                        {
                            ref readonly var epsilon = ref epsilonSpan[i];
                            AddPattern(overlayDp, overlayPops, epsilon.lhs, 0, 0UL, 0UL, 0UL, epsilon.mask);
                        }
                    }

                    if (len == 1)
                    {
                        for (int ntIdx = 0; ntIdx < ntCount; ntIdx++)
                        {
                            if (ntIdx == startIdx) continue;
                            if (minLengths[ntIdx] == int.MaxValue)
                                AddPattern(
                                    overlayDp,
                                    overlayPops,
                                    ntIdx,
                                    1,
                                    0UL,
                                    1UL,
                                    EncodeWildcardLabel(ntIdx, 0, bitsPerNtLabel),
                                    0);
                        }

                        var terminalSpan = CollectionsMarshal.AsSpan(deltaTerminalRules);
                        for (int i = 0; i < terminalSpan.Length; i++)
                        {
                            ref readonly var terminal = ref terminalSpan[i];
                            AddPattern(overlayDp, overlayPops, terminal.lhs, 1, terminal.posEncoded, 0UL, 0UL, terminal.mask);
                        }
                    }

                    if (len > 0)
                    {
                        for (int bi = 0; bi < rootBinaryRules.Length; bi++)
                        {
                            var binary = rootBinaryRules[bi];
                            PatternSet dest = overlayDp[binary.lhs][len];
                            AddRootRuleOverlayCombinations(
                                overlayDp,
                                overlayPops,
                                rootDp,
                                rootPops,
                                binary.lhs,
                                binary.y,
                                binary.z,
                                len,
                                shiftAmounts,
                                labelShiftAmounts,
                                ref dest);
                        }

                        var deltaBinarySpan = CollectionsMarshal.AsSpan(deltaBinaryRules);
                        for (int bi = 0; bi < deltaBinarySpan.Length; bi++)
                        {
                            ref readonly var binary = ref deltaBinarySpan[bi];
                            PatternSet dest = overlayDp[binary.lhs][len];
                            AddDeltaRuleCombinations(
                                overlayDp,
                                overlayPops,
                                rootDp,
                                rootPops,
                                binary.lhs,
                                binary.y,
                                binary.z,
                                binary.mask,
                                len,
                                shiftAmounts,
                                labelShiftAmounts,
                                ref dest);
                        }
                    }

                    if (hasUnitRules)
                    {
                        var deltaUnitSpan = CollectionsMarshal.AsSpan(deltaUnitRules);
                        bool changed;
                        do
                        {
                            changed = false;
                            for (int ui = 0; ui < rootUnitRules.Length; ui++)
                            {
                                var unit = rootUnitRules[ui];
                                var source = overlayDp[unit.y][len];
                                if (source == null) continue;

                                var dest = overlayDp[unit.lhs][len];
                                if (dest == null)
                                {
                                    dest = RentPatternSet();
                                    overlayDp[unit.lhs][len] = dest;
                                    overlayPops[unit.lhs].Add(len);
                                }

                                if (AddOverlayAsOverlay(dest, source, 0))
                                    changed = true;
                            }

                            for (int ui = 0; ui < deltaUnitSpan.Length; ui++)
                            {
                                ref readonly var unit = ref deltaUnitSpan[ui];
                                var dest = overlayDp[unit.lhs][len];
                                var rootSource = GetRootCell(rootDp, unit.y, len);
                                if (rootSource != null)
                                {
                                    if (dest == null)
                                    {
                                        dest = RentPatternSet();
                                        overlayDp[unit.lhs][len] = dest;
                                        overlayPops[unit.lhs].Add(len);
                                    }

                                    if (AddRootAsOverlay(dest, rootSource, unit.mask))
                                        changed = true;
                                }

                                var overlaySource = overlayDp[unit.y][len];
                                if (overlaySource == null) continue;
                                if (dest == null)
                                {
                                    dest = RentPatternSet();
                                    overlayDp[unit.lhs][len] = dest;
                                    overlayPops[unit.lhs].Add(len);
                                }

                                if (AddOverlayAsOverlay(dest, overlaySource, unit.mask))
                                    changed = true;
                            }
                        } while (changed);
                    }

                    var startPatterns = overlayDp[startIdx][len];
                    if (startPatterns == null) continue;

                    var fixedBits = startPatterns.FixedBits;
                    var wildcardMasks = startPatterns.WildcardMasks;
                    var wildcardLabels = startPatterns.WildcardLabels;
                    var ruleMasks = startPatterns.RuleMasks;
                    int startCount = startPatterns.Count;
                    for (int i = 0; i < startCount; i++)
                    {
                        if (ruleMasks[i] != fullRuleMask) continue;
                        if (PatternMatches(
                                fixedBits[i],
                                wildcardMasks[i],
                                len,
                                sentenceIndices,
                                sentenceTokenMasks,
                                maxLen,
                                targetEncoded,
                                ctx.BitsPerSymbol,
                                logger))
                        {
                            if (shadowEnabled &&
                                MatchIsPrunedByShadow(
                                    ctx,
                                    shadowPatterns,
                                    fixedBits[i],
                                    wildcardMasks[i],
                                    wildcardLabels[i],
                                    len,
                                    sentenceIndices,
                                    maxLen,
                                    ctx.BitsPerSymbol,
                                    bitsPerNtLabel,
                                    out var shadowOutcome))
                            {
                                if (shadowOutcome == WildcardPruneOutcome.PrunedShadowNoEvidence)
                                    sawShadowNoEvidence = true;
                                else if (shadowOutcome == WildcardPruneOutcome.PrunedShadowCount)
                                    sawShadowCount = true;
                                continue;
                            }

                            return WildcardPruneOutcome.KeptMatched;
                        }
                    }

                    if (shadowEnabled && shadowPatterns != null && !shadowPatterns.Disabled)
                    {
                        for (int i = 0; i < startCount; i++)
                        {
                            if (ruleMasks[i] == fullRuleMask &&
                                wildcardMasks[i] != 0UL &&
                                wildcardLabels[i] != 0UL)
                            {
                                shadowPatterns.Add(fixedBits[i], wildcardMasks[i], wildcardLabels[i], len);
                            }
                        }
                    }
                }

                return sawShadowNoEvidence
                    ? WildcardPruneOutcome.PrunedShadowNoEvidence
                    : sawShadowCount
                        ? WildcardPruneOutcome.PrunedShadowCount
                        : WildcardPruneOutcome.Pruned;
            }
            finally
            {
                CleanupStringDp(ntCount, overlayDp, overlayPops);
            }
        }

        private static WildcardPruneOutcome EvaluateByWildcardStringDp(
            RootPruneContext ctx,
            IReadOnlyList<Rule> newRules,
            ReadOnlySpan<int> nextUnparsed,
            ILogger logger)
        {
            int maxLen = nextUnparsed.Length;
            int startSymbolId = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);
            int fullRuleMask = (1 << newRules.Count) - 1;
            if (!TryEncodeSentenceIndices(
                    nextUnparsed,
                    ctx.PosToIndex,
                    ctx.BitsPerSymbol,
                    out var sentenceIndices,
                    out ulong targetEncoded,
                    out var sentenceTokenMasks))
            {
                return WildcardPruneOutcome.SkippedNoUnparsed;
            }

            var ntToIdx = t_stringDpNtToIdx ??= new Dictionary<int, int>();
            ntToIdx.Clear();
            AddNonTerminal(ntToIdx, ctx.PosToIndex, startSymbolId);
            AddRuleSymbols(ctx.BaseRules, ctx.PosToIndex, ntToIdx);
            AddRuleSymbols(newRules, ctx.PosToIndex, ntToIdx);

            int ntCount = ntToIdx.Count;
            if (ntCount == 0) return WildcardPruneOutcome.Pruned;

            if (t_stringDpMinLengths == null || t_stringDpMinLengths.Length < ntCount)
                t_stringDpMinLengths = new int[ntCount];
            var minLengths = t_stringDpMinLengths;
            Array.Fill(minLengths, int.MaxValue, 0, ntCount);
            bool minChanged;
            do
            {
                minChanged = false;
                minChanged |= RelaxMinLengths(ctx.BaseRules, ctx.PosToIndex, ntToIdx, minLengths);
                minChanged |= RelaxMinLengths(newRules, ctx.PosToIndex, ntToIdx, minLengths);
            } while (minChanged);

            var epsilonRules = t_stringDpEpsilonRules ??= new List<(int lhs, int mask)>();
            epsilonRules.Clear();
            var terminalRules = t_stringDpTerminalRules ??= new List<(int lhs, ulong posEncoded, int mask)>();
            terminalRules.Clear();
            var unitRules = t_stringDpUnitRules ??= new List<(int lhs, int y, int mask)>();
            unitRules.Clear();
            var binaryRules = t_stringDpBinaryRules ??= new List<(int lhs, int y, int z, int mask)>();
            binaryRules.Clear();

            var classifyOutcome = ClassifyRules(ctx.BaseRules, 0, ctx, ntToIdx, epsilonRules, terminalRules, unitRules, binaryRules);
            if (classifyOutcome != WildcardPruneOutcome.KeptMatched) return classifyOutcome;
            for (int i = 0; i < newRules.Count; i++)
            {
                int ruleMask = 1 << i;
                classifyOutcome = ClassifyRule(newRules[i], ruleMask, ctx, ntToIdx, epsilonRules, terminalRules, unitRules, binaryRules);
                if (classifyOutcome != WildcardPruneOutcome.KeptMatched) return classifyOutcome;
            }

            EnsureStringDpCapacity(ntCount, maxLen);
            var dp = t_stringDp;
            var populatedLengths = t_stringDpPopulatedLens;
            for (int i = 0; i < ntCount; i++)
            {
                populatedLengths[i] = RentIntList();
            }
            if (t_stringDpShiftAmounts == null || t_stringDpShiftAmounts.Length < maxLen + 1)
                t_stringDpShiftAmounts = new int[maxLen + 1];
            var shiftAmounts = t_stringDpShiftAmounts;
            for (int len = 0; len <= maxLen; len++)
                shiftAmounts[len] = ctx.BitsPerSymbol * len;
            bool hasUnitRules = unitRules.Count > 0;

            int startIdx = ntToIdx[startSymbolId];
            try
            {
                for (int len = 0; len <= maxLen; len++)
                {
                    if (len == 0)
                    {
                        var epsilonSpan = CollectionsMarshal.AsSpan(epsilonRules);
                        for (int i = 0; i < epsilonSpan.Length; i++)
                        {
                            ref readonly var epsilon = ref epsilonSpan[i];
                            AddPattern(dp, populatedLengths, epsilon.lhs, 0, 0UL, 0UL, 0UL, epsilon.mask);
                        }
                    }

                    if (len == 1)
                    {
                        for (int ntIdx = 0; ntIdx < ntCount; ntIdx++)
                        {
                            if (ntIdx == startIdx) continue;
                            if (minLengths[ntIdx] == int.MaxValue)
                                AddPattern(dp, populatedLengths, ntIdx, 1, 0UL, 1UL, 0UL, 0);
                        }

                        var terminalSpan = CollectionsMarshal.AsSpan(terminalRules);
                        for (int i = 0; i < terminalSpan.Length; i++)
                        {
                            ref readonly var terminal = ref terminalSpan[i];
                            AddPattern(dp, populatedLengths, terminal.lhs, 1, terminal.posEncoded, 0UL, 0UL, terminal.mask);
                        }
                    }

                    if (len > 0)
                    {
                        var binarySpan = CollectionsMarshal.AsSpan(binaryRules);
                        for (int bi = 0; bi < binarySpan.Length; bi++)
                        {
                            ref readonly var binary = ref binarySpan[bi];
                            int lhs = binary.lhs;
                            int y = binary.y;
                            int z = binary.z;
                            int mask = binary.mask;
                            var dpLhs = dp[lhs];
                            var dest = dpLhs[len];
                            var popsY = populatedLengths[y];
                            for (int pi = 0; pi < popsY.Count; pi++)
                            {
                                int lenY = popsY[pi];
                                if (lenY > len) break;
                                int lenZ = len - lenY;
                                var stringsY = dp[y][lenY];
                                var stringsZ = dp[z][lenZ];
                                if (stringsY == null || stringsZ == null) continue;

                                if (dest == null)
                                {
                                    dest = RentPatternSet();
                                    dpLhs[len] = dest;
                                    populatedLengths[lhs].Add(len);
                                }

                                int shiftBits = shiftAmounts[lenY];
                                var fixedY = stringsY.FixedBits;
                                var wildcardY = stringsY.WildcardMasks;
                                var labelsY = stringsY.WildcardLabels;
                                var masksY = stringsY.RuleMasks;
                                var fixedZ = stringsZ.FixedBits;
                                var wildcardZ = stringsZ.WildcardMasks;
                                var labelsZ = stringsZ.WildcardLabels;
                                var masksZ = stringsZ.RuleMasks;
                                int countY = stringsY.Count;
                                int countZ = stringsZ.Count;
                                for (int iy = 0; iy < countY; iy++)
                                {
                                    ulong fixedBitsY = fixedY[iy];
                                    ulong wildcardMaskY = wildcardY[iy];
                                    ulong wildcardLabelsY = labelsY[iy];
                                    int ruleMaskY = masksY[iy];
                                    for (int iz = 0; iz < countZ; iz++)
                                    {
                                        dest.Add(
                                            fixedBitsY | (fixedZ[iz] << shiftBits),
                                            wildcardMaskY | (wildcardZ[iz] << lenY),
                                            wildcardLabelsY | labelsZ[iz],
                                            ruleMaskY | masksZ[iz] | mask);
                                    }
                                }
                            }
                        }
                    }

                    if (hasUnitRules)
                    {
                        var unitSpan = CollectionsMarshal.AsSpan(unitRules);
                        bool changed;
                        do
                        {
                            changed = false;
                            for (int ui = 0; ui < unitSpan.Length; ui++)
                            {
                                ref readonly var unit = ref unitSpan[ui];
                                int lhs = unit.lhs;
                                int y = unit.y;
                                int mask = unit.mask;
                                var source = dp[y][len];
                                if (source == null) continue;

                                var dest = dp[lhs][len];
                                if (dest == null)
                                {
                                    dest = RentPatternSet();
                                    dp[lhs][len] = dest;
                                    populatedLengths[lhs].Add(len);
                                }

                                if (lhs == y)
                                {
                                    if (mask == 0) continue;
                                    var sourceFixedBits = source.FixedBits;
                                    var sourceWildcardMasks = source.WildcardMasks;
                                    var sourceWildcardLabels = source.WildcardLabels;
                                    var sourceRuleMasks = source.RuleMasks;
                                    int sourceCount = source.Count;
                                    for (int s = 0; s < sourceCount; s++)
                                    {
                                        if (dest.Add(sourceFixedBits[s], sourceWildcardMasks[s], sourceWildcardLabels[s], sourceRuleMasks[s] | mask))
                                            changed = true;
                                    }
                                }
                                else
                                {
                                    var sourceFixedBits = source.FixedBits;
                                    var sourceWildcardMasks = source.WildcardMasks;
                                    var sourceWildcardLabels = source.WildcardLabels;
                                    var sourceRuleMasks = source.RuleMasks;
                                    int sourceCount = source.Count;
                                    for (int s = 0; s < sourceCount; s++)
                                    {
                                        if (dest.Add(sourceFixedBits[s], sourceWildcardMasks[s], sourceWildcardLabels[s], sourceRuleMasks[s] | mask))
                                            changed = true;
                                    }
                                }
                            }
                        } while (changed);
                    }

                    var startPatterns = dp[startIdx][len];
                    if (startPatterns == null) continue;

                    var fixedBits = startPatterns.FixedBits;
                    var wildcardMasks = startPatterns.WildcardMasks;
                    var ruleMasks = startPatterns.RuleMasks;
                    int startCount = startPatterns.Count;
                    for (int i = 0; i < startCount; i++)
                    {
                        if (ruleMasks[i] != fullRuleMask) continue;
                        if (PatternMatches(
                                fixedBits[i],
                                wildcardMasks[i],
                                len,
                                sentenceIndices,
                                sentenceTokenMasks,
                                maxLen,
                                targetEncoded,
                                ctx.BitsPerSymbol,
                                logger))
                        {
                            return WildcardPruneOutcome.KeptMatched;
                        }
                    }
                }

                return WildcardPruneOutcome.Pruned;
            }
            finally
            {
                CleanupStringDp(ntCount, dp, populatedLengths);
            }
        }

        private static bool MatchIsPrunedByShadow(
            RootPruneContext ctx,
            ShadowPatternBuffer shadowPatterns,
            ulong fixedBits,
            ulong wildcardMask,
            ulong wildcardLabels,
            int floorLength,
            int[] sentenceIndices,
            int sentenceLength,
            int bitsPerSymbol,
            int bitsPerNtLabel,
            out WildcardPruneOutcome outcome)
        {
            outcome = WildcardPruneOutcome.Pruned;
            if (shadowPatterns == null ||
                shadowPatterns.Count == 0 ||
                shadowPatterns.Disabled ||
                wildcardMask == 0UL ||
                wildcardLabels == 0UL ||
                bitsPerNtLabel <= 0)
            {
                return false;
            }

            int capacity = Math.Max(4, floorLength + 1);
            if (t_shadowForcedLabels == null || t_shadowForcedLabels.Length < capacity)
            {
                t_shadowForcedLabels = new int[capacity];
                t_shadowForcedLengths = new int[capacity];
                t_shadowForcedBits = new ulong[capacity];
            }

            int segmentations = 0;
            bool sawNoEvidence = false;
            bool sawCount = false;
            bool sawSafeOrUnknown = HasSafeOrUnknownSegmentation(
                ctx,
                shadowPatterns,
                fixedBits,
                wildcardMask,
                wildcardLabels,
                floorLength,
                sentenceIndices,
                sentenceLength,
                bitsPerSymbol,
                bitsPerNtLabel,
                cell: 0,
                position: 0,
                forcedCount: 0,
                ref segmentations,
                ref sawNoEvidence,
                ref sawCount);

            if (sawSafeOrUnknown || segmentations == 0)
                return false;

            outcome = sawNoEvidence
                ? WildcardPruneOutcome.PrunedShadowNoEvidence
                : sawCount
                    ? WildcardPruneOutcome.PrunedShadowCount
                    : WildcardPruneOutcome.Pruned;
            return outcome == WildcardPruneOutcome.PrunedShadowNoEvidence ||
                   outcome == WildcardPruneOutcome.PrunedShadowCount;
        }

        private static bool HasSafeOrUnknownSegmentation(
            RootPruneContext ctx,
            ShadowPatternBuffer shadowPatterns,
            ulong fixedBits,
            ulong wildcardMask,
            ulong wildcardLabels,
            int floorLength,
            int[] sentenceIndices,
            int sentenceLength,
            int bitsPerSymbol,
            int bitsPerNtLabel,
            int cell,
            int position,
            int forcedCount,
            ref int segmentations,
            ref bool sawNoEvidence,
            ref bool sawCount)
        {
            if (segmentations >= MaxShadowSegmentations)
                return true;

            if (cell == floorLength)
            {
                if (position != sentenceLength)
                    return false;

                segmentations++;
                var bad = SegmentationShadowBad(
                    ctx,
                    shadowPatterns,
                    forcedCount,
                    bitsPerSymbol,
                    bitsPerNtLabel);
                if (bad == WildcardPruneOutcome.PrunedShadowNoEvidence)
                {
                    sawNoEvidence = true;
                    return false;
                }

                if (bad == WildcardPruneOutcome.PrunedShadowCount)
                {
                    sawCount = true;
                    return false;
                }

                return true;
            }

            int remainingCellsAfter = floorLength - cell - 1;
            if (position + 1 + remainingCellsAfter > sentenceLength)
                return false;

            if (((wildcardMask >> cell) & 1UL) == 0UL)
            {
                if (position >= sentenceLength) return false;
                int expected = ExtractPackedValue(fixedBits, cell, bitsPerSymbol);
                if (sentenceIndices[position] != expected) return false;
                return HasSafeOrUnknownSegmentation(
                    ctx,
                    shadowPatterns,
                    fixedBits,
                    wildcardMask,
                    wildcardLabels,
                    floorLength,
                    sentenceIndices,
                    sentenceLength,
                    bitsPerSymbol,
                    bitsPerNtLabel,
                    cell + 1,
                    position + 1,
                    forcedCount,
                    ref segmentations,
                    ref sawNoEvidence,
                    ref sawCount);
            }

            int label = ExtractWildcardLabel(wildcardLabels, cell, bitsPerNtLabel);
            if (label <= 0)
                return true;

            int maxEnd = sentenceLength - remainingCellsAfter;
            for (int end = position + 1; end <= maxEnd; end++)
            {
                int sliceLen = end - position;
                if (!TryEncodeSentenceSlice(sentenceIndices, position, sliceLen, bitsPerSymbol, out ulong sliceBits))
                    return true;

                t_shadowForcedLabels[forcedCount] = label;
                t_shadowForcedLengths[forcedCount] = sliceLen;
                t_shadowForcedBits[forcedCount] = sliceBits;

                if (HasSafeOrUnknownSegmentation(
                        ctx,
                        shadowPatterns,
                        fixedBits,
                        wildcardMask,
                        wildcardLabels,
                        floorLength,
                        sentenceIndices,
                        sentenceLength,
                        bitsPerSymbol,
                        bitsPerNtLabel,
                        cell + 1,
                        end,
                        forcedCount + 1,
                        ref segmentations,
                        ref sawNoEvidence,
                        ref sawCount))
                {
                    return true;
                }
            }

            return false;
        }

        private static WildcardPruneOutcome SegmentationShadowBad(
            RootPruneContext ctx,
            ShadowPatternBuffer shadowPatterns,
            int forcedCount,
            int bitsPerSymbol,
            int bitsPerNtLabel)
        {
            ResetShadowGeneratedSets();
            int substitutions = 0;
            try
            {
                for (int i = 0; i < shadowPatterns.Count; i++)
                {
                    var outcome = EvaluateShadowPattern(
                        ctx,
                        shadowPatterns.FixedBits[i],
                        shadowPatterns.WildcardMasks[i],
                        shadowPatterns.WildcardLabels[i],
                        shadowPatterns.FloorLengths[i],
                        forcedCount,
                        bitsPerSymbol,
                        bitsPerNtLabel,
                        ref substitutions);

                    if (outcome == WildcardPruneOutcome.PrunedShadowNoEvidence ||
                        outcome == WildcardPruneOutcome.PrunedShadowCount)
                    {
                        return outcome;
                    }
                }

                return WildcardPruneOutcome.KeptMatched;
            }
            finally
            {
                ResetShadowGeneratedSets();
            }
        }

        private static WildcardPruneOutcome EvaluateShadowPattern(
            RootPruneContext ctx,
            ulong fixedBits,
            ulong wildcardMask,
            ulong wildcardLabels,
            int floorLength,
            int forcedCount,
            int bitsPerSymbol,
            int bitsPerNtLabel,
            ref int substitutions)
        {
            return EvaluateShadowPatternCell(
                ctx,
                fixedBits,
                wildcardMask,
                wildcardLabels,
                floorLength,
                forcedCount,
                bitsPerSymbol,
                bitsPerNtLabel,
                cell: 0,
                outputLength: 0,
                outputBits: 0UL,
                ref substitutions);
        }

        private static WildcardPruneOutcome EvaluateShadowPatternCell(
            RootPruneContext ctx,
            ulong fixedBits,
            ulong wildcardMask,
            ulong wildcardLabels,
            int floorLength,
            int forcedCount,
            int bitsPerSymbol,
            int bitsPerNtLabel,
            int cell,
            int outputLength,
            ulong outputBits,
            ref int substitutions)
        {
            if (substitutions >= MaxShadowSubstitutionsPerSegmentation)
                return WildcardPruneOutcome.KeptMatched;

            if (cell == floorLength)
            {
                substitutions++;
                return CheckGeneratedShadowString(ctx, outputLength, outputBits);
            }

            if (((wildcardMask >> cell) & 1UL) == 0UL)
            {
                if (!TryAppendFixedSymbol(
                        outputBits,
                        outputLength,
                        ExtractPackedValue(fixedBits, cell, bitsPerSymbol),
                        bitsPerSymbol,
                        out ulong nextBits))
                {
                    return WildcardPruneOutcome.KeptMatched;
                }

                return EvaluateShadowPatternCell(
                    ctx,
                    fixedBits,
                    wildcardMask,
                    wildcardLabels,
                    floorLength,
                    forcedCount,
                    bitsPerSymbol,
                    bitsPerNtLabel,
                    cell + 1,
                    outputLength + 1,
                    nextBits,
                    ref substitutions);
            }

            int label = ExtractWildcardLabel(wildcardLabels, cell, bitsPerNtLabel);
            if (label <= 0)
                return WildcardPruneOutcome.KeptMatched;

            for (int i = 0; i < forcedCount; i++)
            {
                if (t_shadowForcedLabels[i] != label) continue;

                int sliceLen = t_shadowForcedLengths[i];
                int nextLength = outputLength + sliceLen;
                if ((long)nextLength * bitsPerSymbol > MaxBitsPerLong)
                    return WildcardPruneOutcome.KeptMatched;

                ulong nextBits = outputBits | (t_shadowForcedBits[i] << (outputLength * bitsPerSymbol));
                var outcome = EvaluateShadowPatternCell(
                    ctx,
                    fixedBits,
                    wildcardMask,
                    wildcardLabels,
                    floorLength,
                    forcedCount,
                    bitsPerSymbol,
                    bitsPerNtLabel,
                    cell + 1,
                    nextLength,
                    nextBits,
                    ref substitutions);

                if (outcome == WildcardPruneOutcome.PrunedShadowNoEvidence ||
                    outcome == WildcardPruneOutcome.PrunedShadowCount)
                {
                    return outcome;
                }
            }

            return WildcardPruneOutcome.KeptMatched;
        }

        private static WildcardPruneOutcome CheckGeneratedShadowString(
            RootPruneContext ctx,
            int length,
            ulong bits)
        {
            var evidence = ctx.ShadowEvidence;
            if (evidence == null)
                return WildcardPruneOutcome.KeptMatched;

            if (length <= ctx.FrozenRootDp.MaxLen && RootContains(ctx.FrozenRootDp, ctx.StartIdx, length, bits))
                return WildcardPruneOutcome.KeptMatched;

            if (evidence.HasExactPosEvidence)
            {
                var exactByLength = evidence.ExactPosByLength;
                if (exactByLength == null || (uint)length >= (uint)exactByLength.Length)
                    return WildcardPruneOutcome.PrunedShadowNoEvidence;

                var exactSet = exactByLength[length];
                if (exactSet == null || !exactSet.Contains(bits))
                    return WildcardPruneOutcome.PrunedShadowNoEvidence;
            }

            var maxEvidence = evidence.MaxEvidenceByLength;
            if (maxEvidence == null)
                return WildcardPruneOutcome.KeptMatched;

            if ((uint)length >= (uint)maxEvidence.Length)
                return WildcardPruneOutcome.PrunedShadowCount;

            var generated = GetShadowGeneratedSet(length, maxEvidence.Length);
            if (generated.Add(bits))
            {
                var rootCell = GetRootCell(ctx.FrozenRootDp.Cells, ctx.StartIdx, length);
                int rootCount = rootCell == null ? 0 : rootCell.Count;
                if (rootCount + generated.Count > maxEvidence[length])
                    return WildcardPruneOutcome.PrunedShadowCount;
            }

            return WildcardPruneOutcome.KeptMatched;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryAppendFixedSymbol(
            ulong outputBits,
            int outputLength,
            int symbol,
            int bitsPerSymbol,
            out ulong nextBits)
        {
            int shift = outputLength * bitsPerSymbol;
            if (shift >= MaxBitsPerLong)
            {
                nextBits = 0UL;
                return false;
            }

            nextBits = outputBits | ((ulong)symbol << shift);
            return true;
        }

        private static bool TryEncodeSentenceSlice(
            int[] sentenceIndices,
            int start,
            int length,
            int bitsPerSymbol,
            out ulong bits)
        {
            bits = 0UL;
            if ((long)length * bitsPerSymbol > MaxBitsPerLong)
                return false;

            for (int i = 0; i < length; i++)
                bits |= ((ulong)sentenceIndices[start + i]) << (i * bitsPerSymbol);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractPackedValue(ulong packed, int position, int bitsPerSymbol)
        {
            ulong mask = bitsPerSymbol >= 64 ? ulong.MaxValue : ((1UL << bitsPerSymbol) - 1UL);
            return (int)((packed >> (position * bitsPerSymbol)) & mask);
        }

        private static bool RootContains(FrozenRootStringDp rootDp, int startIdx, int length, ulong bits)
        {
            var rootCell = GetRootCell(rootDp.Cells, startIdx, length);
            if (rootCell == null) return false;
            var fixedBits = rootCell.FixedBits;
            for (int i = 0; i < fixedBits.Length; i++)
            {
                if (fixedBits[i] == bits) return true;
            }

            return false;
        }

        private static HashSet<ulong> GetShadowGeneratedSet(int length, int maxLengthCount)
        {
            if (t_shadowGeneratedByLength == null || t_shadowGeneratedByLength.Length < maxLengthCount)
                t_shadowGeneratedByLength = new HashSet<ulong>[maxLengthCount];
            if (t_shadowGeneratedTouchedLengths == null || t_shadowGeneratedTouchedLengths.Length < maxLengthCount)
                t_shadowGeneratedTouchedLengths = new int[maxLengthCount];

            var set = t_shadowGeneratedByLength[length];
            if (set == null)
            {
                set = new HashSet<ulong>();
                t_shadowGeneratedByLength[length] = set;
            }

            if (set.Count == 0)
                t_shadowGeneratedTouchedLengths[t_shadowGeneratedTouchedCount++] = length;

            return set;
        }

        private static void ResetShadowGeneratedSets()
        {
            if (t_shadowGeneratedByLength == null || t_shadowGeneratedTouchedLengths == null)
            {
                t_shadowGeneratedTouchedCount = 0;
                return;
            }

            for (int i = 0; i < t_shadowGeneratedTouchedCount; i++)
                t_shadowGeneratedByLength[t_shadowGeneratedTouchedLengths[i]]?.Clear();
            t_shadowGeneratedTouchedCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static FrozenRootPatternSet GetRootCell(FrozenRootPatternSet[][] rootDp, int ntIdx, int len)
        {
            if ((uint)ntIdx >= (uint)rootDp.Length) return null;
            var row = rootDp[ntIdx];
            if ((uint)len >= (uint)row.Length) return null;
            return row[len];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PatternSet EnsureOverlayCell(
            PatternSet[][] overlayDp,
            List<int>[] overlayPops,
            int lhs,
            int len,
            ref PatternSet dest)
        {
            if (dest != null) return dest;
            dest = RentPatternSet();
            overlayDp[lhs][len] = dest;
            overlayPops[lhs].Add(len);
            return dest;
        }

        private static void AddRootRuleOverlayCombinations(
            PatternSet[][] overlayDp,
            List<int>[] overlayPops,
            FrozenRootPatternSet[][] rootDp,
            int[][] rootPops,
            int lhs,
            int y,
            int z,
            int len,
            int[] shiftAmounts,
            int[] labelShiftAmounts,
            ref PatternSet dest)
        {
            var overlayPopsY = overlayPops[y];
            for (int pi = 0; pi < overlayPopsY.Count; pi++)
            {
                int lenY = overlayPopsY[pi];
                if (lenY > len) break;
                int lenZ = len - lenY;
                var overlayY = overlayDp[y][lenY];
                var rootZ = GetRootCell(rootDp, z, lenZ);
                if (overlayY == null || rootZ == null) continue;

                EnsureOverlayCell(overlayDp, overlayPops, lhs, len, ref dest);
                AddOverlayRoot(dest, overlayY, rootZ, shiftAmounts[lenY], 0);
            }

            if ((uint)y < (uint)rootPops.Length)
            {
                var rootPopsY = rootPops[y];
                for (int pi = 0; pi < rootPopsY.Length; pi++)
                {
                    int lenY = rootPopsY[pi];
                    if (lenY > len) break;
                    int lenZ = len - lenY;
                    var rootY = GetRootCell(rootDp, y, lenY);
                    var overlayZ = overlayDp[z][lenZ];
                    if (rootY == null || overlayZ == null) continue;

                    EnsureOverlayCell(overlayDp, overlayPops, lhs, len, ref dest);
                    AddRootOverlay(dest, rootY, overlayZ, lenY, shiftAmounts[lenY], labelShiftAmounts == null ? 0 : labelShiftAmounts[lenY], 0);
                }
            }

            for (int pi = 0; pi < overlayPopsY.Count; pi++)
            {
                int lenY = overlayPopsY[pi];
                if (lenY > len) break;
                int lenZ = len - lenY;
                var overlayY = overlayDp[y][lenY];
                var overlayZ = overlayDp[z][lenZ];
                if (overlayY == null || overlayZ == null) continue;

                EnsureOverlayCell(overlayDp, overlayPops, lhs, len, ref dest);
                AddOverlayOverlay(dest, overlayY, overlayZ, lenY, shiftAmounts[lenY], labelShiftAmounts == null ? 0 : labelShiftAmounts[lenY], 0);
            }
        }

        private static void AddDeltaRuleCombinations(
            PatternSet[][] overlayDp,
            List<int>[] overlayPops,
            FrozenRootPatternSet[][] rootDp,
            int[][] rootPops,
            int lhs,
            int y,
            int z,
            int ruleMask,
            int len,
            int[] shiftAmounts,
            int[] labelShiftAmounts,
            ref PatternSet dest)
        {
            if ((uint)y < (uint)rootPops.Length)
            {
                var rootPopsY = rootPops[y];
                for (int pi = 0; pi < rootPopsY.Length; pi++)
                {
                    int lenY = rootPopsY[pi];
                    if (lenY > len) break;
                    int lenZ = len - lenY;
                    var rootY = GetRootCell(rootDp, y, lenY);
                    var rootZ = GetRootCell(rootDp, z, lenZ);
                    if (rootY == null || rootZ == null) continue;

                    EnsureOverlayCell(overlayDp, overlayPops, lhs, len, ref dest);
                    AddRootRoot(dest, rootY, rootZ, shiftAmounts[lenY], ruleMask);
                }
            }

            var overlayPopsY = overlayPops[y];
            for (int pi = 0; pi < overlayPopsY.Count; pi++)
            {
                int lenY = overlayPopsY[pi];
                if (lenY > len) break;
                int lenZ = len - lenY;
                var overlayY = overlayDp[y][lenY];
                var rootZ = GetRootCell(rootDp, z, lenZ);
                if (overlayY == null || rootZ == null) continue;

                EnsureOverlayCell(overlayDp, overlayPops, lhs, len, ref dest);
                AddOverlayRoot(dest, overlayY, rootZ, shiftAmounts[lenY], ruleMask);
            }

            if ((uint)y < (uint)rootPops.Length)
            {
                var rootPopsY = rootPops[y];
                for (int pi = 0; pi < rootPopsY.Length; pi++)
                {
                    int lenY = rootPopsY[pi];
                    if (lenY > len) break;
                    int lenZ = len - lenY;
                    var rootY = GetRootCell(rootDp, y, lenY);
                    var overlayZ = overlayDp[z][lenZ];
                    if (rootY == null || overlayZ == null) continue;

                    EnsureOverlayCell(overlayDp, overlayPops, lhs, len, ref dest);
                    AddRootOverlay(dest, rootY, overlayZ, lenY, shiftAmounts[lenY], labelShiftAmounts == null ? 0 : labelShiftAmounts[lenY], ruleMask);
                }
            }

            for (int pi = 0; pi < overlayPopsY.Count; pi++)
            {
                int lenY = overlayPopsY[pi];
                if (lenY > len) break;
                int lenZ = len - lenY;
                var overlayY = overlayDp[y][lenY];
                var overlayZ = overlayDp[z][lenZ];
                if (overlayY == null || overlayZ == null) continue;

                EnsureOverlayCell(overlayDp, overlayPops, lhs, len, ref dest);
                AddOverlayOverlay(dest, overlayY, overlayZ, lenY, shiftAmounts[lenY], labelShiftAmounts == null ? 0 : labelShiftAmounts[lenY], ruleMask);
            }
        }

        private static void AddRootRoot(
            PatternSet dest,
            FrozenRootPatternSet left,
            FrozenRootPatternSet right,
            int shiftBits,
            int ruleMask)
        {
            var fixedY = left.FixedBits;
            var fixedZ = right.FixedBits;
            for (int iy = 0; iy < fixedY.Length; iy++)
            {
                ulong fixedBitsY = fixedY[iy];
                for (int iz = 0; iz < fixedZ.Length; iz++)
                    dest.Add(fixedBitsY | (fixedZ[iz] << shiftBits), 0UL, 0UL, ruleMask);
            }
        }

        private static void AddOverlayRoot(
            PatternSet dest,
            PatternSet left,
            FrozenRootPatternSet right,
            int shiftBits,
            int ruleMask)
        {
            var fixedY = left.FixedBits;
            var wildcardY = left.WildcardMasks;
            var labelsY = left.WildcardLabels;
            var masksY = left.RuleMasks;
            var fixedZ = right.FixedBits;
            int countY = left.Count;
            for (int iy = 0; iy < countY; iy++)
            {
                ulong fixedBitsY = fixedY[iy];
                ulong wildcardMaskY = wildcardY[iy];
                ulong wildcardLabelsY = labelsY[iy];
                int ruleMaskY = masksY[iy] | ruleMask;
                for (int iz = 0; iz < fixedZ.Length; iz++)
                    dest.Add(fixedBitsY | (fixedZ[iz] << shiftBits), wildcardMaskY, wildcardLabelsY, ruleMaskY);
            }
        }

        private static void AddRootOverlay(
            PatternSet dest,
            FrozenRootPatternSet left,
            PatternSet right,
            int lenY,
            int shiftBits,
            int labelShiftBits,
            int ruleMask)
        {
            var fixedY = left.FixedBits;
            var fixedZ = right.FixedBits;
            var wildcardZ = right.WildcardMasks;
            var labelsZ = right.WildcardLabels;
            var masksZ = right.RuleMasks;
            int countZ = right.Count;
            for (int iy = 0; iy < fixedY.Length; iy++)
            {
                ulong fixedBitsY = fixedY[iy];
                for (int iz = 0; iz < countZ; iz++)
                {
                    dest.Add(
                        fixedBitsY | (fixedZ[iz] << shiftBits),
                        wildcardZ[iz] << lenY,
                        labelShiftBits == 0 ? labelsZ[iz] : (labelsZ[iz] << labelShiftBits),
                        masksZ[iz] | ruleMask);
                }
            }
        }

        private static void AddOverlayOverlay(
            PatternSet dest,
            PatternSet left,
            PatternSet right,
            int lenY,
            int shiftBits,
            int labelShiftBits,
            int ruleMask)
        {
            var fixedY = left.FixedBits;
            var wildcardY = left.WildcardMasks;
            var labelsY = left.WildcardLabels;
            var masksY = left.RuleMasks;
            var fixedZ = right.FixedBits;
            var wildcardZ = right.WildcardMasks;
            var labelsZ = right.WildcardLabels;
            var masksZ = right.RuleMasks;
            int countY = left.Count;
            int countZ = right.Count;
            for (int iy = 0; iy < countY; iy++)
            {
                ulong fixedBitsY = fixedY[iy];
                ulong wildcardMaskY = wildcardY[iy];
                ulong wildcardLabelsY = labelsY[iy];
                int ruleMaskY = masksY[iy] | ruleMask;
                for (int iz = 0; iz < countZ; iz++)
                {
                    dest.Add(
                        fixedBitsY | (fixedZ[iz] << shiftBits),
                        wildcardMaskY | (wildcardZ[iz] << lenY),
                        wildcardLabelsY | (labelShiftBits == 0 ? labelsZ[iz] : (labelsZ[iz] << labelShiftBits)),
                        ruleMaskY | masksZ[iz]);
                }
            }
        }

        private static bool AddRootAsOverlay(PatternSet dest, FrozenRootPatternSet source, int ruleMask)
        {
            bool changed = false;
            var fixedBits = source.FixedBits;
            for (int i = 0; i < fixedBits.Length; i++)
            {
                if (dest.Add(fixedBits[i], 0UL, 0UL, ruleMask))
                    changed = true;
            }
            return changed;
        }

        private static bool AddOverlayAsOverlay(PatternSet dest, PatternSet source, int ruleMask)
        {
            bool changed = false;
            var fixedBits = source.FixedBits;
            var wildcardMasks = source.WildcardMasks;
            var wildcardLabels = source.WildcardLabels;
            var ruleMasks = source.RuleMasks;
            int count = source.Count;
            for (int i = 0; i < count; i++)
            {
                if (dest.Add(fixedBits[i], wildcardMasks[i], wildcardLabels[i], ruleMasks[i] | ruleMask))
                    changed = true;
            }
            return changed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddNonTerminal(
            Dictionary<int, int> ntToIdx,
            Dictionary<int, int> posToIndex,
            int symbol)
        {
            if (posToIndex.ContainsKey(symbol)) return;
            ntToIdx.TryAdd(symbol, ntToIdx.Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddRuleSymbols(
            IReadOnlyList<Rule> rules,
            Dictionary<int, int> posToIndex,
            Dictionary<int, int> ntToIdx)
        {
            if (rules == null) return;

            if (rules is List<Rule> ruleList)
            {
                var span = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < span.Length; i++)
                {
                    ref readonly var rule = ref span[i];
                    AddNonTerminal(ntToIdx, posToIndex, rule.LeftHandSide);
                    var rhs = rule.RightHandSide;
                    for (int j = 0; j < rhs.Length; j++)
                        AddNonTerminal(ntToIdx, posToIndex, rhs[j]);
                }
                return;
            }

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                AddNonTerminal(ntToIdx, posToIndex, rule.LeftHandSide);
                var rhs = rule.RightHandSide;
                for (int j = 0; j < rhs.Length; j++)
                    AddNonTerminal(ntToIdx, posToIndex, rhs[j]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RelaxMinLengths(
            IReadOnlyList<Rule> rules,
            Dictionary<int, int> posToIndex,
            Dictionary<int, int> ntToIdx,
            int[] minLengths)
        {
            if (rules == null) return false;

            bool changed = false;
            if (rules is List<Rule> ruleList)
            {
                var span = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < span.Length; i++)
                {
                    ref readonly var rule = ref span[i];
                    if (!ntToIdx.TryGetValue(rule.LeftHandSide, out int lhsIdx)) continue;

                    int bodyLen = 0;
                    bool finite = true;
                    var rhs = rule.RightHandSide;
                    for (int j = 0; j < rhs.Length; j++)
                    {
                        int symbol = rhs[j];
                        if (posToIndex.ContainsKey(symbol))
                        {
                            bodyLen++;
                        }
                        else if (ntToIdx.TryGetValue(symbol, out int symIdx) && minLengths[symIdx] != int.MaxValue)
                        {
                            bodyLen += minLengths[symIdx];
                        }
                        else
                        {
                            finite = false;
                            break;
                        }
                    }

                    if (finite && bodyLen < minLengths[lhsIdx])
                    {
                        minLengths[lhsIdx] = bodyLen;
                        changed = true;
                    }
                }

                return changed;
            }

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (!ntToIdx.TryGetValue(rule.LeftHandSide, out int lhsIdx)) continue;

                int bodyLen = 0;
                bool finite = true;
                var rhs = rule.RightHandSide;
                for (int j = 0; j < rhs.Length; j++)
                {
                    int symbol = rhs[j];
                    if (posToIndex.ContainsKey(symbol))
                    {
                        bodyLen++;
                    }
                    else if (ntToIdx.TryGetValue(symbol, out int symIdx) && minLengths[symIdx] != int.MaxValue)
                    {
                        bodyLen += minLengths[symIdx];
                    }
                    else
                    {
                        finite = false;
                        break;
                    }
                }

                if (finite && bodyLen < minLengths[lhsIdx])
                {
                    minLengths[lhsIdx] = bodyLen;
                    changed = true;
                }
            }

            return changed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static WildcardPruneOutcome ClassifyRules(
            List<Rule> rules,
            int fixedRuleMask,
            RootPruneContext ctx,
            Dictionary<int, int> ntToIdx,
            List<(int lhs, int mask)> epsilonRules,
            List<(int lhs, ulong posEncoded, int mask)> terminalRules,
            List<(int lhs, int y, int mask)> unitRules,
            List<(int lhs, int y, int z, int mask)> binaryRules)
        {
            if (rules == null) return WildcardPruneOutcome.KeptMatched;

            var span = CollectionsMarshal.AsSpan(rules);
            for (int i = 0; i < span.Length; i++)
            {
                var outcome = ClassifyRule(
                    span[i],
                    fixedRuleMask,
                    ctx,
                    ntToIdx,
                    epsilonRules,
                    terminalRules,
                    unitRules,
                    binaryRules);
                if (outcome != WildcardPruneOutcome.KeptMatched)
                    return outcome;
            }

            return WildcardPruneOutcome.KeptMatched;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static WildcardPruneOutcome ClassifyRule(
            Rule rule,
            int ruleMask,
            RootPruneContext ctx,
            Dictionary<int, int> ntToIdx,
            List<(int lhs, int mask)> epsilonRules,
            List<(int lhs, ulong posEncoded, int mask)> terminalRules,
            List<(int lhs, int y, int mask)> unitRules,
            List<(int lhs, int y, int z, int mask)> binaryRules)
        {
            if (!ntToIdx.TryGetValue(rule.LeftHandSide, out int lhsIdx))
                return WildcardPruneOutcome.KeptMatched;

            var rhs = rule.RightHandSide;
            if (rhs.Length == 0)
            {
                epsilonRules.Add((lhsIdx, ruleMask));
                return WildcardPruneOutcome.KeptMatched;
            }

            if (rhs.Length == 1)
            {
                int y = rhs[0];
                if (ctx.PosToIndex.TryGetValue(y, out int pIdx))
                {
                    terminalRules.Add((lhsIdx, (ulong)pIdx, ruleMask));
                    return WildcardPruneOutcome.KeptMatched;
                }

                if (ntToIdx.TryGetValue(y, out int yIdx))
                {
                    unitRules.Add((lhsIdx, yIdx, ruleMask));
                    return WildcardPruneOutcome.KeptMatched;
                }

                return WildcardPruneOutcome.SkippedUnsupported;
            }

            if (rhs.Length == 2)
            {
                if (ntToIdx.TryGetValue(rhs[0], out int yIdx) &&
                    ntToIdx.TryGetValue(rhs[1], out int zIdx))
                {
                    binaryRules.Add((lhsIdx, yIdx, zIdx, ruleMask));
                    return WildcardPruneOutcome.KeptMatched;
                }

                return WildcardPruneOutcome.SkippedUnsupported;
            }

            return WildcardPruneOutcome.SkippedUnsupported;
        }

        private static void EnsureStringDpCapacity(int ntCount, int maxLen)
        {
            int lenCount = maxLen + 1;
            if (t_stringDp == null || t_stringDpSize < ntCount || t_stringDpMaxLen < lenCount)
            {
                int newSize = Math.Max(ntCount, t_stringDpSize);
                int newMaxLen = Math.Max(lenCount, t_stringDpMaxLen);
                var newDp = new PatternSet[newSize][];
                for (int i = 0; i < newSize; i++)
                    newDp[i] = new PatternSet[newMaxLen];
                t_stringDp = newDp;
                t_stringDpPopulatedLens = new List<int>[newSize];
                t_stringDpSize = newSize;
                t_stringDpMaxLen = newMaxLen;
                return;
            }

            for (int i = 0; i < ntCount; i++)
            {
                if (t_stringDp[i] == null || t_stringDp[i].Length < lenCount)
                    t_stringDp[i] = new PatternSet[Math.Max(lenCount, t_stringDpMaxLen)];
            }
        }

        private static void EnsureRootBuildDpCapacity(int ntCount, int maxLen)
        {
            int lenCount = maxLen + 1;
            if (t_rootBuildDp == null || t_rootBuildDpSize < ntCount || t_rootBuildDpMaxLen < lenCount)
            {
                int newSize = Math.Max(ntCount, t_rootBuildDpSize);
                int newMaxLen = Math.Max(lenCount, t_rootBuildDpMaxLen);
                var newDp = new HashSet<ulong>[newSize][];
                for (int i = 0; i < newSize; i++)
                    newDp[i] = new HashSet<ulong>[newMaxLen];
                t_rootBuildDp = newDp;
                t_rootBuildPopulatedLens = new List<int>[newSize];
                t_rootBuildDpSize = newSize;
                t_rootBuildDpMaxLen = newMaxLen;
                return;
            }

            for (int i = 0; i < ntCount; i++)
            {
                if (t_rootBuildDp[i] == null || t_rootBuildDp[i].Length < lenCount)
                    t_rootBuildDp[i] = new HashSet<ulong>[Math.Max(lenCount, t_rootBuildDpMaxLen)];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AddRootPattern(
            HashSet<ulong>[][] dp,
            List<int>[] populatedLengths,
            int ntIdx,
            int length,
            ulong fixedBits)
        {
            var cell = dp[ntIdx][length];
            if (cell == null)
            {
                cell = RentUlongSet();
                dp[ntIdx][length] = cell;
                populatedLengths[ntIdx].Add(length);
            }
            return cell.Add(fixedBits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AddPattern(
            PatternSet[][] dp,
            List<int>[] populatedLengths,
            int ntIdx,
            int length,
            ulong fixedBits,
            ulong wildcardMask,
            ulong wildcardLabels,
            int ruleMask)
        {
            var cell = dp[ntIdx][length];
            if (cell == null)
            {
                cell = RentPatternSet();
                dp[ntIdx][length] = cell;
                populatedLengths[ntIdx].Add(length);
            }
            return cell.Add(fixedBits, wildcardMask, wildcardLabels, ruleMask);
        }

        private static void CleanupStringDp(
            int ntCount,
            PatternSet[][] dp,
            List<int>[] populatedLengths)
        {
            for (int i = 0; i < ntCount; i++)
            {
                var pops = populatedLengths[i];
                if (pops == null) continue;

                var row = dp[i];
                for (int p = 0; p < pops.Count; p++)
                {
                    int len = pops[p];
                    ReturnPatternSet(row[len]);
                    row[len] = null;
                }

                ReturnIntList(pops);
                populatedLengths[i] = null;
            }
        }

        private static void CleanupRootBuildDp(
            int ntCount,
            HashSet<ulong>[][] dp,
            List<int>[] populatedLengths)
        {
            for (int i = 0; i < ntCount; i++)
            {
                var pops = populatedLengths[i];
                if (pops == null) continue;

                var row = dp[i];
                for (int p = 0; p < pops.Count; p++)
                {
                    int len = pops[p];
                    ReturnUlongSet(row[len]);
                    row[len] = null;
                }

                ReturnIntList(pops);
                populatedLengths[i] = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryEncodeSentenceIndices(
            ReadOnlySpan<int> sentence,
            Dictionary<int, int> posToIndex,
            out int[] indices)
        {
            if (t_nextUnparsedIndices == null || t_nextUnparsedIndices.Length < sentence.Length)
                t_nextUnparsedIndices = new int[sentence.Length];

            indices = t_nextUnparsedIndices;
            for (int i = 0; i < sentence.Length; i++)
            {
                if (!posToIndex.TryGetValue(sentence[i], out int pIdx))
                    return false;
                indices[i] = pIdx;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryEncodeSentenceIndices(
            ReadOnlySpan<int> sentence,
            Dictionary<int, int> posToIndex,
            int bitsPerSymbol,
            out int[] indices,
            out ulong encoded,
            out ulong[] tokenMasks)
        {
            if (t_nextUnparsedIndices == null || t_nextUnparsedIndices.Length < sentence.Length)
                t_nextUnparsedIndices = new int[sentence.Length];

            indices = t_nextUnparsedIndices;
            encoded = 0UL;
            tokenMasks = null;

            bool buildTokenMasks = sentence.Length <= 63;
            if (buildTokenMasks)
            {
                int posCount = posToIndex.Count;
                if (t_sentenceTokenMasks == null || t_sentenceTokenMasks.Length < posCount)
                    t_sentenceTokenMasks = new ulong[posCount];
                if (t_sentenceTokenMaskTouched == null || t_sentenceTokenMaskTouched.Length < posCount)
                    t_sentenceTokenMaskTouched = new int[posCount];

                for (int i = 0; i < t_sentenceTokenMaskTouchedCount; i++)
                    t_sentenceTokenMasks[t_sentenceTokenMaskTouched[i]] = 0UL;
                t_sentenceTokenMaskTouchedCount = 0;
                tokenMasks = t_sentenceTokenMasks;
            }

            for (int i = 0; i < sentence.Length; i++)
            {
                if (!posToIndex.TryGetValue(sentence[i], out int pIdx))
                    return false;
                indices[i] = pIdx;
                encoded |= ((ulong)pIdx) << (i * bitsPerSymbol);
                if (buildTokenMasks)
                {
                    if (tokenMasks[pIdx] == 0UL)
                        t_sentenceTokenMaskTouched[t_sentenceTokenMaskTouchedCount++] = pIdx;
                    tokenMasks[pIdx] |= 1UL << i;
                }
            }

            return true;
        }

        private static bool PatternMatches(
            ulong fixedBits,
            ulong wildcardMask,
            int floorLength,
            int[] sentenceIndices,
            ulong[] tokenMasks,
            int sentenceLength,
            ulong targetEncoded,
            int bitsPerSymbol,
            ILogger logger)
        {
            if (floorLength > sentenceLength) return false;

            if (wildcardMask == 0UL)
            {
                if (floorLength != sentenceLength) return false;
                return targetEncoded == fixedBits;
            }

            ulong floorMask = floorLength == 64 ? ulong.MaxValue : ((1UL << floorLength) - 1UL);
            if ((wildcardMask & floorMask) == floorMask)
                return true;

            if (sentenceLength <= 63)
                return PatternMatchesBitset(fixedBits, wildcardMask, floorLength, tokenMasks, sentenceLength, bitsPerSymbol);

            LogBitsetFallback64(logger);
            return PatternMatchesLinear(fixedBits, wildcardMask, floorLength, sentenceIndices, sentenceLength, bitsPerSymbol);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool PatternMatchesBitset(
            ulong fixedBits,
            ulong wildcardMask,
            int floorLength,
            ulong[] tokenMasks,
            int sentenceLength,
            int bitsPerSymbol)
        {
            ulong validPositions = sentenceLength == 63
                ? ulong.MaxValue
                : ((1UL << (sentenceLength + 1)) - 1UL);

            ulong reach = 1UL;
            ulong valueMask = bitsPerSymbol >= 64 ? ulong.MaxValue : ((1UL << bitsPerSymbol) - 1UL);
            ulong fixedCursor = fixedBits;
            ulong wildcardCursor = wildcardMask;
            bool fullWidthShift = bitsPerSymbol >= 64;
            for (int i = 0; i < floorLength; i++)
            {
                if ((wildcardCursor & 1UL) != 0)
                {
                    ulong low = reach & (0UL - reach);
                    if (low == 0UL) return false;
                    reach = validPositions & ~((low << 1) - 1UL);
                }
                else
                {
                    int expected = (int)(fixedCursor & valueMask);
                    reach = ((reach & tokenMasks[expected]) << 1) & validPositions;
                }

                if (reach == 0UL) return false;
                wildcardCursor >>= 1;
                fixedCursor = fullWidthShift ? 0UL : (fixedCursor >> bitsPerSymbol);
            }

            return (reach & (1UL << sentenceLength)) != 0UL;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LogBitsetFallback64(ILogger logger)
        {
            if (logger == null) return;
            if (Interlocked.Exchange(ref s_loggedBitsetFallback64, 1) == 0)
            {
                logger.LogWarning(
                    "[WILDCARD-PRUNE] Bitset wildcard matcher fallback: next-unparsed length is 64, so wildcard matching uses the linear matcher.");
            }
        }

        private static bool PatternMatchesLinear(
            ulong fixedBits,
            ulong wildcardMask,
            int floorLength,
            int[] sentenceIndices,
            int sentenceLength,
            int bitsPerSymbol)
        {
            if (t_matchReach == null ||
                t_matchNextReach == null ||
                t_matchReach.Length < sentenceLength + 1 ||
                t_matchNextReach.Length < sentenceLength + 1)
            {
                t_matchReach = new int[sentenceLength + 1];
                t_matchNextReach = new int[sentenceLength + 1];
                t_matchStampEpoch = 0;
            }
            else if (t_matchStampEpoch >= int.MaxValue - floorLength - 2)
            {
                Array.Clear(t_matchReach, 0, sentenceLength + 1);
                Array.Clear(t_matchNextReach, 0, sentenceLength + 1);
                t_matchStampEpoch = 0;
            }
            var reach = t_matchReach;
            var nextReach = t_matchNextReach;
            int reachStamp = ++t_matchStampEpoch;
            reach[0] = reachStamp;

            bool fullWidthShift = bitsPerSymbol >= 64;
            ulong valueMask = bitsPerSymbol >= 64 ? ulong.MaxValue : ((1UL << bitsPerSymbol) - 1UL);
            ulong fixedCursor = fixedBits;
            ulong wildcardCursor = wildcardMask;
            for (int i = 0; i < floorLength; i++)
            {
                int nextStamp = ++t_matchStampEpoch;
                bool isWildcard = (wildcardCursor & 1UL) != 0;
                bool any = false;
                if (isWildcard)
                {
                    int firstReach = -1;
                    for (int p = 0; p < sentenceLength; p++)
                    {
                        if (reach[p] == reachStamp)
                        {
                            firstReach = p;
                            break;
                        }
                    }
                    if (firstReach < 0) return false;
                    for (int q = firstReach + 1; q <= sentenceLength; q++)
                        nextReach[q] = nextStamp;
                    any = true;
                }
                else
                {
                    int expected = (int)(fixedCursor & valueMask);
                    for (int p = 0; p < sentenceLength; p++)
                    {
                        if (reach[p] != reachStamp) continue;
                        if (sentenceIndices[p] == expected)
                        {
                            nextReach[p + 1] = nextStamp;
                            any = true;
                        }
                    }
                }

                if (!any) return false;

                (reach, nextReach) = (nextReach, reach);
                reachStamp = nextStamp;
                wildcardCursor >>= 1;
                fixedCursor = fullWidthShift ? 0UL : (fixedCursor >> bitsPerSymbol);
            }

            return reach[sentenceLength] == reachStamp;
        }

    }
}
