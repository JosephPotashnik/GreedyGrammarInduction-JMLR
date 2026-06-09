// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
namespace GreedyGrammarInductionLearner
{
    public class SentencesInfo
    {
        //the sentence being parsed
        public string[] Sentence { get; set; }

        //the number of times the sentence was encountered in the corpus.
        public int Count { get; set; }

        //the length of the sentence (number of words)
        public int Length { get; set; }
    }
}
