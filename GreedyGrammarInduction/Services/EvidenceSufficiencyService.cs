// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.IO;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner;
using Microsoft.Extensions.Logging;

namespace GreedyGrammarInduction.Services
{
    public class EvidenceSufficiencyRequest
    {
        public SentenceWithCounts[] Sentences { get; set; }
        public Lexicon TargetLexicon { get; set; }
        public string TargetGrammarFileName { get; set; }
    }

    public class EvidenceSufficiencyResponse
    {
        public bool EnoughEvidenceToLearn { get; set; }
        public double Fitness { get; set; }
        public int NumberOfSentences { get; set; }
        public int MaxSentenceLength { get; set; }
        public int MinSentenceLength { get; set; }
        public bool EvidenceContainsEmptyString { get; set; }
        public bool TargetHasEpsilon { get; set; }
        public string[] MissingSurfaceSentences { get; set; } = [];
        public string Message { get; set; }
    }

    public class EvidenceSufficiencyService
    {
        private readonly ILogger _logger;

        public EvidenceSufficiencyService(ILogger logger)
        {
            _logger = logger;
        }

        public EvidenceSufficiencyResponse CheckEnoughEvidence(EvidenceSufficiencyRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Sentences);
            ArgumentNullException.ThrowIfNull(request.TargetLexicon);

            if (request.Sentences.Length == 0)
            {
                throw new ArgumentException("At least one sample sentence is required.", nameof(request.Sentences));
            }

            if (string.IsNullOrEmpty(request.TargetGrammarFileName))
            {
                throw new ArgumentException("TargetGrammarFileName is required.", nameof(request.TargetGrammarFileName));
            }

            ValidateSamplesAgainstLexicon(request.Sentences, request.TargetLexicon);

            Grammar.PartsOfSpeech = new HashSet<int>(request.TargetLexicon.POSWithPossibleWords.Keys);
            EarleyParser.ScannedRules = new ScannedRulesDict(request.TargetLexicon).ScannedRules;

            var sentences = CopyAndSortByLength(request.Sentences);
            var minSentenceLength = sentences[0].Sentence.Length;
            var maxSentenceLength = sentences[^1].Sentence.Length;

            var oracle = new Oracle(
                _logger,
                request.TargetGrammarFileName,
                request.TargetLexicon,
                maxSentenceLength,
                strongEquivalenceTargetFileName: null,
                OracleComparisonMode.PartsOfSpeech,
                targetLexiconFileName: null,
                generateComparableSequences: false);

            bool evidenceContainsEmptyString = sentences.Length > 0 && sentences[0].Sentence.Length == 0;
            if (evidenceContainsEmptyString)
            {
                var nonEpsilonSentences = new SentenceWithCounts[sentences.Length - 1];
                Array.Copy(sentences, 1, nonEpsilonSentences, 0, sentences.Length - 1);
                sentences = nonEpsilonSentences;
            }

            if (sentences.Length == 0)
            {
                throw new InvalidDataException("At least one non-empty sample sentence is required for evidence sufficiency checking.");
            }

            var evidenceShapeVectorCalculator = new EvidenceTreesShape(sentences, maxSentenceLength, request.TargetLexicon);
            var (enoughEvidence, fitness, missingSurfaceSentences) = oracle.EvaluateEvidenceSufficiency(evidenceShapeVectorCalculator);

            return new EvidenceSufficiencyResponse
            {
                EnoughEvidenceToLearn = enoughEvidence,
                Fitness = fitness,
                NumberOfSentences = sentences.Length,
                MaxSentenceLength = maxSentenceLength,
                MinSentenceLength = minSentenceLength,
                EvidenceContainsEmptyString = evidenceContainsEmptyString,
                TargetHasEpsilon = oracle.TargetHasEpsilon,
                MissingSurfaceSentences = missingSurfaceSentences,
                Message = enoughEvidence
                    ? "There is enough evidence to learn the target grammar."
                    : "There is not enough evidence to conclusively learn the target grammar."
            };
        }

        private static SentenceWithCounts[] CopyAndSortByLength(SentenceWithCounts[] sentences)
        {
            var copy = new SentenceWithCounts[sentences.Length];
            for (int i = 0; i < sentences.Length; i++)
            {
                copy[i] = new SentenceWithCounts
                {
                    Sentence = sentences[i].Sentence,
                    Count = sentences[i].Count
                };
            }

            Array.Sort(copy, (a, b) => a.Sentence.Length.CompareTo(b.Sentence.Length));
            return copy;
        }

        private static void ValidateSamplesAgainstLexicon(SentenceWithCounts[] sentences, Lexicon lexicon)
        {
            var unknownWords = new HashSet<string>();
            for (int i = 0; i < sentences.Length; i++)
            {
                var sentence = sentences[i].Sentence;
                for (int j = 0; j < sentence.Length; j++)
                {
                    var word = sentence[j];
                    if (!lexicon.WordWithPossiblePOS.ContainsKey(word))
                    {
                        unknownWords.Add(word);
                    }
                }
            }

            if (unknownWords.Count == 0)
            {
                return;
            }

            var words = new string[unknownWords.Count];
            unknownWords.CopyTo(words);
            Array.Sort(words, StringComparer.Ordinal);
            throw new FormatException($"Sample token(s) missing from target lexicon: {string.Join(", ", words)}");
        }
    }
}
