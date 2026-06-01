// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EarleyParserForGreedyGrammarInduction;

// ── CKYPOSSharedTables ───────────────────────────────────────────────────────
public class CKYPOSSharedTables
{
    public readonly (int A, int B, int C)[] BinaryRules;
    public readonly int[] EntryNTs;

    private CKYPOSSharedTables((int A, int B, int C)[] binaryRules, int[] entryNTs)
    {
        BinaryRules = binaryRules;
        EntryNTs = entryNTs;
    }

    public static CKYPOSSharedTables Build(IEnumerable<Rule> structuralRules, LatticeRuleSpace ruleSpace)
    {
        int numNTs = ruleSpace.NonTerminalIds.Length;
        int startId = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);

        var ntIndex = new Dictionary<int, int>(numNTs);
        for (int i = 0; i < numNTs; i++)
            ntIndex[ruleSpace.NonTerminalIds[i]] = i;

        var binary = new List<(int A, int B, int C)>();
        var entryNTs = new List<int>();

        foreach (var rule in structuralRules)
        {
            if (rule.RightHandSide.Length == 2)
            {
                if (ntIndex.TryGetValue(rule.LeftHandSide, out int A) &&
                    ntIndex.TryGetValue(rule.RightHandSide[0], out int B) &&
                    ntIndex.TryGetValue(rule.RightHandSide[1], out int C))
                    binary.Add((A, B, C));
            }
            else if (rule.RightHandSide.Length == 1 && rule.LeftHandSide == startId)
            {
                if (ntIndex.TryGetValue(rule.RightHandSide[0], out int Xi))
                    entryNTs.Add(Xi);
            }
        }

        return new CKYPOSSharedTables(binary.ToArray(), entryNTs.ToArray());
    }
}

// ── CKYPOSAssignerInstance ───────────────────────────────────────────────────
public class CKYPOSAssignerInstance
{
    private readonly LatticeRuleSpace _ruleSpace;
    private readonly int _n;
    private readonly int _numNTs;
    private readonly int _words;

    private readonly ulong[] _ntMasksFlat;
    private readonly (int word, ulong mask)[][] _leafEntries;
    private readonly HashSet<int>[] _wordPOSSet;
    private readonly Dictionary<int, int> _ntIndexById;

    // ── Single Flat Arena ──────────────────────────────────────────────────────
    private ulong[] _arena;
    private ulong[] _arenaAssignedNtMasks;
    private int _arenaPtr;
    private readonly ArenaComparer _comparer;

    // ── Sparse Chart System ───────────────────────────────────────────────────
    private readonly ChartCell[] _chart;
    private readonly List<int> _touchedCells;

    // ── Per-call scratch promoted to instance fields (instance is lane-scoped) ─
    // GetPOSAssignments runs single-threaded for the lifetime of this instance during
    // any one parallel iteration, so reusing these across calls is safe.
    private int[] _fixedPOSForNT;
    private readonly ulong[] _forbiddenNewAssignmentMask;
    private readonly ulong[] _requiredCoverageMasksFlat;
    private readonly HashSet<int> _seen;

    private class ChartCell
    {
        public readonly List<int> Items = new();
        public readonly HashSet<int> Set;
        public ChartCell(ArenaComparer comparer) { Set = new HashSet<int>(comparer); }
        public void Clear() { Items.Clear(); Set.Clear(); }
    }

