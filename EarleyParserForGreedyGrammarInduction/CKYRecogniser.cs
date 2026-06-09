// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EarleyParserForGreedyGrammarInduction;

/// <summary>
/// Pre-computed rule tables that depend only on the grammar, not on the sentence.
/// Built once per grammar and shared across all CKYRecognizer instances.
/// </summary>
public class CKYSharedTables
{
    // Narrow path (≤64 NTs)
    public ulong[] BinaryParents;
    public ulong[] UnaryClosure;
    public int MaxNT;

    // Wide path (>64 NTs)
    public ulong[][] WideBinaryRuleTable;
    public ulong[][] WideUnaryClosure;
    public int WordsPerBitset;

    public bool IsWide;

    public static CKYSharedTables Build(Grammar g)
    {
        int ntCount = Grammar.s_symbolTable.Count;
        var tables = new CKYSharedTables();
        tables.MaxNT = ntCount;

        if (ntCount > 64)
        {
            tables.IsWide = true;
            tables.WordsPerBitset = (ntCount + 63) >> 6;
            BuildWide(g, ntCount, tables);
        }
        else
        {
            tables.IsWide = false;
            BuildNarrow(g, ntCount, tables);
        }

        return tables;
    }

    private static void BuildNarrow(Grammar g, int ntCount, CKYSharedTables tables)
    {
        tables.UnaryClosure = new ulong[ntCount];
        tables.BinaryParents = new ulong[ntCount * ntCount];

        foreach (var rule in g.GetRules())
        {
            int A = rule.LeftHandSide;

            if (rule.RightHandSide.Length == 2)
            {
                int B = rule.RightHandSide[0];
                int C = rule.RightHandSide[1];
                tables.BinaryParents[B * ntCount + C] |= 1UL << A;
            }
            else if (rule.RightHandSide.Length == 1)
            {
                int B = rule.RightHandSide[0];
                tables.UnaryClosure[B] |= 1UL << A;
            }
        }

        // Compute transitive unary closure
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int B = 0; B < ntCount; B++)
            {
                ulong parents = tables.UnaryClosure[B];
                if (parents == 0) continue;

                ulong expanded = parents;
                while (parents != 0)
                {
                    int p = BitOperations.TrailingZeroCount(parents);
                    parents &= parents - 1;
                    expanded |= tables.UnaryClosure[p];
                }

                if (expanded != tables.UnaryClosure[B])
                {
                    tables.UnaryClosure[B] = expanded;
                    changed = true;
                }
            }
        }
    }

    private static void BuildWide(Grammar g, int ntCount, CKYSharedTables tables)
    {
        int w = tables.WordsPerBitset;
        int tableSize = ntCount * ntCount;

        tables.WideBinaryRuleTable = new ulong[tableSize][];
        for (int idx = 0; idx < tableSize; idx++)
            tables.WideBinaryRuleTable[idx] = new ulong[w];

        tables.WideUnaryClosure = new ulong[ntCount][];
        for (int idx = 0; idx < ntCount; idx++)
            tables.WideUnaryClosure[idx] = new ulong[w];

        foreach (var rule in g.GetRules())
        {
            int A = rule.LeftHandSide;

            if (rule.RightHandSide.Length == 2)
            {
                int B = rule.RightHandSide[0];
                int C = rule.RightHandSide[1];
                tables.WideBinaryRuleTable[B * ntCount + C][A >> 6] |= 1UL << (A & 63);
            }
            else if (rule.RightHandSide.Length == 1)
            {
                int B = rule.RightHandSide[0];
                tables.WideUnaryClosure[B][A >> 6] |= 1UL << (A & 63);
            }
        }

        // Transitive closure
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int B = 0; B < ntCount; B++)
            {
                for (int wi = 0; wi < w; wi++)
                {
                    ulong parents = tables.WideUnaryClosure[B][wi];
                    while (parents != 0)
                    {
                        int parent = (wi << 6) + BitOperations.TrailingZeroCount(parents);
                        parents &= parents - 1;

                        for (int wj = 0; wj < w; wj++)
                        {
                            ulong newBits = tables.WideUnaryClosure[parent][wj] & ~tables.WideUnaryClosure[B][wj];
                            if (newBits != 0)
                            {
                                tables.WideUnaryClosure[B][wj] |= tables.WideUnaryClosure[parent][wj];
                                changed = true;
                            }
                        }
                    }
                }
            }
        }
    }
}

public class CKYRecognizer : IRecognizer
{
    private readonly Lexicon _lexicon;
    private readonly string[] _sentence;
    private readonly int _n;

