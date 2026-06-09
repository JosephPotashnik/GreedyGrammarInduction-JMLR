// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner;
using Microsoft.Extensions.Logging;
using static GreedyGrammarInductionLearner.ArrayCompressor;

namespace GreedyGrammarInduction;
internal class Oracle
{
    private readonly ILogger _logger;
    private readonly Grammar _targetGrammar;
    private readonly Lexicon _learnedLexicon;
    private readonly Lexicon _targetLexicon;
    private readonly int _maxSentenceLength;
    private readonly string[] _targetSequences;
    private readonly List<Rule> _weaklyEquivalentGrammarRules;
    private readonly bool _isStronglyEquivalentSolutionRequired;
    private readonly List<ContextFreeGrammar> _expectedTargetGrammars;
    private readonly OracleComparisonMode _comparisonMode;

    private static readonly char[] s_whitespace = [' ', '\t'];

    public bool TargetHasEpsilon { get; }

    public Oracle(
        ILogger logger,
        string weakEquivalenceTargetFileName,
        Lexicon learnedLexicon,
        int maxSentenceLength,
        string strongEquivalenceTargetFileName,
        OracleComparisonMode comparisonMode,
        string targetLexiconFileName,
        bool generateComparableSequences = true)
    {
        _logger = logger;
        _learnedLexicon = learnedLexicon;
        _targetLexicon = string.IsNullOrEmpty(targetLexiconFileName)
            ? learnedLexicon
            : Lexicon.ReadLexiconFromFile(targetLexiconFileName, updateGrammarPartsOfSpeech: false);
        _maxSentenceLength = maxSentenceLength;
        _comparisonMode = comparisonMode;
        _isStronglyEquivalentSolutionRequired = !string.IsNullOrEmpty(strongEquivalenceTargetFileName);

        var targetGrammarRules = GrammarFileReader.ReadRulesFromFile(weakEquivalenceTargetFileName);
        _logger.LogInformation($"Oracle : Loaded 1 weakly equivalent grammar(s) from {weakEquivalenceTargetFileName}");
        var startId = SymbolTable.Instance.GetId(Grammar.StartSymbol);
        TargetHasEpsilon = targetGrammarRules.Exists(r =>
            r.LeftHandSide == startId && r.RightHandSide.Length == 0);
        _weaklyEquivalentGrammarRules = TargetHasEpsilon
            ? targetGrammarRules.FindAll(r => !(r.LeftHandSide == startId && r.RightHandSide.Length == 0))
            : targetGrammarRules;

        if (generateComparableSequences)
        {
            (var targetSentencesPerLength, _targetGrammar) = GeneratePartsOfSpeechSequences(_weaklyEquivalentGrammarRules, _targetLexicon, maxSentenceLength);
            _targetSequences = GetComparableSequences(targetSentencesPerLength, _targetLexicon);
        }
        else
        {
            _targetGrammar = null;
            _targetSequences = [];
        }

        // Load expected target grammars from file (for strong equivalence testing)
        _expectedTargetGrammars = [];
        if (_isStronglyEquivalentSolutionRequired)
        {
            var targetFilePath = Path.Combine(".", "InputData", "TargetGrammars", strongEquivalenceTargetFileName);
            if (File.Exists(targetFilePath))
            {
                _expectedTargetGrammars = LoadTargetGrammarsFromFile(targetFilePath);
                _logger.LogInformation($"Oracle : Loaded {_expectedTargetGrammars.Count} strongly equivalent grammar(s) from {strongEquivalenceTargetFileName}");
            }
            else
            {
                _logger.LogWarning($"Oracle: Target grammar file not found: {targetFilePath}");
            }
        }
    }

    public bool EnoughEvidenceToLearn(EvidenceTreesShape evidenceShapeVectorCalculator)
    {
        return EvaluateEvidenceSufficiency(evidenceShapeVectorCalculator).EnoughEvidence;
    }