    public CKYPOSAssignerInstance(LatticeRuleSpace ruleSpace, Lexicon lexicon, string[] sentence)
    {
        _ruleSpace = ruleSpace;
        _n = sentence.Length;
        _numNTs = ruleSpace.NonTerminalIds.Length;
        _words = (ruleSpace.POSAssignmentRules.Length + 63) >> 6;
        _comparer = new ArenaComparer(this);
        _ntIndexById = new Dictionary<int, int>(_numNTs);
        for (int nt = 0; nt < _numNTs; nt++)
            _ntIndexById[ruleSpace.NonTerminalIds[nt]] = nt;

        // Initial arena allocation - rented once, kept for the lifetime of the instance
        _arena = ArrayPool<ulong>.Shared.Rent(8192);
        _arenaAssignedNtMasks = new ulong[_arena.Length];

        int numPOS = ruleSpace.POSes.Count;

        var ntPosToWord = new Dictionary<(int ntId, int posId), (int word, ulong mask)>(
            ruleSpace.POSAssignmentRules.Length);
        foreach (var kvp in ruleSpace.POSAssignmentDictionary)
        {
            int idx = kvp.Value;
            ntPosToWord[(kvp.Key.LeftHandSide, kvp.Key.RightHandSide[0])] =
                (idx >> 6, 1UL << (idx & 63));
        }

        _ntMasksFlat = new ulong[_numNTs * _words];
        for (int nt = 0; nt < _numNTs; nt++)
        {
            int startIdx = nt * numPOS;
            for (int pos = 0; pos < numPOS; pos++)
            {
                int bitIdx = startIdx + pos;
                _ntMasksFlat[nt * _words + (bitIdx >> 6)] |= 1UL << (bitIdx & 63);
            }
        }

        _wordPOSSet = new HashSet<int>[_n];
        _leafEntries = new (int word, ulong mask)[_n * _numNTs][];

        for (int p = 0; p < _n; p++)
        {
            lexicon.WordWithPossiblePOS.TryGetValue(sentence[p], out var posIds);
            _wordPOSSet[p] = posIds ?? new HashSet<int>();

            for (int nt = 0; nt < _numNTs; nt++)
            {
                if (posIds == null || posIds.Count == 0)
                {
                    _leafEntries[p * _numNTs + nt] = Array.Empty<(int, ulong)>();
                    continue;
                }

                int ntId = ruleSpace.NonTerminalIds[nt];
                var list = new List<(int word, ulong mask)>(posIds.Count);
                foreach (var posId in posIds)
                    if (ntPosToWord.TryGetValue((ntId, posId), out var loc))
                        list.Add(loc);

                _leafEntries[p * _numNTs + nt] = list.ToArray();
            }
        }

        _chart = new ChartCell[_n * _n * _numNTs];
        for (int idx = 0; idx < _chart.Length; idx++)
            _chart[idx] = new ChartCell(_comparer);

        _touchedCells = new List<int>(1024);

        _fixedPOSForNT = new int[_numNTs];
        _forbiddenNewAssignmentMask = new ulong[_words];
        _requiredCoverageMasksFlat = new ulong[Math.Max(1, _numNTs * _words)];
        _seen = new HashSet<int>(_comparer);
    }

    public List<HashSet<Rule>> GetPOSAssignments(
        CKYPOSSharedTables tables,
        HashSet<Rule> fixedPOSMapping,
        IReadOnlyList<ulong[]> residualBannedMasks = null,
        HashSet<int> forbiddenNewAssignmentLhs = null,
        HashSet<int> requiredAssignmentLhs = null,
        ulong[] forbiddenNewAssignmentMask = null)
    {
        var pointers = GetPOSAssignmentBitsets(
            tables,
            fixedPOSMapping,
            residualBannedMasks,
            forbiddenNewAssignmentLhs,
            requiredAssignmentLhs,
            forbiddenNewAssignmentMask);

        var results = new List<HashSet<Rule>>(pointers.Count);
        for (int i = 0; i < pointers.Count; i++)
        {
            results.Add(MaterializePOSAssignment(pointers[i]));
        }

        return results;
    }

