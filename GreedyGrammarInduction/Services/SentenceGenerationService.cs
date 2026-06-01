// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner;
using GreedyGrammarInductionLearner.SearchSpace;
using Microsoft.Extensions.Logging;

namespace GreedyGrammarInduction.Services
{
    /// <summary>
    /// Request model for sentence generation
    /// </summary>
    public class SentenceGenerationRequest
    {
        public List<Rule> GrammarRules { get; set; }
        public Lexicon UniversalLexicon { get; set; }
        public DistributionType DistributionType { get; set; }
        public int MaxSentenceLength { get; set; }
        public double AllowedMissingProbabilityMass { get; set; }
        public int NumberOfSamples { get; set; } = 1000;
        public int? RandomSeed { get; set; }
        public string OutputSamplesToFile { get; set; }
        public string GrammarFileName { get; set; }
    }

    /// <summary>
    /// Response model for sentence generation
    /// </summary>
    public class SentenceGenerationResponse
    {
        public SentenceWithCounts[] Sentences { get; set; }
        public Lexicon DataLexicon { get; set; }
        public SentenceGenerationMetadata Metadata { get; set; }
    }

    /// <summary>
    /// Metadata about the sentence generation process
    /// </summary>
    public class SentenceGenerationMetadata
    {
        public int TotalSamples { get; set; }
        public int UniqueSentenceTypes { get; set; }
        public int MaxSentenceLength { get; set; }
        public int MinSentenceLength { get; set; }
        public Dictionary<int, int> SentenceCountsByLength { get; set; }
        public Dictionary<int, int> UniqueTypesByLength { get; set; }
        public long GenerationTimeMs { get; set; }
        public double ActualMissingProbabilityMass { get; set; }
    }

    /// <summary>
    /// Service responsible for generating sentences from a grammar and lexicon
    /// </summary>
    public class SentenceGenerationService
    {
        private readonly ILogger _logger;