    public (bool EnoughEvidence, double Fitness, string[] MissingSurfaceSentences) EvaluateEvidenceSufficiency(EvidenceTreesShape evidenceShapeVectorCalculator)
    {
        (var fitness, var missingSurfaceSentences) = ComputeFitnessOfTargetGrammar(
            evidenceShapeVectorCalculator,
            _weaklyEquivalentGrammarRules,
            _targetLexicon);

        if (fitness == double.PositiveInfinity)
        {
            return (false, fitness, missingSurfaceSentences);
        }
        else
        {
            _logger.LogInformation($"Oracle: Fitness of target grammar: {fitness}");
            return (true, fitness, missingSurfaceSentences);
        }
    }

    public (List<(ContextFreeGrammar Grammar, int Index)>, bool) EvaluateLearnedGrammars(List<(ContextFreeGrammar Grammar, int Index)> allGlobals)
    {
        var bestGrammars = new List<(ContextFreeGrammar Grammar, int Index)>();
        var weaklyEquivalentIndices = new List<int>();
        var stronglyEquivalentIndices = new List<int>();
        const double F1_TOLERANCE = 0.001;

        // Compute canonical forms of all expected target grammars (for strong equivalence)
        List<int[][]> targetCanonicals = null;
        if (_isStronglyEquivalentSolutionRequired && _expectedTargetGrammars.Count > 0)
        {
            targetCanonicals = new List<int[][]>(_expectedTargetGrammars.Count);
            for (int i = 0; i < _expectedTargetGrammars.Count; i++)
            {
                targetCanonicals.Add(OptimalSolutionsTracker.GetCanonicalAdjacencyArray(_expectedTargetGrammars[i]));
            }
        }

        HashSet<int> encounteredExpectedGrammar = [];

        foreach (var (grammar, index) in allGlobals)
        {
            try
            {
                var (f1score, f1scoremessage) = Statistics(grammar);
                _logger.LogInformation($"Oracle: Global #{index}: {f1scoremessage}");

                if (Math.Abs(f1score - 1.0) < F1_TOLERANCE)
                {
                    bestGrammars.Add((grammar, index));
                    weaklyEquivalentIndices.Add(index);

                    if (_isStronglyEquivalentSolutionRequired && targetCanonicals != null)
                    {
                        var learnedCanonical = OptimalSolutionsTracker.GetCanonicalAdjacencyArray(grammar);
                        for (int t = 0; t < targetCanonicals.Count; t++)
                        {
                            if (CanonicalAdjacencyArrayComparer.Shared.Equals(targetCanonicals[t], learnedCanonical))
                            {
                                _logger.LogInformation($"Oracle: Global #{index} is strongly equivalent to target grammar #{t + 1}");
                                if (encounteredExpectedGrammar.Add(t))
                                {
                                    stronglyEquivalentIndices.Add(index);
                                    break;
                                }

                            }
                        }
                    }
                }
            }
            catch (TooManyEarleyItemsGeneratedException)
            {
                // Skip grammars that generate too many items
            }
        }

        if (weaklyEquivalentIndices.Count > 0)
        {
            _logger.LogInformation($"Oracle: Weakly equivalent grammar indices: {string.Join(", ", weaklyEquivalentIndices)}");
        }
        else
        {
            _logger.LogInformation("Oracle: No weakly equivalent grammars found among the global optimums.");
        }

        if (stronglyEquivalentIndices.Count > 0)
        {
            _logger.LogInformation($"Oracle: Strongly equivalent grammar indices: {string.Join(", ", stronglyEquivalentIndices)}");
        }

        bool success;
        if (_isStronglyEquivalentSolutionRequired)
            success = stronglyEquivalentIndices.Count == _expectedTargetGrammars.Count;
        else
            success = weaklyEquivalentIndices.Count > 0;

        return (bestGrammars, success);
    }