    // Returned pointers reference this instance's arena and remain valid only until the next
    // GetPOSAssignments/GetPOSAssignmentBitsets call on this instance.
    public List<int> GetPOSAssignmentBitsets(
        CKYPOSSharedTables tables,
        HashSet<Rule> fixedPOSMapping,
        IReadOnlyList<ulong[]> residualBannedMasks = null,
        HashSet<int> forbiddenNewAssignmentLhs = null,
        HashSet<int> requiredAssignmentLhs = null,
        ulong[] forbiddenNewAssignmentMask = null)
    {
        _arenaPtr = 0; // Reset arena for this search (NO ArrayPool return here)

        var fixedPOSForNT = _fixedPOSForNT;
        Array.Fill(fixedPOSForNT, -1);
        foreach (var r in fixedPOSMapping)
        {
            if (_ntIndexById.TryGetValue(r.LeftHandSide, out int nt))
                fixedPOSForNT[nt] = r.RightHandSide[0];
        }

        int words = _words;
        bool useAssignedNtMaskShortcut = _numNTs <= 64;
        var activeForbiddenMask = forbiddenNewAssignmentMask;
        if (activeForbiddenMask == null)
        {
            PrepareForbiddenMask(forbiddenNewAssignmentLhs, _forbiddenNewAssignmentMask);
            activeForbiddenMask = _forbiddenNewAssignmentMask;
        }

        int requiredCoverageCount = PrepareRequiredCoverageMasks(requiredAssignmentLhs, fixedPOSForNT, forbiddenNewAssignmentLhs);
        if (requiredCoverageCount < 0)
            return new List<int>();

        // ── Leaf cells ───────────────────────────────────────────────────────────
        for (int p = 0; p < _n; p++)
        {
            int cellBase = (p * _n + p) * _numNTs;
            for (int nt = 0; nt < _numNTs; nt++)
            {
                int fixedPos = fixedPOSForNT[nt];
                var destCell = _chart[cellBase + nt];

                if (fixedPos >= 0)
                {
                    if (!_wordPOSSet[p].Contains(fixedPos)) continue;

                    int ptr = Allocate(words);
                    _arena.AsSpan(ptr, words).Clear();
                    _arenaAssignedNtMasks[ptr] = 0UL;

                    if (destCell.Set.Add(ptr))
                    {
                        if (destCell.Items.Count == 0) _touchedCells.Add(cellBase + nt);
                        destCell.Items.Add(ptr);
                    }
                    else _arenaPtr -= words; // Backtrack allocation
                }
                else
                {
                    foreach (var (w, mask) in _leafEntries[p * _numNTs + nt])
                    {
                        int ptr = Allocate(words);
                        var candidate = _arena.AsSpan(ptr, words);
                        candidate.Clear();
                        candidate[w] = mask;
                        _arenaAssignedNtMasks[ptr] = useAssignedNtMaskShortcut ? 1UL << nt : 0UL;

                        if (FailsMonotoneFilters(candidate, activeForbiddenMask, residualBannedMasks, words))
                        {
                            _arenaPtr -= words;
                            continue;
                        }

                        if (destCell.Set.Add(ptr))
                        {
                            if (destCell.Items.Count == 0) _touchedCells.Add(cellBase + nt);
                            destCell.Items.Add(ptr);
                        }
                        else _arenaPtr -= words;
                    }
                }
            }
        }

        // ── CKY fill ──────────────────────────────────────────────────────────────
        var binaryRules = tables.BinaryRules;
        var ntMasksFlat = _ntMasksFlat;
        int numNTs = _numNTs;
        int n = _n;

        for (int span = 2; span <= n; span++)
        {
            for (int i = 0; i <= n - span; i++)
            {
                int j = i + span - 1;
                int cellBase = (i * n + j) * numNTs;

                for (int ri = 0; ri < binaryRules.Length; ri++)
                {
                    var (A, B, C) = binaryRules[ri];
                    var destCell = _chart[cellBase + A];

                    var leftBase = (i * n) * numNTs + B;
                    var rightBase = n * numNTs + C; // Offset for (k+1)*n

                    for (int k = i; k < j; k++)
                    {
                        var leftCell = _chart[(i * n + k) * numNTs + B];
                        var rightCell = _chart[((k + 1) * n + j) * numNTs + C];

                        if (leftCell.Items.Count == 0 || rightCell.Items.Count == 0) continue;

                        foreach (int leftIdx in leftCell.Items)
                        {
                            ReadOnlySpan<ulong> leftSpan = _arena.AsSpan(leftIdx, words);
                            foreach (int rightIdx in rightCell.Items)
                            {
                                int destIdx = Allocate(words);
                                Span<ulong> destSpan = _arena.AsSpan(destIdx, words);
                                ReadOnlySpan<ulong> rightSpan = _arena.AsSpan(rightIdx, words);
                                ulong leftAssignedNtMask = _arenaAssignedNtMasks[leftIdx];
                                ulong rightAssignedNtMask = _arenaAssignedNtMasks[rightIdx];
                                ulong destAssignedNtMask = leftAssignedNtMask | rightAssignedNtMask;

                                // Maximized SIMD: The JIT will vectorize this OR loop
                                for (int w = 0; w < words; w++)
                                    destSpan[w] = leftSpan[w] | rightSpan[w];

                                if ((!useAssignedNtMaskShortcut || (leftAssignedNtMask & rightAssignedNtMask) != 0) &&
                                    !IsCompatible(destSpan, numNTs, ntMasksFlat, words))
                                {
                                    _arenaPtr -= words; // Reject allocation
                                    continue;
                                }

                                _arenaAssignedNtMasks[destIdx] = destAssignedNtMask;

                                if (FailsMonotoneFilters(destSpan, activeForbiddenMask, residualBannedMasks, words))
                                {
                                    _arenaPtr -= words;
                                    continue;
                                }

                                if (destCell.Set.Add(destIdx))
                                {
                                    if (destCell.Items.Count == 0) _touchedCells.Add(cellBase + A);
                                    destCell.Items.Add(destIdx);
                                }
                                else _arenaPtr -= words;
                            }
                        }
                    }
                }
            }
        }

        // ── Collect results ──────────────────────────────────────────────────────
        int fullSpanBase = (n - 1) * numNTs;
        var results = new List<int>();
        var seen = _seen;
        seen.Clear();

        foreach (int xi in tables.EntryNTs)
            foreach (int ptr in _chart[fullSpanBase + xi].Items)
                if (seen.Add(ptr) &&
                    HasRequiredCoverage(_arena.AsSpan(ptr, words), _requiredCoverageMasksFlat, requiredCoverageCount, words))
                    results.Add(ptr);

        // ── Clean up ─────────────────────────────────────────────────────────────
        foreach (int cellIdx in _touchedCells)
            _chart[cellIdx].Clear();
        _touchedCells.Clear();

        return results;
    }