        public SentenceGenerationService(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Generates sentences based on the provided request parameters
        /// </summary>
        public SentenceGenerationResponse GenerateSentences(SentenceGenerationRequest request)
        {
            var stopwatch = Stopwatch.StartNew();

            // Note: SymbolTable should already be initialized by caller with START, GAMMA, EPSILON, STAR
            // Setup scanned rules using the provided lexicon
            var scannedRuleDict = new ScannedRulesDict(request.UniversalLexicon);
            EarleyParser.ScannedRules = scannedRuleDict.ScannedRules;

            // Initialize Grammar.PartsOfSpeech from the lexicon (required before RenameVariables)
            Grammar.PartsOfSpeech = [..request.UniversalLexicon.POSWithPossibleWords.Keys];

            // Use the provided grammar rules
            var grammarRules = request.GrammarRules;

            _logger.LogInformation("Target Grammar:");
            for (int i = 0; i < grammarRules.Count; i++)
            {
                _logger.LogInformation(grammarRules[i].ToFormattedStackString());
            }

            // Get all possible POS sequences
            var allNonTerminalsSequencesWithLengths = SampleGenerator.GetAllPartsOfSpeechSequences(
                grammarRules, request.UniversalLexicon, request.MaxSentenceLength);

            string[] allNonTerminalsSequences = new string[allNonTerminalsSequencesWithLengths.Length];
            for (int i = 0; i < allNonTerminalsSequencesWithLengths.Length; i++)
            {
                allNonTerminalsSequences[i] = allNonTerminalsSequencesWithLengths[i].Item1;
            }

            // Prepare distribution
            int[] distribution = SampleGenerator.PrepareDistribution(
                request.DistributionType, allNonTerminalsSequencesWithLengths);

            // Generate samples using Good-Turing estimator
            var result = GenerateSamplesWithGoodTuring(
                allNonTerminalsSequences,
                distribution,
                request.NumberOfSamples,
                request.UniversalLexicon,
                request.AllowedMissingProbabilityMass,
                request.RandomSeed);

            stopwatch.Stop();

            // Build response with metadata
            var response = new SentenceGenerationResponse
            {
                Sentences = result.Sentences,
                DataLexicon = result.DataLexicon,
                Metadata = new SentenceGenerationMetadata
                {
                    TotalSamples = result.TotalSamples,
                    UniqueSentenceTypes = result.Sentences.Length,
                    MaxSentenceLength = GetMaxSentenceLength(result.Sentences),
                    MinSentenceLength = GetMinSentenceLength(result.Sentences),
                    SentenceCountsByLength = CalculateSentenceCountsByLength(result.Sentences),
                    UniqueTypesByLength = CalculateUniqueTypesByLength(result.Sentences),
                    GenerationTimeMs = stopwatch.ElapsedMilliseconds,
                    ActualMissingProbabilityMass = result.ActualMissingProbabilityMass
                }
            };

            if (request.RandomSeed.HasValue)
                _logger.LogInformation("Sampling seed: {RandomSeed}", request.RandomSeed.Value);

            LogSentenceStatistics(response, request.OutputSamplesToFile, request.GrammarFileName);

            return response;
        }

        /// <summary>
        /// Generates samples using Good-Turing estimator for unseen probability mass
        /// </summary>
        private static (SentenceWithCounts[] Sentences, Lexicon DataLexicon, int TotalSamples, double ActualMissingProbabilityMass)
            GenerateSamplesWithGoodTuring(
                string[] allNonTerminalsSequences,
                int[] distribution,
                int numberOfSamples,
                Lexicon universalLexicon,
                double allowedMissingProbabilityMass,
                int? seed)
        {
            Lexicon dataLexicon = new Lexicon();
            HashSet<List<int[]>> uniquePOSSequences = new HashSet<List<int[]>>(ListIntArrayCompare.Shared);
            Dictionary<List<int[]>, SentenceWithCounts> dataDic = new Dictionary<List<int[]>, SentenceWithCounts>(ListIntArrayCompare.Shared);
            SentenceWithCounts[] sentencesWithCounts = null;
            int totalSamples = 0;
            int upperBound = 9;
            double actualMissingProbMass = 0;
            Random seededRandom = seed.HasValue ? new Random(seed.Value) : null;

            bool enoughSamples = false;
            while (!enoughSamples)
            {
                var data = SampleGenerator.DrawSamples(
                    allNonTerminalsSequences,
                    distribution,
                    numberOfSamples,
                    universalLexicon,
                    dataLexicon,
                    seededRandom);

                sentencesWithCounts = SentenceDataProcessor.ReduceDataToUniquePOSTypes(
                    data,
                    dataLexicon,
                    uniquePOSSequences,
                    dataDic);

                Array.Sort(sentencesWithCounts, (a, b) => a.Sentence.Length.CompareTo(b.Sentence.Length));

                int numberOfSingletons = 0;
                totalSamples = 0;

                for (int i = 0; i < sentencesWithCounts.Length; i++)
                {
                    if (sentencesWithCounts[i].Sentence.Length > upperBound)
                    {
                        break;
                    }

                    totalSamples += sentencesWithCounts[i].Count;
                    if (sentencesWithCounts[i].Count == 1)
                    {
                        numberOfSingletons++;
                    }
                }

                // Good-Turing test
                double frac = numberOfSingletons / (double)totalSamples;
                actualMissingProbMass = frac;
                enoughSamples = frac <= allowedMissingProbabilityMass;
            }

            return (sentencesWithCounts, dataLexicon, totalSamples, actualMissingProbMass);
        }

        /// <summary>
        /// Calculates sentence counts grouped by length
        /// </summary>
        private static Dictionary<int, int> CalculateSentenceCountsByLength(SentenceWithCounts[] sentences)
        {
            var result = new Dictionary<int, int>();
            for (int i = 0; i < sentences.Length; i++)
            {
                int len = sentences[i].Sentence.Length;
                if (result.TryGetValue(len, out int existing))
                    result[len] = existing + sentences[i].Count;
                else
                    result[len] = sentences[i].Count;
            }
            return result;
        }

        /// <summary>
        /// Calculates unique sentence types grouped by length
        /// </summary>
        private static Dictionary<int, int> CalculateUniqueTypesByLength(SentenceWithCounts[] sentences)
        {
            var result = new Dictionary<int, int>();
            for (int i = 0; i < sentences.Length; i++)
            {
                int len = sentences[i].Sentence.Length;
                if (result.TryGetValue(len, out int existing))
                    result[len] = existing + 1;
                else
                    result[len] = 1;
            }
            return result;
        }

        private static int GetMaxSentenceLength(SentenceWithCounts[] sentences)
        {
            int max = sentences[0].Sentence.Length;
            for (int i = 1; i < sentences.Length; i++)
            {
                int len = sentences[i].Sentence.Length;
                if (len > max) max = len;
            }
            return max;
        }

        private static int GetMinSentenceLength(SentenceWithCounts[] sentences)
        {
            int min = sentences[0].Sentence.Length;
            for (int i = 1; i < sentences.Length; i++)
            {
                int len = sentences[i].Sentence.Length;
                if (len < min) min = len;
            }
            return min;
        }

        /// <summary>
        /// Logs statistics about the generated sentences
        /// </summary>
        private void LogSentenceStatistics(SentenceGenerationResponse response, string outputSamplesToFile, string grammarFileName)
        {
            _logger.LogInformation($"Data samples:");
            var sortedCounts = new List<KeyValuePair<int, int>>(response.Metadata.SentenceCountsByLength);
            sortedCounts.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kvp in sortedCounts)
            {
                _logger.LogInformation($"{kvp.Value} sentences of length {kvp.Key}");
            }

            _logger.LogInformation($"Unique sentences types (POS sequences) from data samples:");
            var sortedTypes = new List<KeyValuePair<int, int>>(response.Metadata.UniqueTypesByLength);
            sortedTypes.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kvp in sortedTypes)
            {
                _logger.LogInformation($"{kvp.Value} unique sentences types of length {kvp.Key}");
            }


            _logger.LogInformation($"Total samples: {response.Metadata.TotalSamples}");
            _logger.LogInformation($"Actual missing probability mass: {response.Metadata.ActualMissingProbabilityMass:F4}");
            _logger.LogInformation($"Generation time: {response.Metadata.GenerationTimeMs}ms");

            if (!string.IsNullOrEmpty(outputSamplesToFile))
            {
                WriteUniqueSamplesToFile(response.Sentences, outputSamplesToFile, grammarFileName);
            }
        }

        private void WriteUniqueSamplesToFile(SentenceWithCounts[] sentences, string outputSamplesToFile, string grammarFileName)
        {
            var outputDirectory = Path.GetDirectoryName(outputSamplesToFile);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var builder = new StringBuilder();
            builder.AppendLine($"# Samples of Target {grammarFileName} #");
            builder.AppendLine("Count\tSentence");
            for (int i = 0; i < sentences.Length; i++)
            {
                builder.Append(sentences[i].Count);
                builder.Append('\t');
                builder.AppendLine(string.Join(" ", sentences[i].Sentence));
            }

            File.WriteAllText(outputSamplesToFile, builder.ToString());
            _logger.LogInformation("Wrote {UniqueSampleCount} unique sampled sentences to {OutputSamplesToFile}", sentences.Length, outputSamplesToFile);
        }
    }
}