    private (double, string) Statistics(ContextFreeGrammar learnedGrammar)
    {
        var targetSentences = _targetSequences;

        (var learnedSentencesPerLength, var _) = GeneratePartsOfSpeechSequences(learnedGrammar.GetRules(), _learnedLexicon, _maxSentenceLength);
        var learnedSentences = GetComparableSequences(learnedSentencesPerLength, _learnedLexicon);

        var targetSet = new HashSet<string>(targetSentences);
        int truePositives = 0;
        for (int i = 0; i < learnedSentences.Length; i++)
        {
            if (targetSet.Contains(learnedSentences[i]))
            {
                truePositives++;
            }
        }

        var precision = learnedSentences.Length == 0 ? 0 : truePositives / (double)learnedSentences.Length;
        var recall = targetSentences.Length == 0 ? 0 : truePositives / (double)targetSentences.Length;
        var f1Score = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var s = $"Mode: {_comparisonMode}; Precision: {precision:0.0000} Recall: {recall:0.0000} F1-Score: {f1Score:0.0000}\r\n";
        if (targetSentences.Length == 0)
        {
            s += "Oracle target generated no comparable strings. Check target grammar and target lexicon compatibility.\r\n";
        }
        if (learnedSentences.Length == 0)
        {
            s += "Learned grammar generated no comparable strings.\r\n";
        }

        if (Math.Abs(f1Score - 1.0) < 0.001)
        {
            int samplesPerLength = 10;
            int negativeTestsPerLength = 10;
            int minLength = 10;
            int maxLength = 30;
            var tester = new GrammarStressTester(_targetGrammar, _targetLexicon, _learnedLexicon, seed: 42);
            var result = tester.RunStressTest(
                        learnedGrammar,
                        minLength,
                        maxLength,
                        samplesPerLength,
                        negativeTestsPerLength
                    );
            (string report, bool allPassed) = GrammarStressTester.BuildReport(result);
            s += report;
            if (allPassed)
            {
                s += $"^^^^--THIS GRAMMAR PASSED THE STRESS TEST (sampling {samplesPerLength} sentences with per length, length ranges from {minLength} to {maxLength}--^^^^\r\n";
                s += "^^^^--A WEAKLY EQUIVALENT GRAMMAR TO THE TARGET GRAMMAR WAS FOUND--^^^^\r\n";
            }
            else
            {
                s += $" the learned grammar achieved f1_score=1 on all sentences up to length {_maxSentenceLength}, but did not pass the stress test, so not weakly equivalent\r\n";
            }
        }
        return (f1Score, s);
    }

    private static (string[][] Sequences, Grammar Grammar) GeneratePartsOfSpeechSequences(
        List<Rule> grammarRules,
        Lexicon lexicon,
        int maxWords)
    {
        var previousPartsOfSpeech = Grammar.PartsOfSpeech;
        var previousScannedRules = EarleyParser.ScannedRules;

        try
        {
            Grammar.PartsOfSpeech = new HashSet<int>(lexicon.POSWithPossibleWords.Keys);
            EarleyParser.ScannedRules = new ScannedRulesDict(lexicon).ScannedRules;

            var grammar = Grammar.CreateGrammar(grammarRules);
            var generator = new EarleyGenerator(grammar, lexicon, maxWords);
            generator.GenerateSentence();
            return (generator.GetAllSequences(), grammar);
        }
        finally
        {
            Grammar.PartsOfSpeech = previousPartsOfSpeech;
            EarleyParser.ScannedRules = previousScannedRules;
        }
    }

    private string[] GetComparableSequences(string[][] partsOfSpeechSequencesPerLength, Lexicon lexicon)
    {
        return _comparisonMode == OracleComparisonMode.SurfaceTokens
            ? ExpandPartsOfSpeechSequencesToSurfaceTokens(partsOfSpeechSequencesPerLength, lexicon)
            : FlattenSequences(partsOfSpeechSequencesPerLength);
    }

