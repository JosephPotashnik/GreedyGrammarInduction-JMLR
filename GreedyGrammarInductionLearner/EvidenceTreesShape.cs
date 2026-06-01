// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner.SearchSpace;
using Microsoft.Extensions.Logging;
using static GreedyGrammarInductionLearner.ArrayCompressor;

namespace GreedyGrammarInductionLearner
{
    public class SentenceWithCounts
    {
        public string[] Sentence;
        public int Count;
    }

    public class EvidenceTreesShape
    {
        private readonly int _maxWordsInSentence;
        public readonly int[] SentencesLengthsIndices;
        public int SentencesCount;
        private readonly Lexicon _lexicon;

        private readonly IRecognizer[][] _recognisers;
        private readonly CKYPOSAssignerInstance[][] _posAssigners;
        private readonly bool[][] _accepted;
        private readonly ConcurrentBag<int> _availableLanes;

        // Optimization: Pre-allocated list to reuse in loops and avoid GC pressure.
        private readonly ThreadLocal<List<int>> _reusableToParseList = new(() => new List<int>());
        private readonly ThreadLocal<HashSet<int>> _reusableUsedNTSet = new(() => new HashSet<int>());
        private readonly ThreadLocal<byte[]> _reusableParsedArray;
        private readonly ThreadLocal<HashSet<int>> _reusableStrictlyRHSSet = new(() => new HashSet<int>());

        public EvidenceTreesShape(SentenceWithCounts[] sentences, int maxWordsInSentence, Lexicon dataLexicon, LatticeRuleSpace ruleSpace = null)
        {
            _lexicon = dataLexicon;
            _maxWordsInSentence = maxWordsInSentence;
            SentencesLengthsIndices = new int[maxWordsInSentence + 1];
            Sentences = new SentencesInfo[sentences.Length];
            SentencesCount = 0;
            int currentLength = 0;
            ArrayCompressor.Length = (ushort)Sentences.Length;

            for (var i = 0; i < Sentences.Length; i++)
            {
                var s = sentences[i];
                Sentences[i] = new SentencesInfo
                {
                    Sentence = s.Sentence,
                    Count = s.Count,
                    Length = s.Sentence.Length
                };
                SentencesCount += s.Count;

                if (Sentences[i].Length < currentLength)
                {
                    throw new Exception("Sentences are not ordered by length; please check the input.");
                }

                if (Sentences[i].Length > currentLength)
                {
                    currentLength = Sentences[i].Length;
                    SentencesLengthsIndices[currentLength] = i;
                }
            }

            MaxDistinctByLength = new int[maxWordsInSentence + 1];
            foreach (var s in Sentences)
                MaxDistinctByLength[s.Length]++;

            int numCores = Environment.ProcessorCount;
            _availableLanes = new ConcurrentBag<int>(); // New lock-free bag

            _recognisers = new IRecognizer[numCores][];
            _posAssigners = new CKYPOSAssignerInstance[numCores][];
            _accepted = new bool[numCores][];

            for (var k = 0; k < numCores; k++)
            {
                _recognisers[k] = new IRecognizer[sentences.Length];
                _posAssigners[k] = new CKYPOSAssignerInstance[sentences.Length];
                for (var i = 0; i < sentences.Length; i++)
                {
                    _recognisers[k][i] = new CKYRecognizer(_lexicon, Sentences[i].Sentence);
                    _recognisers[k][i].Reset();
                    if (ruleSpace != null)
                        _posAssigners[k][i] = new CKYPOSAssignerInstance(ruleSpace, _lexicon, Sentences[i].Sentence);
                }

                _accepted[k] = new bool[Sentences.Length];
                _availableLanes.Add(k);
            }

            _reusableParsedArray = new ThreadLocal<byte[]>(() => new byte[Sentences.Length]);

            // Pre-compute POS sequences per sentence (first POS for ambiguous words). Used by the
            // wildcard shape prune to map sentence positions to their canonical POS tag.
            SentencesPOS = new int[Sentences.Length][];
            bool sentencesPosAreExact = true;
            for (int i = 0; i < Sentences.Length; i++)
            {
                var words = Sentences[i].Sentence;
                var posSeq = new int[words.Length];
                bool ok = true;
                for (int j = 0; j < words.Length; j++)
                {
                    var posSet = _lexicon[words[j]];
                    if (posSet == null || posSet.Count == 0) { ok = false; break; }
                    if (posSet.Count != 1) sentencesPosAreExact = false;
                    int firstPos = -1;
                    foreach (int p in posSet) { firstPos = p; break; }
                    posSeq[j] = firstPos;
                }
                if (!ok) sentencesPosAreExact = false;
                SentencesPOS[i] = ok ? posSeq : null;
            }
            SentencesPOSAreExact = sentencesPosAreExact;
            WildcardShadowEvidence = WildcardPrune.BuildShadowEvidenceIndex(
                SentencesPOS,
                MaxDistinctByLength,
                SentencesPOSAreExact);
        }