    private readonly ulong[] _chart;
    private readonly ulong[] _wordPOS;

    private bool _wordPOSResolved;

    private int _maxNT;

    private readonly int _startSymbolId;

    // unaryClosure[B] = bitset of all ancestors reachable from B via unary rules (transitive).
    // Includes both POS->NT and NT->NT unary rules so that compacted grammars
    // (where POS can appear on binary rule RHS) work correctly.
    private ulong[] _unaryClosure;

    // Flat lookup: _binaryParents[B * _maxNT + C] = parent bitset for rule A -> B C.
    // At ≤64 NTs this is at most 64×64×8 = 32KB, fits in L1 cache.
    private ulong[] _binaryParents;

    public CKYRecognizer(Lexicon lexicon, string[] sentence)
    {
        _lexicon = lexicon;
        _sentence = sentence;
        _n = sentence.Length;

        _startSymbolId = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);

        _chart = new ulong[_n * _n];
        _wordPOS = new ulong[_n];
    }

    // Resets cached word-POS resolution (call when lexicon/sentence changes).
    // Rule tables are rebuilt on every RecognizeSentence call since the grammar changes.
    public void Reset()
    {
        _wordPOSResolved = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (bool accepted, byte parsedCode) RecognizeSentence(Grammar g)
    {
        int ntCount = Grammar.s_symbolTable.Count;

        if (ntCount > 64)
            return RecognizeSentenceWide(g, ntCount);

        BuildRuleTables(g, ntCount);
        ResolveWordPOS();

        bool recognized = Recognize();

        return (recognized, recognized ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Recognizes using pre-built shared rule tables (avoids redundant BuildRuleTables per sentence).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (bool accepted, byte parsedCode) RecognizeSentence(CKYSharedTables tables)
    {
        _maxNT = tables.MaxNT;
        _unaryClosure = tables.UnaryClosure;
        _binaryParents = tables.BinaryParents;

        if (tables.IsWide)
        {
            _wordsPerBitset = tables.WordsPerBitset;
            _wideUnaryClosure = tables.WideUnaryClosure;
            _wideBinaryRuleTable = tables.WideBinaryRuleTable;
            ResolveWordPOSWide();
            bool recWide = RecognizeWide();
            return (recWide, recWide ? (byte)1 : (byte)0);
        }

        ResolveWordPOS();
        bool recognized = Recognize();
        return (recognized, recognized ? (byte)1 : (byte)0);
    }

    private void ResolveWordPOS()
    {
        if (_wordPOSResolved) return;

        for (int i = 0; i < _n; i++)
        {
            if (_lexicon.WordWithPossiblePOS.TryGetValue(_sentence[i], out var poses))
            {
                ulong bits = 0;

                foreach (var pos in poses)
                    bits |= 1UL << pos;

                _wordPOS[i] = bits;
            }
            else
            {
                _wordPOS[i] = 0;
            }
        }

        _wordPOSResolved = true;
    }

    private void BuildRuleTables(Grammar g, int ntCount)
    {
        _maxNT = ntCount;

        if (_unaryClosure == null || _unaryClosure.Length < ntCount)
            _unaryClosure = new ulong[ntCount];
        else
            Array.Clear(_unaryClosure, 0, ntCount);

        int tableSize = ntCount * ntCount;

        if (_binaryParents == null || _binaryParents.Length < tableSize)
            _binaryParents = new ulong[tableSize];
        else
            Array.Clear(_binaryParents, 0, tableSize);

        foreach (var rule in g.GetRules())
        {
            int A = rule.LeftHandSide;

            if (rule.RightHandSide.Length == 2)
            {
                int B = rule.RightHandSide[0];
                int C = rule.RightHandSide[1];

                _binaryParents[B * ntCount + C] |= 1UL << A;
            }
            else if (rule.RightHandSide.Length == 1)
            {
                int B = rule.RightHandSide[0];
                _unaryClosure[B] |= 1UL << A;
            }
        }

        // Compute transitive unary closure.
        // Typically POS->NT (depth-1) and START->X (depth-1), so converges in 1-2 passes.
        bool changed = true;

        while (changed)
        {
            changed = false;

            for (int B = 0; B < ntCount; B++)
            {
                ulong parents = _unaryClosure[B];
                if (parents == 0) continue;

                ulong expanded = parents;

                while (parents != 0)
                {
                    int p = BitOperations.TrailingZeroCount(parents);
                    parents &= parents - 1;
                    expanded |= _unaryClosure[p];
                }

                if (expanded != _unaryClosure[B])
                {
                    _unaryClosure[B] = expanded;
                    changed = true;
                }
            }
        }
    }

    // Single-pass: _unaryClosure is transitively closed by BuildRuleTables,
    // so each entry already contains all ancestors. No re-scanning needed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ApplyUnaryClosure(ulong cell)
    {
        ulong tmp = cell;

        while (tmp != 0)
        {
            int sym = BitOperations.TrailingZeroCount(tmp);
            tmp &= tmp - 1;
            cell |= _unaryClosure[sym];
        }

        return cell;
    }

    private bool Recognize()
    {
        int n = _n;
        int maxNT = _maxNT;
        ulong[] binaryParents = _binaryParents;

        Array.Clear(_chart, 0, n * n);

        ulong startBit = 1UL << _startSymbolId;

        // Diagonal: seed with POS bits, then close under unary rules.
        // POS bits stay in the cell so compacted grammars (POS on binary RHS) work.
        for (int i = 0; i < n; i++)
        {
            ulong posBits = _wordPOS[i];

            if (posBits == 0)
                return false;

            _chart[i * n + i] = ApplyUnaryClosure(posBits);
        }

        // CKY
        for (int span = 2; span <= n; span++)
        {
            for (int i = 0; i <= n - span; i++)
            {
                int j = i + span - 1;

                ulong cell = 0;

                for (int k = i; k < j; k++)
                {
                    ulong left = _chart[i * n + k];
                    ulong right = _chart[(k + 1) * n + j];

                    if (left == 0 || right == 0)
                        continue;

                    while (left != 0)
                    {
                        int B = BitOperations.TrailingZeroCount(left);
                        left &= left - 1;

                        int baseIdx = B * maxNT;
                        ulong rightBits = right;

                        while (rightBits != 0)
                        {
                            int C = BitOperations.TrailingZeroCount(rightBits);
                            rightBits &= rightBits - 1;

                            cell |= binaryParents[baseIdx + C];
                        }
                    }
                }

                if (cell != 0)
                    cell = ApplyUnaryClosure(cell);

                _chart[i * n + j] = cell;
            }
        }

        return (_chart[n - 1] & startBit) != 0;
    }

    // ---- Wide path: ntCount > 64, uses ulong[] multi-word bitsets ----

    private ulong[][] _wideBinaryRuleTable;
    private ulong[][] _wideUnaryClosure;
    private ulong[][] _wideChart;
    private ulong[][] _wideWordPOS;
    private int _wordsPerBitset;

    private (bool, byte) RecognizeSentenceWide(Grammar g, int ntCount)
    {
        _wordsPerBitset = (ntCount + 63) >> 6;
        BuildRuleTablesWide(g, ntCount);
        ResolveWordPOSWide();

        bool recognized = RecognizeWide();
        return (recognized, recognized ? (byte)1 : (byte)0);
    }

    private void ResolveWordPOSWide()
    {
        int w = _wordsPerBitset;

        if (_wideWordPOS == null || _wideWordPOS.Length < _n)
            _wideWordPOS = new ulong[_n][];

        for (int i = 0; i < _n; i++)
        {
            if (_wideWordPOS[i] == null || _wideWordPOS[i].Length < w)
                _wideWordPOS[i] = new ulong[w];
            else
                Array.Clear(_wideWordPOS[i], 0, w);

            if (_lexicon.WordWithPossiblePOS.TryGetValue(_sentence[i], out var poses))
            {
                foreach (var pos in poses)
                    _wideWordPOS[i][pos >> 6] |= 1UL << (pos & 63);
            }
        }
    }

    private void BuildRuleTablesWide(Grammar g, int ntCount)
    {
        _maxNT = ntCount;
        int w = _wordsPerBitset;
        int tableSize = ntCount * ntCount;

        if (_wideBinaryRuleTable == null || _wideBinaryRuleTable.Length < tableSize)
            _wideBinaryRuleTable = new ulong[tableSize][];

        for (int idx = 0; idx < tableSize; idx++)
        {
            if (_wideBinaryRuleTable[idx] == null || _wideBinaryRuleTable[idx].Length < w)
                _wideBinaryRuleTable[idx] = new ulong[w];
            else
                Array.Clear(_wideBinaryRuleTable[idx], 0, w);
        }

        if (_wideUnaryClosure == null || _wideUnaryClosure.Length < ntCount)
            _wideUnaryClosure = new ulong[ntCount][];

        for (int idx = 0; idx < ntCount; idx++)
        {
            if (_wideUnaryClosure[idx] == null || _wideUnaryClosure[idx].Length < w)
                _wideUnaryClosure[idx] = new ulong[w];
            else
                Array.Clear(_wideUnaryClosure[idx], 0, w);
        }

        foreach (var rule in g.GetRules())
        {
            int A = rule.LeftHandSide;

            if (rule.RightHandSide.Length == 2)
            {
                int B = rule.RightHandSide[0];
                int C = rule.RightHandSide[1];
                _wideBinaryRuleTable[B * ntCount + C][A >> 6] |= 1UL << (A & 63);
            }
            else if (rule.RightHandSide.Length == 1)
            {
                int B = rule.RightHandSide[0];
                _wideUnaryClosure[B][A >> 6] |= 1UL << (A & 63);
            }
        }

        // Transitive closure
        bool changed = true;

        while (changed)
        {
            changed = false;

            for (int B = 0; B < ntCount; B++)
            {
                for (int wi = 0; wi < w; wi++)
                {
                    ulong parents = _wideUnaryClosure[B][wi];

                    while (parents != 0)
                    {
                        int parent = (wi << 6) + BitOperations.TrailingZeroCount(parents);
                        parents &= parents - 1;

                        for (int wj = 0; wj < w; wj++)
                        {
                            ulong newBits = _wideUnaryClosure[parent][wj] & ~_wideUnaryClosure[B][wj];

                            if (newBits != 0)
                            {
                                _wideUnaryClosure[B][wj] |= _wideUnaryClosure[parent][wj];
                                changed = true;
                            }
                        }
                    }
                }
            }
        }
    }

    private void WideApplyUnaryClosure(ulong[] cell)
    {
        int w = _wordsPerBitset;
        bool added = true;

        while (added)
        {
            added = false;

            for (int wi = 0; wi < w; wi++)
            {
                ulong bits = cell[wi];

                while (bits != 0)
                {
                    int sym = (wi << 6) + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    for (int wj = 0; wj < w; wj++)
                    {
                        ulong newBits = _wideUnaryClosure[sym][wj] & ~cell[wj];

                        if (newBits != 0)
                        {
                            cell[wj] |= _wideUnaryClosure[sym][wj];
                            added = true;
                        }
                    }
                }
            }
        }
    }

    private bool RecognizeWide()
    {
        int n = _n;
        int w = _wordsPerBitset;
        int chartSize = n * n;

        if (_wideChart == null || _wideChart.Length < chartSize)
            _wideChart = new ulong[chartSize][];

        for (int idx = 0; idx < chartSize; idx++)
        {
            if (_wideChart[idx] == null || _wideChart[idx].Length < w)
                _wideChart[idx] = new ulong[w];
            else
                Array.Clear(_wideChart[idx], 0, w);
        }

        // Base case
        for (int i = 0; i < n; i++)
        {
            bool hasAny = false;

            for (int wi = 0; wi < w; wi++)
            {
                _wideChart[i * n + i][wi] = _wideWordPOS[i][wi];
                if (_wideWordPOS[i][wi] != 0) hasAny = true;
            }

            if (!hasAny) return false;
            WideApplyUnaryClosure(_wideChart[i * n + i]);
        }

        // Fill spans
        for (int span = 2; span <= n; span++)
        {
            for (int i = 0; i <= n - span; i++)
            {
                int j = i + span - 1;
                var cell = _wideChart[i * n + j];

                for (int k = i; k < j; k++)
                {
                    var left = _wideChart[i * n + k];
                    var right = _wideChart[(k + 1) * n + j];

                    for (int wi = 0; wi < w; wi++)
                    {
                        ulong leftBits = left[wi];

                        while (leftBits != 0)
                        {
                            int B = (wi << 6) + BitOperations.TrailingZeroCount(leftBits);
                            leftBits &= leftBits - 1;
                            int baseIdx = B * _maxNT;

                            for (int wj = 0; wj < w; wj++)
                            {
                                ulong rightBits = right[wj];

                                while (rightBits != 0)
                                {
                                    int C = (wj << 6) + BitOperations.TrailingZeroCount(rightBits);
                                    rightBits &= rightBits - 1;

                                    var ruleResult = _wideBinaryRuleTable[baseIdx + C];

                                    for (int wk = 0; wk < w; wk++)
                                        cell[wk] |= ruleResult[wk];
                                }
                            }
                        }
                    }
                }

                bool cellNonEmpty = false;

                for (int wi = 0; wi < w; wi++)
                    if (cell[wi] != 0) { cellNonEmpty = true; break; }

                if (cellNonEmpty)
                    WideApplyUnaryClosure(cell);
            }
        }

        int startWord = _startSymbolId >> 6;
        ulong startBit = 1UL << (_startSymbolId & 63);
        return (_wideChart[n - 1][startWord] & startBit) != 0;
    }
}
