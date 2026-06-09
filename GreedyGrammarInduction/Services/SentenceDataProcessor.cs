// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner;

namespace GreedyGrammarInduction.Services
{
    /// <summary>
    /// Helper class for processing sentence data and POS sequences
    /// </summary>
    public static class SentenceDataProcessor
    {
        private static readonly char[] s_spaceSeparator = [' '];

        /// <summary>
        /// Reduces raw sentence data to unique POS sequence types with counts
        /// </summary>
        public static SentenceWithCounts[] ReduceDataToUniquePOSTypes(
            string[][] data,
            Lexicon dataLexicon,
            HashSet<List<int[]>> uniquePOSSequences,
            Dictionary<List<int[]>, SentenceWithCounts> dataDic)
        {
            // Leave in data only unique sets of sequences of POS
            foreach (var sentence in data)
            {
                var posSequence = POSSequencesOfSentences(sentence, dataLexicon);

                if (uniquePOSSequences.Add(posSequence))
                {
                    dataDic[posSequence] = new SentenceWithCounts { Sentence = sentence, Count = 1 };
                }
                else
                {
                    dataDic[posSequence].Count++;
                }
            }

            var result = new SentenceWithCounts[dataDic.Count];
            dataDic.Values.CopyTo(result, 0);
            return result;
        }

        /// <summary>
        /// Generates all possible POS sequences for a given sentence
        /// </summary>
        private static List<int[]> POSSequencesOfSentences(Span<string> sentence, Lexicon lexicon)
        {
            if (sentence.Length == 0)
            {
                return [[]];
            }

            var l = new List<int[]>();

            var firstWord = sentence[0];
            var poses = lexicon.WordWithPossiblePOS[firstWord];
            var restOfSentencePOSSequences = POSSequencesOfSentences(sentence[1..], lexicon);

            foreach (var pos in poses)
            {
                foreach (var sequence in restOfSentencePOSSequences)
                {
                    var posSequences = new int[sentence.Length];
                    posSequences[0] = pos;
                    if (sequence.Length > 0)
                    {
                        sequence.CopyTo(posSequences, 1);
                    }

                    l.Add(posSequences);
                }
            }
            return l;
        }

        /// <summary>
        /// Filters sentences by word length range
        /// </summary>
        public static SentenceWithCounts[] GetSentencesInWordLengthRange(
            SentenceWithCounts[] allData,
            int minWords,
            int maxWords)
        {
            var sentences = new List<SentenceWithCounts>();

            foreach (var arr in allData)
            {
                if (arr.Sentence.Length > maxWords || arr.Sentence.Length < minWords)
                {
                    continue;
                }

                sentences.Add(arr);
            }

            //var sss = sentences.Select(inner => string.Concat(inner.Sentence)).ToArray();
            //foreach (var item in sss)
            //{
            //    Console.WriteLine(item);
            //} 
            return sentences.ToArray();
        }

        public static (SentenceWithCounts[] Sentences, Lexicon DataLexicon) ReadSamplesFromFile(
            string samplesFileName,
            Lexicon universalLexicon)
        {
            ArgumentNullException.ThrowIfNull(universalLexicon);

            return ReadSamplesFromFileCore(
                samplesFileName,
                (sentence, dataLexicon, sampleFilePath, lineNumber) =>
                    AddSentenceWordsToDataLexicon(sentence, universalLexicon, dataLexicon, sampleFilePath, lineNumber));
        }

        public static (SentenceWithCounts[] Sentences, Lexicon DataLexicon) ReadSamplesFromFileWithIdentityLexicon(
            string samplesFileName)
        {
            return ReadSamplesFromFileCore(
                samplesFileName,
                AddSentenceWordsToIdentityLexicon);
        }

        private static (SentenceWithCounts[] Sentences, Lexicon DataLexicon) ReadSamplesFromFileCore(
            string samplesFileName,
            Action<string[], Lexicon, string, int> addSentenceWordsToDataLexicon)
        {
            if (string.IsNullOrEmpty(samplesFileName))
            {
                throw new ArgumentException("SamplesFileName is required when SourceOfTruth is Samples.", nameof(samplesFileName));
            }

            var sampleFilePath = Path.Combine(".", "InputData", "Samples", samplesFileName);
            var sentences = new List<SentenceWithCounts>();
            var dataLexicon = new Lexicon();

            using var reader = File.OpenText(sampleFilePath);
            string line;
            int lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith('#') ||
                    line.StartsWith("Count", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tabIndex = line.IndexOf('\t');
                var countText = tabIndex < 0 ? line.Trim() : line[..tabIndex].Trim();
                var sentenceText = tabIndex < 0 ? string.Empty : line[(tabIndex + 1)..];
                if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                {
                    throw new FormatException($"Invalid sample count in {sampleFilePath} at line {lineNumber}: {countText}");
                }

                if (count <= 0)
                {
                    throw new FormatException($"Sample count must be positive in {sampleFilePath} at line {lineNumber}: {count}");
                }

                var sentence = string.IsNullOrWhiteSpace(sentenceText)
                    ? Array.Empty<string>()
                    : sentenceText.Split(s_spaceSeparator, StringSplitOptions.RemoveEmptyEntries);

                addSentenceWordsToDataLexicon(sentence, dataLexicon, sampleFilePath, lineNumber);
                sentences.Add(new SentenceWithCounts { Sentence = sentence, Count = count });
            }

            if (sentences.Count == 0)
            {
                throw new InvalidDataException($"No samples were found in {sampleFilePath}.");
            }

            sentences.Sort((a, b) => a.Sentence.Length.CompareTo(b.Sentence.Length));
            return (sentences.ToArray(), dataLexicon);
        }

        private static void AddSentenceWordsToDataLexicon(
            string[] sentence,
            Lexicon universalLexicon,
            Lexicon dataLexicon,
            string sampleFilePath,
            int lineNumber)
        {
            for (int i = 0; i < sentence.Length; i++)
            {
                var word = sentence[i];
                if (!universalLexicon.WordWithPossiblePOS.TryGetValue(word, out var posIds))
                {
                    throw new FormatException($"Unknown sample word in {sampleFilePath} at line {lineNumber}: {word}");
                }

                foreach (var posId in posIds)
                {
                    dataLexicon.AddWordsToPOSCategory(posId, [word]);
                }
            }
        }

        private static void AddSentenceWordsToIdentityLexicon(
            string[] sentence,
            Lexicon dataLexicon,
            string sampleFilePath,
            int lineNumber)
        {
            for (int i = 0; i < sentence.Length; i++)
            {
                var word = sentence[i];
                if (string.IsNullOrWhiteSpace(word))
                {
                    throw new FormatException($"Empty sample token in {sampleFilePath} at line {lineNumber}.");
                }

                var posId = SymbolTable.Instance.GetId(word);
                dataLexicon.AddWordsToPOSCategory(posId, [word]);
            }
        }
    }
}
