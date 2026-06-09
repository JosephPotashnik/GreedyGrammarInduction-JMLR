// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner;
using GreedyGrammarInductionLearner.SearchSpace;
using Microsoft.Extensions.Logging;
using ShellProgressBar;

namespace GreedyGrammarInduction.Services
{
    /// <summary>
    /// Request model for grammar learning
    /// </summary>
    public class GrammarLearningRequest
    {
        public SentenceWithCounts[] Sentences { get; set; }
        public Lexicon DataLexicon { get; set; }
        public SearchSpaceParams SearchSpaceParams { get; set; }
        public string ResultsContainGrammarWeaklyEquivalentTo { get; set; }
        public string ResultsContainGrammarStronglyEquivalentTo { get; set; }
        public OracleComparisonMode VerificationComparisonMode { get; set; }
        public string VerificationTargetLexiconFileName { get; set; }
        public bool RunPostLearnerVerification { get; set; }
    }

    /// <summary>
    /// Response model for grammar learning
    /// </summary>
    public class GrammarLearningResponse
    {
        public bool Success { get; set; }
        public Grammar LearnedGrammar { get; set; }
        public List<List<Rule>> BestLearnedGrammars { get; set; }
        public GrammarLearningMetadata Metadata { get; set; }
    }

    /// <summary>
    /// Metadata about the grammar learning process
    /// </summary>
    public class GrammarLearningMetadata
    {
        public int MaxSentenceLength { get; set; }
        public int MinSentenceLength { get; set; }
        public int NumberOfSentences { get; set; }
        public int NumberOfNonTerminals { get; set; }
        public int MaxRulesCount { get; set; }
        public int MaxDepthBetweenSubSolutions { get; set; }
    }

    /// <summary>
    /// Service responsible for learning grammar from sentence data
    /// </summary>
    public class GrammarLearnerService
    {
        private readonly ILogger _logger;
        private readonly Func<string, IProgress<double>> _progressBarFactory;

        public GrammarLearnerService(ILogger logger, Func<string, IProgress<double>> progressBarFactory = null)
        {
            _logger = logger;
            _progressBarFactory = progressBarFactory ?? CreateProgressBar;
        }

        /// <summary>
        /// Learns a grammar from the provided sentence data
        /// </summary>
        public GrammarLearningResponse LearnGrammar(GrammarLearningRequest request)
        {
            var data = request.Sentences;
            var dataLexicon = request.DataLexicon;
            var ssParams = request.SearchSpaceParams;
            bool runPostLearnerVerification = request.RunPostLearnerVerification;

            int maxSentenceLength = data[0].Sentence.Length;
            int minWordsInSentences = data[0].Sentence.Length;
            for (int i = 1; i < data.Length; i++)
            {
                int len = data[i].Sentence.Length;
                if (len > maxSentenceLength) maxSentenceLength = len;
                if (len < minWordsInSentences) minWordsInSentences = len;
            }
            int numberOfNonterminals = ssParams.MaxNumberOfNonTerminals;
            int maxRulesCount = ssParams.MaxNumberOfRules;
            int maxDepthBetweenSubSolutions = ssParams.MaxDepthBetweenSubSolutions;
            int maxDepthAfterMinimalGlobal = ssParams.MaxDepthAfterMinimalGlobal;
            int minimumSizeOfOptimalSubSolution = ssParams.MinSizeOfOptimalSubSolution;
            var heuristicParams = ssParams.SearchSpaceHeuristicsParams ?? new SearchSpaceHeuristicsParams();
            bool continueSearchAfterOptimalNode = heuristicParams.ContinueSearchAfterOptimalNode;
            bool skipKnownOptimalStructuralNodesAcrossRoots = heuristicParams.SkipKnownOptimalStructuralNodesAcrossRoots;
            var paretoRibbonThickness = ssParams.ParetoRibbonThickness;
            Oracle oracle = null;
            if (runPostLearnerVerification)
            {
                oracle = new Oracle(
                    _logger,
                    request.ResultsContainGrammarWeaklyEquivalentTo,
                    dataLexicon,
                    maxSentenceLength,
                    request.ResultsContainGrammarStronglyEquivalentTo,
                    request.VerificationComparisonMode,
                    request.VerificationTargetLexiconFileName);
            }

            // Filter sentences by word length range
            var sentences = SentenceDataProcessor.GetSentencesInWordLengthRange(data, minWordsInSentences, maxSentenceLength);

           
            // Detect and strip epsilon (empty string) from evidence.
            // The learner works with epsilon-free grammars; if the language contains the empty string,
            // we add START -> epsilon to the final learned grammars after the search.
            bool evidenceContainsEmptyString = sentences.Length > 0 && sentences[0].Sentence.Length == 0;
            if (evidenceContainsEmptyString)
            {
                var nonEpsilonSentences = new SentenceWithCounts[sentences.Length - 1];
                Array.Copy(sentences, 1, nonEpsilonSentences, 0, sentences.Length - 1);
                sentences = nonEpsilonSentences;
            }

            bool epsilonInLanguage = (oracle?.TargetHasEpsilon ?? false) || evidenceContainsEmptyString;
            if (epsilonInLanguage)
            {
                _logger.LogInformation("The empty string was detected. The rule START -> ε is added post-learning.");
            }
            var posInText = new HashSet<int>(dataLexicon.POSWithPossibleWords.Keys);
            var sentenceArrays = new string[sentences.Length][];
            for (int i = 0; i < sentences.Length; i++)
            {
                sentenceArrays[i] = sentences[i].Sentence;
            }

            // Register Xi nonterminals in SymbolTable
            // IMPORTANT: This must happen AFTER POS symbols are already in the SymbolTable
            // (POS symbols are registered when the lexicon/data is loaded)
            var symbolTable = SymbolTable.Instance;
            var startId = symbolTable.GetId(Grammar.StartSymbol);
            for (int i = 0; i < numberOfNonterminals; i++)
            {
                symbolTable.GetId($"X{i + 1}");
            }

            var latticeRuleSpace = new LatticeRuleSpace(posInText, numberOfNonterminals);

            // Create the search algorithm
            var algorithm = new HypothesesSearchSpace(
                _logger,
                _progressBarFactory,
                latticeRuleSpace,
                maxRulesCount,
                maxDepthBetweenSubSolutions,
                maxDepthAfterMinimalGlobal,
                minimumSizeOfOptimalSubSolution,
                evidenceContainsEmptyString,
                continueSearchAfterOptimalNode: continueSearchAfterOptimalNode,
                skipKnownOptimalStructuralNodesAcrossRoots: skipKnownOptimalStructuralNodesAcrossRoots,
                paretoRibbonThickness: paretoRibbonThickness);

            var evidenceShapeVectorCalculator = new EvidenceTreesShape(sentences, maxSentenceLength, dataLexicon, latticeRuleSpace);

            _logger.LogInformation("---- Learner -----");
            try
            {
                algorithm.Search(evidenceShapeVectorCalculator);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.ToString());
                throw; // Re-throw for API to handle
            }
            _logger.LogInformation("---- End of Learner -----");