    private static string[] FlattenSequences(string[][] sequencesPerLength)
    {
        var sequences = new HashSet<string>();
        for (int i = 0; i < sequencesPerLength.Length; i++)
        {
            for (int j = 0; j < sequencesPerLength[i].Length; j++)
            {
                sequences.Add(sequencesPerLength[i][j]);
            }
        }

        var result = new string[sequences.Count];
        sequences.CopyTo(result);
        return result;
    }

    private static string[] ExpandPartsOfSpeechSequencesToSurfaceTokens(
        string[][] partsOfSpeechSequencesPerLength,
        Lexicon lexicon)
    {
        var surfaceStrings = new HashSet<string>();

        for (int i = 0; i < partsOfSpeechSequencesPerLength.Length; i++)
        {
            for (int j = 0; j < partsOfSpeechSequencesPerLength[i].Length; j++)
            {
                var partsOfSpeechSequence = partsOfSpeechSequencesPerLength[i][j];
                if (string.IsNullOrWhiteSpace(partsOfSpeechSequence))
                {
                    surfaceStrings.Add(string.Empty);
                    continue;
                }

                var posSymbols = partsOfSpeechSequence.Split(s_whitespace, StringSplitOptions.RemoveEmptyEntries);
                ExpandPartsOfSpeechSequenceToSurfaceTokens(posSymbols, 0, lexicon, new List<string>(posSymbols.Length), surfaceStrings);
            }
        }

        var result = new string[surfaceStrings.Count];
        surfaceStrings.CopyTo(result);
        return result;
    }

    private static void ExpandPartsOfSpeechSequenceToSurfaceTokens(
        string[] posSymbols,
        int index,
        Lexicon lexicon,
        List<string> current,
        HashSet<string> surfaceStrings)
    {
        if (index == posSymbols.Length)
        {
            surfaceStrings.Add(string.Join(" ", current));
            return;
        }

        var posId = SymbolTable.Instance.GetId(posSymbols[index]);
        if (!lexicon.POSWithPossibleWords.TryGetValue(posId, out var words))
        {
            return;
        }

        foreach (var word in words)
        {
            current.Add(word);
            ExpandPartsOfSpeechSequenceToSurfaceTokens(posSymbols, index + 1, lexicon, current, surfaceStrings);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static List<ContextFreeGrammar> LoadTargetGrammarsFromFile(string filePath)
    {
        var targetGrammars = new List<ContextFreeGrammar>();
        var grammarLines = new List<string>();

        using var file = File.OpenText(filePath);
        string line;
        while ((line = file.ReadLine()) != null)
        {
            if (line.Contains("Target"))
            {
                grammarLines.Clear();
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("Count:"))
                        break;
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                        continue;
                    grammarLines.Add(line);
                }

                var rules = new List<Rule>();
                foreach (var ruleLine in grammarLines)
                {
                    var rule = ParseRule(ruleLine);
                    if (rule != null)
                        rules.Add(rule.Value);
                }

                if (rules.Count > 0)
                    targetGrammars.Add(new ContextFreeGrammar(rules));
            }
        }

        return targetGrammars;
    }

    private static Rule? ParseRule(string s)
    {
        var removeArrow = s.Replace("->", "");
        if (s == removeArrow)
            return null;

        var rawParts = removeArrow.Split(s_whitespace, StringSplitOptions.RemoveEmptyEntries);
        var symbolsList = new List<string>(rawParts.Length);
        for (int i = 0; i < rawParts.Length; i++)
        {
            var trimmed = rawParts[i].Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                symbolsList.Add(trimmed);
            }
        }
        var symbols = symbolsList.ToArray();

        var lhs = SymbolTable.Instance.GetId(symbols[0]);
        var rhs = new int[symbols.Length - 1];
        for (int i = 1; i < symbols.Length; i++)
            rhs[i - 1] = SymbolTable.Instance.GetId(symbols[i]);

