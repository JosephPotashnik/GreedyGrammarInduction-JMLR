// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Text;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// Stress tests grammars by:
    /// 1. Generating random sentences from target grammar and verifying learned grammar accepts them
    /// 2. Generating sentences NOT in target grammar and verifying learned grammar rejects them
    /// </summary>
    public class GrammarStressTester
    {
        private readonly Grammar _targetGrammar;
        private readonly Lexicon _targetLexicon;
        private readonly Lexicon _learnedLexicon;
        private readonly RandomSentenceGenerator _generator;

        public GrammarStressTester(Grammar targetGrammar, Lexicon lexicon, int? seed = null)
            : this(targetGrammar, lexicon, lexicon, seed)
        {
        }

        public GrammarStressTester(Grammar targetGrammar, Lexicon targetLexicon, Lexicon learnedLexicon, int? seed = null)
        {
            _targetGrammar = targetGrammar;
            _targetLexicon = targetLexicon;
            _learnedLexicon = learnedLexicon;
            _generator = new RandomSentenceGenerator(targetGrammar, targetLexicon, seed);
        }

        /// <summary>
        /// Result of a stress test run.
        /// </summary>
        public class StressTestResult
        {
            public int TotalPositiveTests { get; set; }
            public int PositiveTestsPassed { get; set; }
            public int PositiveTestsFailed { get; set; }
            public List<string[]> FailedPositiveSentences { get; set; } = new List<string[]>();

            public int TotalNegativeTests { get; set; }
            public int NegativeTestsPassed { get; set; }
            public int NegativeTestsFailed { get; set; }
            public List<string[]> FailedNegativeSentences { get; set; } = new List<string[]>();

            public double PositiveAccuracy => TotalPositiveTests > 0 ? (double)PositiveTestsPassed / TotalPositiveTests : 1.0;
            public double NegativeAccuracy => TotalNegativeTests > 0 ? (double)NegativeTestsPassed / TotalNegativeTests : 1.0;
            public double OverallAccuracy => (TotalPositiveTests + TotalNegativeTests) > 0
                ? (double)(PositiveTestsPassed + NegativeTestsPassed) / (TotalPositiveTests + TotalNegativeTests)
                : 1.0;

            public override string ToString()
            {
                return $"Positive: {PositiveTestsPassed}/{TotalPositiveTests} ({PositiveAccuracy:P2}), " +
                       $"Negative: {NegativeTestsPassed}/{TotalNegativeTests} ({NegativeAccuracy:P2}), " +
                       $"Overall: {OverallAccuracy:P2}";
            }
        }

        /// <summary>
        /// Runs the full stress test on a learned grammar.
        /// </summary>
        /// <param name="learnedGrammar">The grammar learned by the system</param>
        /// <param name="minLength">Minimum sentence length for testing</param>
        /// <param name="maxLength">Maximum sentence length for testing</param>
        /// <param name="samplesPerLength">Number of samples to generate per length</param>
        /// <param name="negativeTestsPerLength">Number of negative tests per length</param>
        public StressTestResult RunStressTest(
            Grammar learnedGrammar,
            int minLength = 10,
            int maxLength = 30,
            int samplesPerLength = 10,
            int negativeTestsPerLength = 10)
        {
            var result = new StressTestResult();

            // Debug: Check which lengths are derivable
            var derivableLengths = GetDerivableLengths(minLength, maxLength);
            var derivableList = new List<int>();
            foreach (var kv in derivableLengths)
            {
                if (kv.Value)
                {
                    derivableList.Add(kv.Key);
                }
            }

            // Part 1: Positive tests - sentences from target grammar should be accepted
            RunPositiveTests(learnedGrammar, minLength, maxLength, samplesPerLength, result);

            // Part 2: Negative tests - sentences NOT in target grammar should be rejected
            RunNegativeTests(learnedGrammar, minLength, maxLength, negativeTestsPerLength, result);

            return result;
        }

        /// <summary>
        /// Part 1: Generate sentences from target grammar, verify learned grammar accepts them.
        /// </summary>
        private void RunPositiveTests(Grammar learnedGrammar, int minLength, int maxLength, int samplesPerLength, StressTestResult result)
        {
            for (int len = minLength; len <= maxLength; len++)
            {
                for (int i = 0; i < samplesPerLength; i++)
                {
                    var sentence = _generator.GenerateRandomSentence(len);
                    if (sentence == null)
                    {
                        // Target grammar cannot generate sentences of this length
                        continue;
                    }

                    result.TotalPositiveTests++;

                    // Check if learned grammar accepts this sentence
                    bool accepted = CheckGrammarAccepts(learnedGrammar, sentence, _learnedLexicon);

                    if (accepted)
                    {
                        result.PositiveTestsPassed++;
                    }
                    else
                    {
                        result.PositiveTestsFailed++;
                        result.FailedPositiveSentences.Add(sentence);
                    }
                }
            }
        }

        /// <summary>
        /// Part 2: Generate sentences NOT in target grammar, verify learned grammar rejects them.
        /// </summary>
        private void RunNegativeTests(Grammar learnedGrammar, int minLength, int maxLength, int negativeTestsPerLength, StressTestResult result)
        {
            var random = new Random();

            for (int len = minLength; len <= maxLength; len++)
            {
                int testsGenerated = 0;
                int maxAttempts = negativeTestsPerLength * 10; // Limit attempts to avoid infinite loop
                int attempts = 0;

                while (testsGenerated < negativeTestsPerLength && attempts < maxAttempts)
                {
                    attempts++;

                    // Generate a random sentence that might not be in the target grammar
                    var sentence = GenerateRandomNegativeSentence(len, random);
                    if (sentence == null)
                        continue;

                    // Verify it's actually NOT accepted by target grammar
                    bool acceptedByTarget = CheckGrammarAccepts(_targetGrammar, sentence, _targetLexicon);
                    if (acceptedByTarget)
                    {
                        // This is actually a positive example, skip
                        continue;
                    }

                    testsGenerated++;
                    result.TotalNegativeTests++;

                    // Check if learned grammar (correctly) rejects this sentence
                    bool acceptedByLearned = CheckGrammarAccepts(learnedGrammar, sentence, _learnedLexicon);

                    if (!acceptedByLearned)
                    {
                        result.NegativeTestsPassed++;
                    }
                    else
                    {
                        result.NegativeTestsFailed++;
                        result.FailedNegativeSentences.Add(sentence);
                    }
                }
            }
        }

        /// <summary>
        /// Generates a random sentence that is likely NOT in the target grammar.
        /// Uses several strategies:
        /// 1. Random word sequences from lexicon
        /// 2. Mutation of valid sentences
        /// </summary>
        private string[] GenerateRandomNegativeSentence(int length, Random random)
        {
            // Strategy: Generate random sequence of words from lexicon
            var allWordsList = new string[_targetLexicon.WordWithPossiblePOS.Count];
            _targetLexicon.WordWithPossiblePOS.Keys.CopyTo(allWordsList, 0);
            var allWords = allWordsList;
            if (allWords.Length == 0)
                return null;

            var sentence = new string[length];
            for (int i = 0; i < length; i++)
            {
                sentence[i] = allWords[random.Next(allWords.Length)];
            }
            return sentence;
        }

        /// <summary>
        /// Checks if a grammar accepts a given sentence.
        /// </summary>
        private static bool CheckGrammarAccepts(Grammar grammar, string[] sentence, Lexicon lexicon)
        {
            var recogniser = new CKYRecognizer(lexicon, sentence);
            var (_, parsedCode) = recogniser.RecognizeSentence(grammar);
            return parsedCode == 1;
        }


        /// <summary>
        /// Gets a summary of what lengths are derivable from the target grammar.
        /// </summary>
        public Dictionary<int, bool> GetDerivableLengths(int minLength, int maxLength)
        {
            var result = new Dictionary<int, bool>();
            int startSymbol = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);

            for (int len = minLength; len <= maxLength; len++)
            {
                result[len] = _generator.CanDerive(startSymbol, len);
            }

            return result;
        }

        /// <summary>
        /// Prints a detailed report of the stress test results.
        /// </summary>
        public static (string Report, bool AllPassed) BuildReport(StressTestResult result)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== GRAMMAR STRESS TEST REPORT ===");
            sb.AppendLine();

            sb.AppendLine("POSITIVE TESTS (sentences from target grammar):");
            sb.AppendLine($"  Passed: {result.PositiveTestsPassed}/{result.TotalPositiveTests} ({result.PositiveAccuracy:P2})");

            if (result.FailedPositiveSentences.Count > 0)
            {
                sb.AppendLine("  Failed sentences (first 5):");
                int positiveLimit = Math.Min(5, result.FailedPositiveSentences.Count);
                for (int i = 0; i < positiveLimit; i++)
                {
                    sb.AppendLine($"    {string.Join(" ", result.FailedPositiveSentences[i])}");
                }
            }

            sb.AppendLine();

            sb.AppendLine("NEGATIVE TESTS (sentences NOT in target grammar):");
            sb.AppendLine($"  Passed: {result.NegativeTestsPassed}/{result.TotalNegativeTests} ({result.NegativeAccuracy:P2})");

            if (result.FailedNegativeSentences.Count > 0)
            {
                sb.AppendLine("  Failed sentences (first 5, incorrectly accepted):");
                int negativeLimit = Math.Min(5, result.FailedNegativeSentences.Count);
                for (int i = 0; i < negativeLimit; i++)
                {
                    sb.AppendLine($"    {string.Join(" ", result.FailedNegativeSentences[i])}");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"OVERALL ACCURACY: {result.OverallAccuracy:P2}");
            sb.AppendLine("==================================");

            bool allPassed =
                result.PositiveTestsPassed == result.TotalPositiveTests &&
                result.NegativeTestsPassed == result.TotalNegativeTests;

            return (sb.ToString(), allPassed);
        }
    }
}
