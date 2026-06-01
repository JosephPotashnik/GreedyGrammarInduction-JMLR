// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// The main class responsible for parsing. A chart parser with dynamic programming. See Earley 1970, Stolcke 1995
    /// It holds the current Grammar and an array of Earley Sets (Earley Columns).
    /// each Set/Column in the chart table corresponds to an index of the input.
    /// The parser can also be run as a generator, to generate all possible trees of a grammar (up to input length n)
    /// </summary>
    public class EarleyParser
    {
        public Grammar Grammar;
        protected EarleyColumn[] _table;
        protected Lexicon _lexicon;
        private const int MaximumCompletedStatesInColumn = 50000;
        public static Dictionary<int, Rule> ScannedRules;
        private List<(int, Rule, EarleyColumn, EarleyColumn)> _cachedScannedStates = new List<(int, Rule, EarleyColumn, EarleyColumn)>();
        protected readonly EarleyStatePool _statePool = new EarleyStatePool();
        protected readonly EarleySpanPool _spanPool = new EarleySpanPool();

        private readonly Rule _gammaRule;

        // Cached symbol IDs to avoid dictionary lookups in hot paths
        private readonly int _gammaId;
        private readonly int _startId;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="g"> CFG to be parsed.</param>
        /// <param name="v"> Lexicon class listing all Part-Of-Speech -> token rules. </param>
        /// <param name="text"> The sentence the parser parses. Does not change throughout the lifespan of the parser. </param>
        /// <param name="maxWords"> length of sentence. used when the parser is run as a generator (call to GenerateSentence()). </param>
        public EarleyParser(Grammar g, Lexicon v, string[] text, int maxWords = 0)
        {
            _lexicon = v;
            Grammar = g;
            _table = PrepareEarleyTable(text, maxWords);
            PrepareScannedStates();
            _startId = SymbolTable.Instance.GetId(Grammar.StartSymbol);
            _gammaId = SymbolTable.Instance.GetId(Grammar.GammaSymbol);
            _gammaRule = new Rule(_gammaId, [_startId]);
            var startState = _statePool.Rent(_gammaRule, 0, _table[0]);
            if (g != null)
            {
                _table[0].AddState(startState, Grammar);
            }
        }

        public (bool, byte) ParseSentence(Grammar g, bool skipComputingBasicTrees = false)
        {
            Grammar = g;
            foreach (var (colIndex, rule, startColumn, endColumn) in _cachedScannedStates)
            {
                var rentedState = _statePool.Rent(rule, 1, startColumn);
                rentedState.EndColumn = endColumn;
                _table[colIndex].Reductors.AddState(rentedState, _spanPool);
            }
            var startState = _statePool.Rent(_gammaRule, 0, _table[0]);

            _table[0].AddState(startState, Grammar);
            var res = ParseSentence(skipComputingBasicTrees);
            Reset();
            return res;

        }

        public void Reset()
        {
            //Console.WriteLine("Resetting Earley Parser");
            Grammar = null;
            for (var i = 0; i < _table.Length; i++)
            {
                _table[i].Reset();
            }
        }

        /// <summary>
        /// the main loop, traversing first over completed Items (States) agenda, then over Predicted Item Agenda.
        /// Completed Items Agenda is a priority queue, ordered by decreasing order of Set Start index. See Stolcke 1995.
        /// Predicted Item agenda is a queue with nonterminals such that the nonterminal is the LHS of the list of rules to be predicted.
        /// Epsilon transition causes looping back to completion.
        /// </summary>
        /// <returns> (bool b, int i) such that b is true if parse was accepted. 
        /// Parsing is only rejected in case the numbers of completed Items in a Set exceeds a certain threshold (e.g, 10000)
        /// int i is the number of trees in the parse forest.
        /// /returns>
        public (bool, byte) ParseSentence(bool skipComputingBasicTrees = false)
        {
            bool accepted = true;
            var tableLength = _table.Length;

            // Use for loop instead of foreach for better performance
            for (int colIndex = 0; colIndex < tableLength; colIndex++)
            {
                var col = _table[colIndex];
                var exhaustedCompletion = false;
                while (!exhaustedCompletion)
                {
                    TraverseCompletedStates(col);
                    TraversePredictableStates(col);
                    exhaustedCompletion = col.ActionableCompleteStates.Count == 0;
                }

                if (col.CompletedStateCount > MaximumCompletedStatesInColumn)
                {
                    accepted = false;
                    break;
                }
            }

            if (!accepted)
            {
                // Inline cleanup for failed parsing
                for (int i = 0; i < tableLength; i++)
                {
                    var col = _table[i];
                    while (col.ActionableCompleteStates.Count > 0)
                    {
                        _statePool.Return(col.ActionableCompleteStates.Dequeue());
                    }
                    col.ActionableNonTerminalsToPredict.Clear();
                }
            }

            byte returnCode = 0; //no derivations
            if (HasDerivation())
            {
                returnCode = 1;
            }

            return (accepted, returnCode);
        }

        /// <summary>
        /// Generates all possible trees for the grammar up to input length n. (specified in the constructor)
        /// EarleyGenerator.GetAllSequences() can later be called to get all bracketed representations, or all sequences of parts of speech.
        /// </summary>
        public void GenerateSentence()
        {
            foreach (var (colIndex, rule, startColumn, endColumn) in _cachedScannedStates)
            {
                var rentedState = _statePool.Rent(rule, 1, startColumn);
                rentedState.EndColumn = endColumn;
                _table[colIndex].Reductors.AddState(rentedState, _spanPool);
            }

            foreach (var col in _table)
            {
                var exhaustedCompletion = false;
                while (!exhaustedCompletion)
                {
                    TraverseCompletedStates(col);
                    TraversePredictableStates(col);
                    exhaustedCompletion = col.ActionableCompleteStates.Count == 0;
                }

                int count = CountDerivationsOfLengthK(col.Index);

                if (count > MaximumCompletedStatesInColumn * 2)
                {
                    throw new TooManyEarleyItemsGeneratedException();
                }
            }
        }

        //K = col.Index, i.e., count derivations that span exactly input length k.
        private int CountDerivationsOfLengthK(int k)
        {
            int count = 0;
            if (_table[0].Reductors.TryGetValue(_startId, out var val))
            {
                if (val.TryGetValue(k, out var reductor))
                {
                    if (reductor != null)
                    {
                        var visited = new Dictionary<EarleySpan, int>();
                        count = reductor.CountDerivations(visited);
                    }
                }
            }

            return count;
        }

        protected virtual EarleyColumn[] PrepareEarleyTable(string[] text, int _)
        {
            var sentenceLength = text.Length;
            var table = new EarleyColumn[sentenceLength + 1];
            for (var i = 1; i < table.Length; i++)
            {
                table[i] = new EarleyColumn(i, text[i - 1], sentenceLength, _statePool, _spanPool);
            }

            table[0] = new EarleyColumn(0, "", sentenceLength, _statePool, _spanPool);
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TraversePredictableStates(EarleyColumn col)
        {
            var queue = col.ActionableNonTerminalsToPredict;
            while (queue.Count > 0)
            {
                var nextTerm = queue.Dequeue();
                var ruleList = Grammar.RulesByLHS[nextTerm];
                if (ruleList != null)
                {
                    PredictRules(col, ruleList);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TraverseCompletedStates(EarleyColumn col)
        {
            var heap = col.ActionableCompleteStates;
            while (heap.Count > 0)
            {
                var state = heap.Dequeue();
                Complete(col, state);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PredictRules(EarleyColumn col, List<Rule> ruleList)
        {
            var count = ruleList.Count;
            for (int i = 0; i < count; i++)
            {
                var rule = ruleList[i];
                var newState = _statePool.Rent(rule, 0, col);
                col.AddState(newState, Grammar);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Complete(EarleyColumn col, EarleyState reductorState)
        {
            var startColumn = reductorState.StartColumn;
            var completedSyntacticCategory = reductorState.Rule.LeftHandSide;

            var (reductorSpan, localAmbiguityFound) = startColumn.Reductors.AddState(reductorState, _spanPool);

            // Early return for local ambiguity
            if (localAmbiguityFound)
            {
                return;
            }

            // Create consequent states for all predecessors (array-based lookup)
            var predecessorStates = startColumn.PredecessorsBySymbol[completedSyntacticCategory];
            if (predecessorStates != null)
            {
                var count = predecessorStates.Count;
                for (int i = 0; i < count; i++)
                {
                    var predecessor = predecessorStates[i];
                    var newState = _statePool.Rent(predecessor.Rule, predecessor.DotIndex + 1, predecessor.StartColumn);
                    newState.Predecessor = predecessor;
                    newState.ReductorSpan = reductorSpan;
                    col.AddState(newState, Grammar);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EarleySpan GetCompletedStartNonterminal(int columnIndex = 0)
        {
            if (_table[0].Reductors.TryGetValue(_startId, out var val))
            {
                if (val.TryGetValue(_table.Length - 1 - columnIndex, out var res))
                {
                    return res;
                }
            }
            return null;
        }

        public string[] GetFormattedString(int columnIndex = 0, bool onlyPartsOfSpeechSequences = false)
        {
            var reductorSpan = GetCompletedStartNonterminal(columnIndex);
            if (reductorSpan != null)
            {
                var visitedSpans = new Dictionary<EarleySpan, Color>();
                var visitedStates = new Dictionary<EarleyState, Color>();
                var sbs = reductorSpan.GetFormattedString(Grammar, visitedSpans, visitedStates, onlyPartsOfSpeechSequences);
                var sls = new string[sbs.Count];

                // Use for loop for better performance
                for (int i = 0; i < sls.Length; i++)
                {
                    sls[i] = sbs[i].ToString();
                }
                return sls;
            }
            return Array.Empty<string>();
        }


        public bool HasDerivation()
        {
            return GetCompletedStartNonterminal() != null;
        }

        protected virtual HashSet<int> GetPossibleSyntacticCategoriesForToken(string nextScannableTerm)
        {
            return _lexicon[nextScannableTerm];
        }


        private void PrepareScannedStates()
        {
            HashSet<int> possibleNonTerminalsOFNextScannableTerm = null;
            HashSet<int> possibleNonTerminalsOFNextNextScannableTerm = null;
            var tableLength = _table.Length;

            //generate completed PART-OF-SPEECH -> 'token' for each token in the sentence.
            for (int i = 0; i < tableLength - 1; i++)
            {
                if (i == 0)
                {
                    var nextScannableTerm = _table[i + 1].Token;
                    possibleNonTerminalsOFNextScannableTerm = GetPossibleSyntacticCategoriesForToken(nextScannableTerm);
                }
                else
                {
                    possibleNonTerminalsOFNextScannableTerm = possibleNonTerminalsOFNextNextScannableTerm;
                }

                if (i < tableLength - 2)
                {
                    var nextNextScannableTerm = _table[i + 2].Token;
                    possibleNonTerminalsOFNextNextScannableTerm = GetPossibleSyntacticCategoriesForToken(nextNextScannableTerm);
                }

                if (possibleNonTerminalsOFNextScannableTerm != null)
                {
                    var currentTable = _table[i];
                    var nextTable = _table[i + 1];

                    foreach (var nonTerminalId in possibleNonTerminalsOFNextScannableTerm)
                    {
                        var scannedStateRule = ScannedRules[nonTerminalId];
                        _cachedScannedStates.Add((i, scannedStateRule, currentTable, nextTable));

                    }
                }
            }
        }
    }
}