        /// <summary>
        /// POS sequence per sentence (first POS chosen for ambiguous words). Index aligned with
        /// Sentences. A null entry means a word in that sentence had no POS tag in the lexicon.
        /// </summary>
        public int[][] SentencesPOS { get; }

        /// <summary>
        /// True when every evidence word has exactly one POS tag, so SentencesPOS is a complete
        /// evidence POS language rather than only a canonical representative.
        /// </summary>
        public bool SentencesPOSAreExact { get; }

        /// <summary>
        /// Evidence index used by the wildcard shadow prune. Built once per run and shared across
        /// all root/mapping prune contexts.
        /// </summary>
        public ShadowEvidenceIndex WildcardShadowEvidence { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryCheckoutLane(out int laneIndex)
        {
            // TryTake is lock-free and extremely fast when there is no contention
            return _availableLanes.TryTake(out laneIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReturnLane(int laneIndex)
        {
            // Returning the lane so the next partition can use it
            _availableLanes.Add(laneIndex);
        }

        public SentencesInfo[] Sentences { get; }
        public int EvidenceShapeVectorLength => Sentences.Length;

        /// <summary>
        /// MaxDistinctByLength[L] = number of distinct training sentences of length L.
        /// Upper bound on evidenceShapeVector[L] for any grammar and any POS assignment.
        /// Used for pre-MaxFit underfitting detection.
        /// </summary>
        public int[] MaxDistinctByLength { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (bool, double, double, List<CompressionRange> parsedCompressed) ComputeEvidenceVector(int laneIndex, ContextFreeGrammar currentGrammar, ReadOnlySpan<int> grammarShapeVector, List<CompressionRange> previousParsedCompressed, bool earlyExitOnUnparsed = false)
        {

                double fitness = double.PositiveInfinity;
                Span<int> evidenceShapeVector = stackalloc int[grammarShapeVector.Length];

                int parsedSentencesCount = 0;
                int endIndexInSentences = 0;
                int startIndexInSentences;
                double parsedSentenceRatio = 0.0;

                // Use Array.Clear for compatibility - equivalent to original logic
                Array.Clear(_accepted[laneIndex], 0, _accepted[laneIndex].Length);

                byte[] parsed = _reusableParsedArray.Value;

                if (previousParsedCompressed == null)
                {
                    Array.Clear(parsed, 0, parsed.Length); // Fast block zeroing
                }
                else
                {
                    ArrayCompressor.DecompressInto(previousParsedCompressed, parsed); // Zero allocations!
                }

                // Recognizer does not mutate the grammar — no need to copy
                var currentHypothesis = currentGrammar;

                var toParse = _reusableToParseList.Value;
                toParse.Clear();

                // Cache frequently accessed values to avoid repeated property access
                var sentencesLengthsIndicesSpan = SentencesLengthsIndices.AsSpan();
                var sentencesSpan = Sentences.AsSpan();
                var sentencesLength = sentencesSpan.Length;
                var sentencesLengthsIndicesLength = sentencesLengthsIndicesSpan.Length;

                int initialLengthIndex = 0;
                if (sentencesLength > 0)
                {
                    initialLengthIndex = sentencesSpan[0].Length;
                }

                for (int i = 0; i < initialLengthIndex; i++)
                {
                    if (evidenceShapeVector[i] < grammarShapeVector[i])
                    {
                        // Number of yielded strings falls short of the expected strings in the grammar.
                        return (false, 0, double.PositiveInfinity, null);
                    }
                }

                // Build rule tables once for the grammar, shared across all sentence recognizers.
                // Avoids redundant BuildRuleTables calls (one per sentence → one per grammar).
                CKYSharedTables sharedTables = null;
                if (_recognisers[laneIndex][0] is CKYRecognizer)
                    sharedTables = CKYSharedTables.Build(currentHypothesis);


                int currentLength = initialLengthIndex;
                bool collectEvidence = true;
                //bool atLeastOneSentenceUnparsedPastEvidenceRange = false;

                while (currentLength < sentencesLengthsIndicesLength)
                {
                    toParse.Clear();

                    startIndexInSentences = sentencesLengthsIndicesSpan[currentLength];
                    int endLength = currentLength + 1;

                    while (endLength < sentencesLengthsIndicesLength && sentencesLengthsIndicesSpan[endLength] == 0)
                    {
                        endLength++;
                    }

                    endIndexInSentences = endLength == sentencesLengthsIndicesLength
                        ? sentencesLength
                        : sentencesLengthsIndicesSpan[endLength];

                    // First pass: collect sentences to parse and update evidence for already parsed
                    for (int i = startIndexInSentences; i < endIndexInSentences; i++)
                    {
                        if (parsed[i] != 1) // parsed = 1: unparsed = 0
                        {
                            toParse.Add(i);
                        }
                        else
                        {
                            if (collectEvidence)
                            {
                                evidenceShapeVector[sentencesSpan[i].Length]++;
                            }
                            parsedSentencesCount += sentencesSpan[i].Count;
                        }
                    }


                    // Parse sentences using span-based iteration for better performance
                    var toParseSpan = CollectionsMarshal.AsSpan(toParse);
                    for (int idx = 0; idx < toParseSpan.Length; idx++)
                    {
                        int i = toParseSpan[idx];
                        try
                        {
                            if (sharedTables != null)
                                (_accepted[laneIndex][i], parsed[i]) = ((CKYRecognizer)_recognisers[laneIndex][i]).RecognizeSentence(sharedTables);
                            else
                                (_accepted[laneIndex][i], parsed[i]) = _recognisers[laneIndex][i].RecognizeSentence(currentHypothesis);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"Error parsing sentence {i}: {e.Message}");
                            _accepted[laneIndex][i] = false;
                            parsed[i] = 0; // Mark as unparsed
                        }
                    }

                    // Second pass: update evidence for newly parsed sentences
                    for (int idx = 0; idx < toParseSpan.Length; idx++)
                    {
                        int i = toParseSpan[idx];
                        if (parsed[i] == 1)
                        {
                            if (collectEvidence)
                            {
                                evidenceShapeVector[sentencesSpan[i].Length]++;
                            }
                            parsedSentencesCount += sentencesSpan[i].Count;
                        }
                        else //parsed = 0.
                        {
                            //exit immediately when a sentence is unparsed (Regular mode optimization)
                            if (!collectEvidence && earlyExitOnUnparsed)
                            {
                                parsedSentenceRatio = parsedSentencesCount / (double)SentencesCount;
                                fitness = 1.0;
                                var parsedCompressed1 = ArrayCompressor.CompressArray(parsed);
                                return (true, parsedSentenceRatio, fitness, parsedCompressed1);
                            }
                        }
                    }

                    // Check fitness - early exit optimization
                    if (currentLength < grammarShapeVector.Length)
                    {
                        if (evidenceShapeVector[currentLength] < grammarShapeVector[currentLength])
                        {
                            // Number of yielded strings falls short of the expected strings in the grammar.
                            return (false, 0, double.PositiveInfinity, null);
                        }

                        if (evidenceShapeVector[currentLength] > grammarShapeVector[currentLength])
                        {
                            throw new Exception("contradiction in definition");
                        }
                    }

                    // Advance to next length with non-zero sentences
                    currentLength++;
                    while (currentLength < sentencesLengthsIndicesLength && sentencesLengthsIndicesSpan[currentLength] == 0)
                    {
                        currentLength++;
                    }

                    if (collectEvidence && currentLength >= grammarShapeVector.Length)
                    {
                        collectEvidence = false;
                    }
                }

                // Final calculations
                parsedSentenceRatio = parsedSentencesCount / (double)SentencesCount;
                fitness = 1.0;
                var parsedCompressed = ArrayCompressor.CompressArray(parsed);
                return (true, parsedSentenceRatio, fitness, parsedCompressed);
            
        }

        public (bool, double, double, List<CompressionRange> parsedCompressed) ComputeEvidenceVectorGeneral(Grammar currentGrammar, ReadOnlySpan<int> grammarShapeVector, List<CompressionRange> previousParsedCompressed, bool earlyExitOnUnparsed = false)
        {
            double fitness = double.PositiveInfinity;
            Span<int> evidenceShapeVector = stackalloc int[grammarShapeVector.Length];

            int parsedSentencesCount = 0;
            int endIndexInSentences = 0;
            int startIndexInSentences;
            double parsedSentenceRatio = 0.0;

            byte[] parsed = _reusableParsedArray.Value;

            if (previousParsedCompressed == null)
            {
                Array.Clear(parsed, 0, parsed.Length);
            }
            else
            {
                ArrayCompressor.DecompressInto(previousParsedCompressed, parsed);
            }

            var toParse = _reusableToParseList.Value;
            toParse.Clear();

            var sentencesLengthsIndicesSpan = SentencesLengthsIndices.AsSpan();
            var sentencesSpan = Sentences.AsSpan();
            var sentencesLength = sentencesSpan.Length;
            var sentencesLengthsIndicesLength = sentencesLengthsIndicesSpan.Length;

            int initialLengthIndex = 0;
            if (sentencesLength > 0)
            {
                initialLengthIndex = sentencesSpan[0].Length;
            }

            for (int i = 0; i < initialLengthIndex; i++)
            {
                if (evidenceShapeVector[i] < grammarShapeVector[i])
                {
                    return (false, 0, double.PositiveInfinity, null);
                }
            }

            int currentLength = initialLengthIndex;
            bool collectEvidence = true;

            while (currentLength < sentencesLengthsIndicesLength)
            {
                toParse.Clear();

                startIndexInSentences = sentencesLengthsIndicesSpan[currentLength];
                int endLength = currentLength + 1;

                while (endLength < sentencesLengthsIndicesLength && sentencesLengthsIndicesSpan[endLength] == 0)
                {
                    endLength++;
                }

                endIndexInSentences = endLength == sentencesLengthsIndicesLength
                    ? sentencesLength
                    : sentencesLengthsIndicesSpan[endLength];

                for (int i = startIndexInSentences; i < endIndexInSentences; i++)
                {
                    if (parsed[i] != 1)
                    {
                        toParse.Add(i);
                    }
                    else
                    {
                        if (collectEvidence)
                        {
                            evidenceShapeVector[sentencesSpan[i].Length]++;
                        }
                        parsedSentencesCount += sentencesSpan[i].Count;
                    }
                }

                var toParseSpan = CollectionsMarshal.AsSpan(toParse);
                for (int idx = 0; idx < toParseSpan.Length; idx++)
                {
                    int i = toParseSpan[idx];
                    try
                    {
                        parsed[i] = RecognizeSentenceWithGeneralParser(currentGrammar, sentencesSpan[i].Sentence);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Error parsing sentence {i}: {e.Message}");
                        parsed[i] = 0;
                    }
                }

                for (int idx = 0; idx < toParseSpan.Length; idx++)
                {
                    int i = toParseSpan[idx];
                    if (parsed[i] == 1)
                    {
                        if (collectEvidence)
                        {
                            evidenceShapeVector[sentencesSpan[i].Length]++;
                        }
                        parsedSentencesCount += sentencesSpan[i].Count;
                    }
                    else
                    {
                        if (!collectEvidence && earlyExitOnUnparsed)
                        {
                            parsedSentenceRatio = parsedSentencesCount / (double)SentencesCount;
                            fitness = 1.0;
                            var parsedCompressed1 = ArrayCompressor.CompressArray(parsed);
                            return (true, parsedSentenceRatio, fitness, parsedCompressed1);
                        }
                    }
                }

                if (currentLength < grammarShapeVector.Length)
                {
                    if (evidenceShapeVector[currentLength] < grammarShapeVector[currentLength])
                    {
                        return (false, 0, double.PositiveInfinity, null);
                    }

                    if (evidenceShapeVector[currentLength] > grammarShapeVector[currentLength])
                    {
                        throw new Exception("contradiction in definition");
                    }
                }

                currentLength++;
                while (currentLength < sentencesLengthsIndicesLength && sentencesLengthsIndicesSpan[currentLength] == 0)
                {
                    currentLength++;
                }

                if (collectEvidence && currentLength >= grammarShapeVector.Length)
                {
                    collectEvidence = false;
                }
            }

            parsedSentenceRatio = parsedSentencesCount / (double)SentencesCount;
            fitness = 1.0;
            var parsedCompressed = ArrayCompressor.CompressArray(parsed);
            return (true, parsedSentenceRatio, fitness, parsedCompressed);
        }

        private byte RecognizeSentenceWithGeneralParser(Grammar currentGrammar, string[] sentence)
        {
            var parser = new EarleyParser(null, _lexicon, sentence);
            var (accepted, parsedCode) = parser.ParseSentence(currentGrammar, skipComputingBasicTrees: true);
            return accepted && parsedCode == 1 ? (byte)1 : (byte)0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal (List<ushort[]>, List<int>) MaxFitNextUnparsedSentences(
            int laneIndex,
            List<Rule> coreRules,
            List<List<CompressionRange>> listOfPreviousParsed,
            List<HashSet<Rule>> previousPosMappings,
            LatticeRuleSpace ruleSpace,
            RootPOSConstraint rootPOSConstraint,
            PreviousMappingSelection previousMappingSelection = default,
            HashSet<int> coreStrictlyRHSNonterminals = null,
            BannedPOSChain bannedPOS = null)
        {
                // Handle empty case more efficiently
                if (previousPosMappings.Count == 0)
                {
                    previousPosMappings = [new HashSet<Rule>()];
                    var emptyParsed = new byte[Sentences.Length];
                    var compressionOFEmptyParsed = ArrayCompressor.CompressArray(emptyParsed);
                    listOfPreviousParsed = [compressionOFEmptyParsed];
                }

                // Structural rules don't change across previousPosMappings iterations —
                // build shared CKY POS tables once for the entire call.
                var posSharedTables = _posAssigners[laneIndex][0] != null
                    ? CKYPOSSharedTables.Build(coreRules, ruleSpace)
                    : null;

                int previousMappingCount = previousPosMappings.Count;
                if (TryGetSingleSelectedMappingIndex(previousMappingSelection, previousMappingCount, out int singleMappingIndex))
                {
                    var singleResult = GetMaxFitInputSentenceIndex(
                        laneIndex,
                        previousPosMappings[singleMappingIndex],
                        listOfPreviousParsed[singleMappingIndex],
                        coreStrictlyRHSNonterminals,
                        ruleSpace,
                        rootPOSConstraint,
                        bannedPOS,
                        posSharedTables);

                    if (previousMappingCount == 1 && singleMappingIndex == 0)
                    {
                        return (singleResult, null);
                    }

                    var singleIndices = new List<int>(singleResult.Count);
                    for (int i = 0; i < singleResult.Count; i++)
                        singleIndices.Add(singleMappingIndex);

                    return (singleResult, singleIndices);
                }

                // Pre-allocate with more accurate capacity estimation
                int selectedMappingCount = EstimateSelectedMappingCount(previousMappingSelection, previousMappingCount);
                var POSAssignmentsOptions = new List<ushort[]>(selectedMappingCount * 2);
                var PreviousPOSMappingIndices = new List<int>(selectedMappingCount * 2);

                void AddResultsForMapping(int mappingIndex)
                {
                    var res = GetMaxFitInputSentenceIndex(
                        laneIndex,
                        previousPosMappings[mappingIndex],
                        listOfPreviousParsed[mappingIndex],
                        coreStrictlyRHSNonterminals,
                        ruleSpace,
                        rootPOSConstraint,
                        bannedPOS,
                        posSharedTables);

                    // Directly populate the return lists, bypassing the redundant 'sets' list
                    foreach (var assignment in res)
                    {
                        POSAssignmentsOptions.Add(assignment);
                        PreviousPOSMappingIndices.Add(mappingIndex);
                    }
                }

                if (previousMappingSelection.UsesIndices)
                {
                    var indices = previousMappingSelection.Indices;
                    for (int i = 0; i < indices.Length; i++)
                        AddResultsForMapping(indices[i]);
                }
                else if (previousMappingSelection.UsesMask)
                {
                    ulong bits = previousMappingSelection.Mask & GetMappingMask(previousMappingCount);
                    while (bits != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(bits);
                        AddResultsForMapping(bit);
                        bits &= bits - 1;
                    }
                }
                else
                {
                    for (int i = 0; i < previousMappingCount; i++)
                        AddResultsForMapping(i);
                }

                return (POSAssignmentsOptions, PreviousPOSMappingIndices);
            
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GetMappingMask(int mappingCount)
        {
            if (mappingCount >= 64)
                return ulong.MaxValue;

            return mappingCount <= 0 ? 0UL : (1UL << mappingCount) - 1UL;
        }

        private static int EstimateSelectedMappingCount(
            PreviousMappingSelection selection,
            int mappingCount)
        {
            if (mappingCount <= 0)
                return 0;

            if (selection.UsesIndices)
                return selection.Indices.Length;

            if (selection.UsesMask)
                return BitOperations.PopCount(selection.Mask & GetMappingMask(mappingCount));

            return mappingCount;
        }

        private static bool TryGetSingleSelectedMappingIndex(
            PreviousMappingSelection selection,
            int mappingCount,
            out int mappingIndex)
        {
            mappingIndex = 0;
            if (mappingCount <= 0)
                return false;

            if (selection.UsesIndices)
            {
                var indices = selection.Indices;
                if (indices.Length == 1)
                {
                    mappingIndex = indices[0];
                    return true;
                }

                return false;
            }

            if (selection.UsesMask)
            {
                ulong bits = selection.Mask & GetMappingMask(mappingCount);
                if (BitOperations.PopCount(bits) == 1)
                {
                    mappingIndex = BitOperations.TrailingZeroCount(bits);
                    return true;
                }

                return false;
            }

            return mappingCount == 1;
        }

        private List<ushort[]> GetMaxFitInputSentenceIndex(
            int parserIndex,
            HashSet<Rule> previousPosMapping,
            List<CompressionRange> previousParsedCompressed,
            HashSet<int> coreStrictlyRHSNonterminals,
            LatticeRuleSpace ruleSpace,
            RootPOSConstraint rootPOSConstraint,
            BannedPOSChain bannedPOS = null,
            CKYPOSSharedTables posSharedTables = null)
        {
            var newPOSAssignmentsList = new List<ushort[]>();
            // Pre-allocate with estimated capacity to avoid resizing
            var usedNTInPOSAssignments = _reusableUsedNTSet.Value;
            usedNTInPOSAssignments.Clear();
            foreach (var r in previousPosMapping)
            {
                usedNTInPOSAssignments.Add(r.LeftHandSide);
            }

            var strictlyRHSNonterminals = _reusableStrictlyRHSSet.Value;
            strictlyRHSNonterminals.Clear();
            if (coreStrictlyRHSNonterminals != null)
            {
                foreach (var nt in coreStrictlyRHSNonterminals)
                {
                    if (!usedNTInPOSAssignments.Contains(nt))
                        strictlyRHSNonterminals.Add(nt);
                }
            }

            int sentencesLength = Sentences.Length;

            // Build residual banned masks once per call. CKY bitsets contain only new POS
            // assignments, so residual masks are enough to reject banned full mappings.
            POSBitset basePrevBitset = default;
            List<ulong[]> residualBannedMasks = null;
            if (bannedPOS != null)
            {
                basePrevBitset = POSBitset.FromPOSMappings(previousPosMapping, ruleSpace.POSAssignmentDictionary, ruleSpace.POSAssignmentRules.Length);
                residualBannedMasks = BuildResidualBannedMasks(
                    bannedPOS,
                    in basePrevBitset,
                    ruleSpace.POSAssignmentRules.Length,
                    out bool previousMappingAlreadyBanned);
                if (previousMappingAlreadyBanned)
                {
                    return newPOSAssignmentsList;
                }
            }

            int maxFitInputSentenceIndex = ArrayCompressor.FindFirstValue(previousParsedCompressed, 0, sentencesLength);
            if (maxFitInputSentenceIndex < 0)
            {
                return newPOSAssignmentsList;
            }

            // CKY POS-assignment semiring: leaf data is pre-initialized per instance;
            // shared grammar tables are built once per MaxFitNextUnparsedSentences call.
            CKYPOSAssignerInstance ckyAssigner = null;
            List<int> ckyAssignments = null;
            if (posSharedTables != null)
            {
                ckyAssigner = _posAssigners[parserIndex][maxFitInputSentenceIndex];
                ckyAssignments = ckyAssigner
                    .GetPOSAssignmentBitsets(
                        posSharedTables,
                        previousPosMapping,
                        residualBannedMasks,
                        rootPOSConstraint.ForbiddenNewAssignmentLhs,
                        strictlyRHSNonterminals,
                        rootPOSConstraint.ForbiddenNewAssignmentMask);
            }

            if (ckyAssignments == null || ckyAssignments.Count == 0)
            {
                return newPOSAssignmentsList;
            }

            for (int choiceSetIndex = 0; choiceSetIndex < ckyAssignments.Count; choiceSetIndex++)
            {
                newPOSAssignmentsList.Add(ckyAssigner.MaterializePOSAssignmentIndices(ckyAssignments[choiceSetIndex]));
            }

            return newPOSAssignmentsList;
        }

        private static List<ulong[]> BuildResidualBannedMasks(
            BannedPOSChain bannedPOS,
            in POSBitset previousMapping,
            int posCapacity,
            out bool previousMappingAlreadyBanned)
        {
            previousMappingAlreadyBanned = false;
            int wordCount = (posCapacity + 63) >> 6;
            List<ulong[]> residuals = null;

            for (var node = bannedPOS; node != null; node = node.Next)
            {
                if (node.Banned.IsSubsetOf(in previousMapping))
                {
                    previousMappingAlreadyBanned = true;
                    return null;
                }

                var residual = new ulong[wordCount];
                node.Banned.CopyResidualExcluding(in previousMapping, residual);

                if (IsCoveredByExistingResidual(residuals, residual))
                    continue;

                RemoveResidualsCoveredBy(residuals, residual);
                residuals ??= new List<ulong[]>();
                residuals.Add(residual);
            }

            return residuals;
        }

        private static bool IsCoveredByExistingResidual(List<ulong[]> residuals, ulong[] candidate)
        {
            if (residuals == null)
                return false;

            for (int i = 0; i < residuals.Count; i++)
            {
                if (IsMaskSubset(residuals[i], candidate))
                    return true;
            }

            return false;
        }

        private static void RemoveResidualsCoveredBy(List<ulong[]> residuals, ulong[] candidate)
        {
            if (residuals == null)
                return;

            for (int i = residuals.Count - 1; i >= 0; i--)
            {
                if (IsMaskSubset(candidate, residuals[i]))
                    residuals.RemoveAt(i);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsMaskSubset(ulong[] subset, ulong[] candidate)
        {
            for (int i = 0; i < subset.Length; i++)
            {
                if ((subset[i] & candidate[i]) != subset[i])
                    return false;
            }

            return true;
        }
    }
}