        return new Rule(lhs, rhs, RuleType.CFGRules);
    }

    private (double Fitness, string[] MissingSurfaceSentences) ComputeFitnessOfTargetGrammar(
           EvidenceTreesShape evidenceShapeVectorCalculator,
           List<Rule> targetGrammarRules,
           Lexicon targetLexicon)
    {
        var previousPartsOfSpeech = Grammar.PartsOfSpeech;
        var partOfSpeechCategories = new HashSet<int>(targetLexicon.POSWithPossibleWords.Keys);

        try
        {
            Grammar.PartsOfSpeech = partOfSpeechCategories;
            var rules = CopyRules(targetGrammarRules);

            var grammar = new ContextFreeGrammar(rules);

            (var unproductiveRules, var grammarShapeVector, var idealizedPartsOfSpeechSequencesByLength) = grammar.GetGrammarShapeIdealized();
            if (unproductiveRules)
            {
                return (double.PositiveInfinity, []);
            }

            var previousParsed = new byte[evidenceShapeVectorCalculator.Sentences.Length];
            List<CompressionRange> previousParsedCompressed = ArrayCompressor.CompressArray(previousParsed);
            (var goodEnoughFitFound, var parsedSentenceRatio, var fitness, var parsedCompressed) = evidenceShapeVectorCalculator.ComputeEvidenceVectorGeneral(grammar, grammarShapeVector, previousParsedCompressed);

            if (goodEnoughFitFound)
            {
                return (fitness, []);
            }

            var missingSurfaceSentences = LogMissingEvidencePartsOfSpeechSequences(
                evidenceShapeVectorCalculator,
                idealizedPartsOfSpeechSequencesByLength,
                targetLexicon);

            return (double.PositiveInfinity, missingSurfaceSentences);
        }
        finally
        {
            Grammar.PartsOfSpeech = previousPartsOfSpeech;
        }
    }

    private string[] LogMissingEvidencePartsOfSpeechSequences(
        EvidenceTreesShape evidenceShapeVectorCalculator,
        HashSet<string>[] targetPartsOfSpeechSequencesByLength,
        Lexicon targetLexicon)
    {
        if (targetPartsOfSpeechSequencesByLength == null)
            return [];

        var maxLength = targetPartsOfSpeechSequencesByLength.Length - 1;
        var evidencePartsOfSpeechSequencesByLength = GetEvidencePartsOfSpeechSequencesByLength(
            evidenceShapeVectorCalculator,
            targetLexicon,
            maxLength);

        var missing = new List<(int Length, string PartsOfSpeechSequence, string ExampleSentence)>();
        for (int length = 0; length < targetPartsOfSpeechSequencesByLength.Length; length++)
        {
            foreach (var targetPartsOfSpeechSequence in targetPartsOfSpeechSequencesByLength[length])
            {
                if (evidencePartsOfSpeechSequencesByLength[length].Contains(targetPartsOfSpeechSequence))
                    continue;

                missing.Add((
                    length,
                    targetPartsOfSpeechSequence,
                    GenerateExampleSentenceForPartsOfSpeechSequence(targetPartsOfSpeechSequence, targetLexicon)));
            }
        }

        if (missing.Count == 0)
        {
            _logger.LogWarning("Oracle: Evidence is insufficient, but no missing target POS sequence was found up to the rule coverage bound.");
            return [];
        }

        missing.Sort((left, right) =>
        {
            int lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.CompareOrdinal(left.PartsOfSpeechSequence, right.PartsOfSpeechSequence);
        });

        _logger.LogWarning(
            "Oracle: Evidence is missing {MissingCount} target POS sequence(s) up to the rule coverage bound.",
            missing.Count);

        for (int i = 0; i < missing.Count; i++)
        {
            var item = missing[i];
            _logger.LogWarning(
                "Oracle: Missing POS sequence: {PartsOfSpeechSequence}; example sentence: {ExampleSentence}",
                FormatSequenceForLog(item.PartsOfSpeechSequence),
                FormatSequenceForLog(item.ExampleSentence));
        }

        var missingSurfaceSentences = new string[missing.Count];
        for (int i = 0; i < missing.Count; i++)
            missingSurfaceSentences[i] = missing[i].ExampleSentence;

        return missingSurfaceSentences;
    }

    private static HashSet<string>[] GetEvidencePartsOfSpeechSequencesByLength(
        EvidenceTreesShape evidenceShapeVectorCalculator,
        Lexicon targetLexicon,
        int maxLength)
    {
        var evidencePartsOfSpeechSequencesByLength = CreatePartsOfSpeechSequenceBuckets(maxLength);

        foreach (var sentenceInfo in evidenceShapeVectorCalculator.Sentences)
        {
            if (sentenceInfo.Length > maxLength)
                continue;

            AddEvidencePartsOfSpeechSequences(
                sentenceInfo.Sentence,
                0,
                targetLexicon,
                new List<string>(sentenceInfo.Length),
                evidencePartsOfSpeechSequencesByLength[sentenceInfo.Length]);
        }

        return evidencePartsOfSpeechSequencesByLength;
    }

    private static HashSet<string>[] CreatePartsOfSpeechSequenceBuckets(int maxLength)
    {
        var buckets = new HashSet<string>[maxLength + 1];
        for (int i = 0; i < buckets.Length; i++)
            buckets[i] = new HashSet<string>();

        return buckets;
    }

    private static void AddEvidencePartsOfSpeechSequences(
        string[] sentence,
        int index,
        Lexicon targetLexicon,
        List<string> currentPartsOfSpeechSequence,
        HashSet<string> partsOfSpeechSequences)
    {
        if (index == sentence.Length)
        {
            partsOfSpeechSequences.Add(string.Join(" ", currentPartsOfSpeechSequence));
            return;
        }

        if (!targetLexicon.WordWithPossiblePOS.TryGetValue(sentence[index], out var possiblePartsOfSpeech))
            return;

        foreach (var partOfSpeech in possiblePartsOfSpeech)
        {
            currentPartsOfSpeechSequence.Add(SymbolTable.Instance.GetSymbol(partOfSpeech));
            AddEvidencePartsOfSpeechSequences(
                sentence,
                index + 1,
                targetLexicon,
                currentPartsOfSpeechSequence,
                partsOfSpeechSequences);
            currentPartsOfSpeechSequence.RemoveAt(currentPartsOfSpeechSequence.Count - 1);
        }
    }

    private static string GenerateExampleSentenceForPartsOfSpeechSequence(
        string partsOfSpeechSequence,
        Lexicon targetLexicon)
    {
        if (string.IsNullOrWhiteSpace(partsOfSpeechSequence))
            return string.Empty;

        var partsOfSpeechSymbols = partsOfSpeechSequence.Split(s_whitespace, StringSplitOptions.RemoveEmptyEntries);
        var words = new string[partsOfSpeechSymbols.Length];
        for (int i = 0; i < partsOfSpeechSymbols.Length; i++)
        {
            var partOfSpeechId = SymbolTable.Instance.GetId(partsOfSpeechSymbols[i]);
            if (!targetLexicon.POSWithPossibleWords.TryGetValue(partOfSpeechId, out var possibleWords) || possibleWords.Count == 0)
                return "<no lexical witness>";

            foreach (var word in possibleWords)
            {
                words[i] = word;
                break;
            }
        }

        return string.Join(" ", words);
    }

    private static string FormatSequenceForLog(string sequence)
    {
        return string.IsNullOrEmpty(sequence) ? "<epsilon>" : sequence;
    }

    private static List<Rule> CopyRules(List<Rule> rules)
    {
        var copy = new List<Rule>(rules.Count);
        for (int i = 0; i < rules.Count; i++)
        {
            copy.Add(new Rule(rules[i]));
        }

        return copy;
    }
}
