// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
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