            // All global optimums were collected during PrintConsolidatedOptimalSolutionsGraph
            var allGlobals = algorithm.OptimalSolutionsTracker?.BestLearnedGrammars ?? new();

            List<(ContextFreeGrammar Grammar, int Index)> bestGrammars;
            bool actualSuccess;
            if (runPostLearnerVerification)
            {
                _logger.LogInformation("Post-learner verification assumes the caller has ensured the weak-equivalence target grammar is consistent with the evidence samples.");
                // Post-learner verification evaluates which globals are weakly/strongly equivalent to the target
                (bestGrammars, actualSuccess) = oracle.EvaluateLearnedGrammars(allGlobals);
            }
            else
            {
                bestGrammars = allGlobals;
                actualSuccess = allGlobals.Count > 0;
                _logger.LogInformation(
                    "Post-learner verification is disabled; returning {GlobalOptimumCount} learned global optimum(s) without target-equivalence validation.",
                    allGlobals.Count);
            }

            // If the language contains the empty string, append START -> epsilon to each best learned grammar
            if (epsilonInLanguage && bestGrammars.Count > 0)
            {
                var epsilonRule = new Rule(startId, Array.Empty<int>(), RuleType.CFGRules);
                var augmented = new List<(ContextFreeGrammar Grammar, int Index)>(bestGrammars.Count);
                foreach (var (grammar, index) in bestGrammars)
                {
                    var rules = grammar.GetRules();
                    rules.Add(epsilonRule);
                    augmented.Add((new ContextFreeGrammar(rules), index));
                }
                bestGrammars = augmented;
            }

            var bestGrammarRules = new List<List<Rule>>(bestGrammars.Count);
            for (int i = 0; i < bestGrammars.Count; i++)
            {
                bestGrammarRules.Add(bestGrammars[i].Grammar.GetRules());
            }

            // Build response
            var response = new GrammarLearningResponse
            {
                Success = actualSuccess,
                LearnedGrammar = null,
                BestLearnedGrammars = bestGrammarRules,
                Metadata = new GrammarLearningMetadata
                {
                    MaxSentenceLength = maxSentenceLength,
                    MinSentenceLength = minWordsInSentences,
                    NumberOfSentences = sentences.Length,
                    NumberOfNonTerminals = numberOfNonterminals,
                    MaxRulesCount = maxRulesCount,
                    MaxDepthBetweenSubSolutions = maxDepthBetweenSubSolutions
                }
            };

            return response;
        }

#pragma warning disable CA2000 // Dispose objects before losing scope
#pragma warning disable CA1859 // Use concrete types when possible for improved performance
        private static IProgress<double> CreateProgressBar(string title)
        {
            return new ShellProgress(new ProgressBar(maxTicks: 100, title));
        }
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
#pragma warning restore CA2000 // Dispose objects before losing scope
    }

    /// <summary>
    /// Progress bar wrapper for IProgress
    /// </summary>
    public class ShellProgress(IProgressBar progressBar) : IProgress<double>, IDisposable
    {
        public void Report(double value) => progressBar.Tick((int)(value * progressBar.MaxTicks));

        public void Dispose() => progressBar.Dispose();
    }
}