    private void PrepareForbiddenMask(HashSet<int> forbiddenNewAssignmentLhs, ulong[] destination)
    {
        Array.Clear(destination, 0, destination.Length);
        if (forbiddenNewAssignmentLhs == null || forbiddenNewAssignmentLhs.Count == 0)
            return;

        foreach (int ntId in forbiddenNewAssignmentLhs)
        {
            if (!_ntIndexById.TryGetValue(ntId, out int nt))
                continue;

            int maskOffset = nt * _words;
            for (int w = 0; w < _words; w++)
                destination[w] |= _ntMasksFlat[maskOffset + w];
        }
    }

    private int PrepareRequiredCoverageMasks(
        HashSet<int> requiredAssignmentLhs,
        int[] fixedPOSForNT,
        HashSet<int> forbiddenNewAssignmentLhs)
    {
        if (requiredAssignmentLhs == null || requiredAssignmentLhs.Count == 0)
            return 0;

        int requiredCount = 0;
        foreach (int ntId in requiredAssignmentLhs)
        {
            if (!_ntIndexById.TryGetValue(ntId, out int nt))
                return -1;

            // Contradiction short-circuit: an NT cannot be both required to receive a POS
            // assignment AND forbidden from receiving one. Without this, CKY would still build
            // the entire chart and fail HasRequiredCoverage at result time — wasted work.
            // The fixed-POS branch below handles the legitimate case where the previous mapping
            // already pins this NT; only NTs without a fixed POS are subject to the contradiction.
            if (fixedPOSForNT[nt] < 0 &&
                forbiddenNewAssignmentLhs != null &&
                forbiddenNewAssignmentLhs.Contains(ntId))
            {
                return -1;
            }

            // Fixed POS mappings are not stored in the arena bitset, but they already
            // satisfy the requirement for this nonterminal.
            if (fixedPOSForNT[nt] >= 0)
                continue;

            int destinationOffset = requiredCount * _words;
            int sourceOffset = nt * _words;
            for (int w = 0; w < _words; w++)
                _requiredCoverageMasksFlat[destinationOffset + w] = _ntMasksFlat[sourceOffset + w];

            requiredCount++;
        }

        return requiredCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FailsMonotoneFilters(
        ReadOnlySpan<ulong> candidate,
        ulong[] forbiddenMask,
        IReadOnlyList<ulong[]> residualBannedMasks,
        int words)
    {
        for (int w = 0; w < words; w++)
        {
            if ((candidate[w] & forbiddenMask[w]) != 0)
                return true;
        }

        if (residualBannedMasks == null)
            return false;

        for (int i = 0; i < residualBannedMasks.Count; i++)
        {
            var residual = residualBannedMasks[i];
            bool residualIsSubset = true;
            for (int w = 0; w < words; w++)
            {
                if ((candidate[w] & residual[w]) != residual[w])
                {
                    residualIsSubset = false;
                    break;
                }
            }

            if (residualIsSubset)
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasRequiredCoverage(
        ReadOnlySpan<ulong> candidate,
        ulong[] requiredMasksFlat,
        int requiredCount,
        int words)
    {
        for (int i = 0; i < requiredCount; i++)
        {
            int offset = i * words;
            bool foundAssignmentForRequiredNT = false;
            for (int w = 0; w < words; w++)
            {
                if ((candidate[w] & requiredMasksFlat[offset + w]) != 0)
                {
                    foundAssignmentForRequiredNT = true;
                    break;
                }
            }

            if (!foundAssignmentForRequiredNT)
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Allocate(int words)
    {
        int ptr = _arenaPtr;
        _arenaPtr += words;
        if (_arenaPtr > _arena.Length)
        {
            ExpandArena(_arenaPtr);
        }
        return ptr;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExpandArena(int required)
    {
        int newSize = Math.Max(_arena.Length * 2, required);
        ulong[] newArena = ArrayPool<ulong>.Shared.Rent(newSize);
        _arena.AsSpan(0, _arenaPtr - _words).CopyTo(newArena);
        ArrayPool<ulong>.Shared.Return(_arena);
        _arena = newArena;

        var newAssignedNtMasks = new ulong[_arena.Length];
        Array.Copy(
            _arenaAssignedNtMasks,
            newAssignedNtMasks,
            Math.Min(_arenaAssignedNtMasks.Length, newAssignedNtMasks.Length));
        _arenaAssignedNtMasks = newAssignedNtMasks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HashSet<Rule> MaterializePOSAssignment(int ptr)
    {
        var result = new HashSet<Rule>(CountRulesInBitset(ptr));
        ReadOnlySpan<ulong> span = _arena.AsSpan(ptr, _words);
        for (int w = 0; w < _words; w++)
        {
            ulong word = span[w];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                result.Add(_ruleSpace.POSAssignmentRules[w * 64 + bit]);
            }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort[] MaterializePOSAssignmentIndices(int ptr)
    {
        var result = new ushort[CountRulesInBitset(ptr)];
        int resultIndex = 0;
        ReadOnlySpan<ulong> span = _arena.AsSpan(ptr, _words);
        for (int w = 0; w < _words; w++)
        {
            ulong word = span[w];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                result[resultIndex++] = (ushort)(w * 64 + bit);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CountRulesInBitset(int ptr)
    {
        int count = 0;
        ReadOnlySpan<ulong> span = _arena.AsSpan(ptr, _words);
        for (int w = 0; w < _words; w++)
        {
            count += BitOperations.PopCount(span[w]);
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCompatible(ReadOnlySpan<ulong> bs, int numNTs, ulong[] ntMasksFlat, int words)
    {
        int maskIdx = 0;
        for (int nt = 0; nt < numNTs; nt++)
        {
            int count = 0;
            for (int w = 0; w < words; w++)
            {
                count += BitOperations.PopCount(bs[w] & ntMasksFlat[maskIdx++]);
            }
            if (count > 1) return false;
        }
        return true;
    }

    private sealed class ArenaComparer : IEqualityComparer<int>
    {
        private readonly CKYPOSAssignerInstance _parent;
        internal ArenaComparer(CKYPOSAssignerInstance parent) => _parent = parent;

        public bool Equals(int x, int y)
        {
            int words = _parent._words;
            var arena = _parent._arena;
            // SIMD-vectorized comparison
            return arena.AsSpan(x, words).SequenceEqual(arena.AsSpan(y, words));
        }

        public int GetHashCode(int obj)
        {
            int words = _parent._words;
            var span = _parent._arena.AsSpan(obj, words);
            var hc = new HashCode();
            for (int i = 0; i < span.Length; i++)
                hc.Add(span[i]);
            return hc.ToHashCode();
        }
    }

    ~CKYPOSAssignerInstance()
    {
        if (_arena != null) ArrayPool<ulong>.Shared.Return(_arena);
    }
}
