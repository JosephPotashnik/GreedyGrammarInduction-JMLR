// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using EarleyParserForGreedyGrammarInduction;

namespace GreedyGrammarInduction
{
    class SampleGenerator
    {
        private static double F(float x, float mean, float var)
        {
            return Math.Exp(-(x - mean) * (x - mean) / (2 * var));
        }

        static int[] PreparePowerLawDistribution((string, int)[] allNonTerminalsSequences)
        {
            int[] powerlaw = new int[allNonTerminalsSequences.Length];

            int startVal = allNonTerminalsSequences.Length;

            for (int i = 0; i < allNonTerminalsSequences.Length; i++)
            {
                powerlaw[i] = startVal / (allNonTerminalsSequences[i].Item2 + 1);
                if (powerlaw[i] == 0)
                {
                    powerlaw[i] = 1; //a nonzero heavy tail
                }
            }
            return powerlaw;
        }

        static int[] PrepareNormalDistribution(int numberOfStates)
        {
            int[] normal = new int[numberOfStates];

            float mean = numberOfStates / 2.0f;
            float stddev = numberOfStates / 5.0f;
            float var = stddev * stddev;

            for (int i = 0; i < numberOfStates; i++)
            {
                normal[i] = (int)(100 * F(i, mean, var));
            }

            return normal;
        }
        static int[] PrepareUniformDistribution(int numberOfStates)
        {
            int[] uniform = new int[numberOfStates];
            for (int i = 0; i < numberOfStates; i++)
            {
                uniform[i] = 1;
            }

            return uniform;
        }

        static public int[] PrepareDistribution(DistributionType distType, (string, int)[] allNonTerminalsSequencesWithLengths)
        {
            int numberOfStates = allNonTerminalsSequencesWithLengths.Length;
            int[] distribution;
            if (distType == DistributionType.Uniform)
            {
                distribution = PrepareUniformDistribution(numberOfStates);
            }
            else if (distType == DistributionType.Normal)
            {
                distribution = PrepareNormalDistribution(numberOfStates);
            }
            else if (distType == DistributionType.PowerLaw)
            {

                distribution = PreparePowerLawDistribution(allNonTerminalsSequencesWithLengths);
            }
            else
            {
                throw new Exception("unrecognized distribution Type");
            }

            return distribution;
        }

        public static string[][] DrawSamples(
            string[] allNonTerminalsSequences,
            int[] distributionOverStates,
            int numberOfSamples,
            Lexicon universalLexicon,
            Lexicon textLexicon,
            Random seededRandom = null)
        {
            var rand = seededRandom ?? new Random();
            var posCategoriesIds = new HashSet<int>();
            var sentences = new string[numberOfSamples][];
            bool deterministic = seededRandom != null;

            for (int j = 0; j < numberOfSamples; j++)
            {
                int k = DrawStateIndexAccordingToDistribution(distributionOverStates, seededRandom);
                var nonterminalSentence = allNonTerminalsSequences[k];
                var arr = nonterminalSentence.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                var sentence = new string[arr.Length];
                for (var i = 0; i < sentence.Length; i++)
                {
                    var posCat = arr[i];
                    var id = SymbolTable.Instance.GetId(posCat);
                    posCategoriesIds.Add(id);
                    //you can improve here - do not repeatedly call toARRAY.
                    var possibleWordsSet = universalLexicon.POSWithPossibleWords[id];
                    var possibleWords = new string[possibleWordsSet.Count];
                    possibleWordsSet.CopyTo(possibleWords);
                    if (deterministic)
                        Array.Sort(possibleWords, StringComparer.Ordinal);
                    sentence[i] = possibleWords[rand.Next(possibleWords.Length)];

                }
                sentences[j] = sentence;
            }

            foreach (var category in posCategoriesIds)
            {
                if (!textLexicon.ContainsPOS(category))
                {
                    var wordsSet = universalLexicon.POSWithPossibleWords[category];
                    var wordsArray = new string[wordsSet.Count];
                    wordsSet.CopyTo(wordsArray);
                    if (deterministic)
                        Array.Sort(wordsArray, StringComparer.Ordinal);
                    textLexicon.AddWordsToPOSCategory(category, wordsArray);
                }
            }

            return sentences;
        }

        private static int DrawStateIndexAccordingToDistribution(int[] distributionOverStates, Random seededRandom = null)
        {
            int totalWeight = 0;
            for (int i = 0; i < distributionOverStates.Length; i++)
            {
                totalWeight += distributionOverStates[i];
            }

            var r = seededRandom == null
                ? Pseudorandom.NextInt(totalWeight)
                : seededRandom.Next(totalWeight);

            var sum = 0;
            for (int i = 0; i < distributionOverStates.Length; i++)
            {
                if (sum + distributionOverStates[i] > r)
                {
                    return i;
                }

                sum += distributionOverStates[i];
            }
            return 0; //throw new Exception("should never arrive here!");never arrives to this line.

        }

        public static (string, int)[] GetAllPartsOfSpeechSequences(List<Rule> grammarRules, Lexicon universalLexicon, int maxWords)
        {
            var g = Grammar.CreateGrammar(grammarRules);
            var generator = new EarleyGenerator(g, universalLexicon, maxWords);
            generator.GenerateSentence();
            var ret = generator.GetAllSequences();
            List<(string, int)> result = new List<(string, int)>();

            for (int i = 0; i < ret.Length; i++)
            {
                for (int j = 0; j < ret[i].Length; j++)
                {
                    result.Add((ret[i][j], i));

                }

            }

            return result.ToArray();
        }
    }
}
