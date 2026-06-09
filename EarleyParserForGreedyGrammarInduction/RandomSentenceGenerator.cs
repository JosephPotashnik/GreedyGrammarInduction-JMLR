// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// Generates random sentences of exact length k from a context-free grammar.
    /// Uses CKY-style dynamic programming to first compute which (nonterminal, length) pairs
    /// are derivable, then performs a random walk through valid derivations.
    /// </summary>
    public class RandomSentenceGenerator
    {
        private readonly Grammar _grammar;
        private readonly Lexicon _lexicon;
        private readonly Random _random;

        // DP table: derivable[ntId][length] = true if nonterminal ntId can derive a string of exactly 'length' terminals
        private readonly Dictionary<int, HashSet<int>> _derivable;

        // Maximum length we've computed derivability for
        private int _maxComputedLength;

        public RandomSentenceGenerator(Grammar grammar, Lexicon lexicon, int? seed = null)
        {
            _grammar = grammar;
            _lexicon = lexicon;
            _random = seed.HasValue ? new Random(seed.Value) : new Random();
            _derivable = new Dictionary<int, HashSet<int>>();
            _maxComputedLength = 0;
        }

        /// <summary>
        /// Ensures the DP table is computed up to the given length.
        /// </summary>
        private void EnsureComputedUpTo(int maxLength)
        {
            if (maxLength <= _maxComputedLength)
                return;

            // Initialize derivable sets for all nonterminals if not already done
            foreach (var ruleList in _grammar.RulesByLHS)
            {
                if (ruleList == null) continue;
                foreach (var rule in ruleList)
                {
                    if (!_derivable.ContainsKey(rule.LeftHandSide))
                        _derivable[rule.LeftHandSide] = new HashSet<int>();

                    foreach (var rhs in rule.RightHandSide)
                    {
                        if (!_derivable.ContainsKey(rhs))
                            _derivable[rhs] = new HashSet<int>();
                    }
                }
            }

            // Base case: POS (parts of speech) can derive strings of length 1
            foreach (var pos in _lexicon.POSWithPossibleWords.Keys)
            {
                if (!_derivable.ContainsKey(pos))
                    _derivable[pos] = new HashSet<int>();
                _derivable[pos].Add(1);
            }

            // Fixed-point iteration to compute derivability
            // General approach: for each symbol, if it's a POS it contributes {1}, otherwise its derivable lengths
            bool changed = true;
            while (changed)
            {
                changed = false;

                foreach (var ruleList in _grammar.RulesByLHS)
                {
                    if (ruleList == null) continue;

                    foreach (var rule in ruleList)
                    {
                        int lhs = rule.LeftHandSide;
                        int[] rhs = rule.RightHandSide;

                        // Get all possible lengths for this rule by combining RHS symbol lengths
                        var possibleLengths = ComputeRuleLengths(rhs, maxLength);

                        foreach (var len in possibleLengths)
                        {
                            if (_derivable[lhs].Add(len))
                                changed = true;
                        }
                    }
                }
            }

            _maxComputedLength = maxLength;
        }

        /// <summary>
        /// Computes all possible lengths derivable from a rule's RHS.
        /// Each symbol on RHS contributes either {1} if POS, or its derivable lengths if nonterminal.
        /// </summary>
        private HashSet<int> ComputeRuleLengths(int[] rhs, int maxLength)
        {
            // Start with length 0 (will accumulate as we process each symbol)
            var currentLengths = new HashSet<int> { 0 };

            foreach (var symbol in rhs)
            {
                var symbolLengths = GetSymbolLengths(symbol);
                if (symbolLengths.Count == 0)
                {
                    // This symbol can't derive anything yet, so the whole rule can't derive anything
                    return new HashSet<int>();
                }

                var newLengths = new HashSet<int>();
                foreach (var currentLen in currentLengths)
                {
                    foreach (var symbolLen in symbolLengths)
                    {
                        int totalLen = currentLen + symbolLen;
                        if (totalLen <= maxLength)
                        {
                            newLengths.Add(totalLen);
                        }
                    }
                }
                currentLengths = newLengths;

                if (currentLengths.Count == 0)
                    break;
            }

            return currentLengths;
        }

        /// <summary>
        /// Gets the derivable lengths for a symbol.
        /// POS symbols always derive length 1. Nonterminals use the DP table.
        /// </summary>
        private HashSet<int> GetSymbolLengths(int symbol)
        {
            if (_lexicon.ContainsPOS(symbol))
            {
                return new HashSet<int> { 1 };
            }

            if (_derivable.TryGetValue(symbol, out var lengths))
            {
                return lengths;
            }

            return new HashSet<int>();
        }

        /// <summary>
        /// Checks if a nonterminal can derive a string of exactly the given length.
        /// </summary>
        public bool CanDerive(int nonterminal, int length)
        {
            EnsureComputedUpTo(length);
            bool result = _derivable.TryGetValue(nonterminal, out var lengths) && lengths.Contains(length);
            return result;
        }

        
        /// <summary>
        /// Generates a random sentence of exactly the given length from the start symbol.
        /// Returns null if no sentence of that length can be generated.
        /// </summary>
        public string[] GenerateRandomSentence(int length)
        {
            int startSymbol = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);

            if (!CanDerive(startSymbol, length))
                return null;

            var result = new List<string>();
            GenerateFromNonterminal(startSymbol, length, result);
            return result.ToArray();
        }

        /// <summary>
        /// Recursively generates a random derivation from a nonterminal to exactly 'length' terminals.
        /// </summary>
        private void GenerateFromNonterminal(int nonterminal, int length, List<string> result)
        {
            // Base case: if this is a POS, pick a random word
            if (_lexicon.ContainsPOS(nonterminal))
            {
                if (length != 1)
                    throw new InvalidOperationException($"POS {nonterminal} can only derive length 1, not {length}");

                var words = _lexicon.POSWithPossibleWords[nonterminal];
                var wordArray = new string[words.Count];
                words.CopyTo(wordArray);
                result.Add(wordArray[_random.Next(wordArray.Length)]);
                return;
            }

            // Find all applicable rules that can derive the required length
            var applicableRules = new List<(Rule rule, int[] splits)>();

            var rules = _grammar.RulesByLHS[nonterminal];
            if (rules == null)
                throw new InvalidOperationException($"No rules for nonterminal {nonterminal}");

            foreach (var rule in rules)
            {
                int[] rhs = rule.RightHandSide;

                // Find all valid length splits for this rule
                var validSplits = FindValidSplits(rhs, length);
                foreach (var split in validSplits)
                {
                    applicableRules.Add((rule, split));
                }
            }

            if (applicableRules.Count == 0)
                throw new InvalidOperationException($"No applicable rules for {nonterminal} at length {length}");

            // Pick a random applicable rule/split
            var (chosenRule, chosenSplits) = applicableRules[_random.Next(applicableRules.Count)];

            // Recursively generate from each RHS symbol
            for (int i = 0; i < chosenRule.RightHandSide.Length; i++)
            {
                int symbol = chosenRule.RightHandSide[i];
                int symbolLength = chosenSplits[i];

                if (_lexicon.ContainsPOS(symbol))
                {
                    // POS: pick a random word
                    var words = _lexicon.POSWithPossibleWords[symbol];
                    var wordArray = new string[words.Count];
                    words.CopyTo(wordArray);
                    result.Add(wordArray[_random.Next(wordArray.Length)]);
                }
                else
                {
                    // Nonterminal: recurse
                    GenerateFromNonterminal(symbol, symbolLength, result);
                }
            }
        }

        /// <summary>
        /// Finds all valid ways to split a target length among the RHS symbols.
        /// </summary>
        private List<int[]> FindValidSplits(int[] rhs, int targetLength)
        {
            var results = new List<int[]>();
            FindValidSplitsRecursive(rhs, 0, targetLength, new int[rhs.Length], results);
            return results;
        }

        private void FindValidSplitsRecursive(int[] rhs, int index, int remainingLength, int[] currentSplit, List<int[]> results)
        {
            if (index == rhs.Length)
            {
                if (remainingLength == 0)
                {
                    results.Add((int[])currentSplit.Clone());
                }
                return;
            }

            int symbol = rhs[index];
            var symbolLengths = GetSymbolLengths(symbol);

            foreach (var len in symbolLengths)
            {
                if (len <= remainingLength)
                {
                    currentSplit[index] = len;
                    FindValidSplitsRecursive(rhs, index + 1, remainingLength - len, currentSplit, results);
                }
            }
        }

        /// <summary>
        /// Generates multiple random sentences of the given length.
        /// </summary>
        public List<string[]> GenerateRandomSentences(int length, int count)
        {
            var sentences = new List<string[]>();
            for (int i = 0; i < count; i++)
            {
                var sentence = GenerateRandomSentence(length);
                if (sentence != null)
                    sentences.Add(sentence);
            }
            return sentences;
        }

        /// <summary>
        /// Generates random sentences with lengths in the given range [minLength, maxLength].
        /// </summary>
        public List<string[]> GenerateRandomSentencesInRange(int minLength, int maxLength, int countPerLength)
        {
            var sentences = new List<string[]>();
            for (int len = minLength; len <= maxLength; len++)
            {
                for (int i = 0; i < countPerLength; i++)
                {
                    var sentence = GenerateRandomSentence(len);
                    if (sentence != null)
                        sentences.Add(sentence);
                }
            }
            return sentences;
        }
    }
}
