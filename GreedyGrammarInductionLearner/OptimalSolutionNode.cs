// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using System.Threading;

using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner.SearchSpace;
using static GreedyGrammarInductionLearner.ArrayCompressor;

namespace GreedyGrammarInductionLearner
{
    // Comparer for HashSet<ushort> to enable pooling
    public class HashSetComparer<T> : IEqualityComparer<HashSet<T>>
    {
        public bool Equals(HashSet<T> x, HashSet<T> y)
        {
            if (x == null && y == null)
            {
                return true;
            }

            if (x == null || y == null)
            {
                return false;
            }

            return x.SetEquals(y);
        }

        public int GetHashCode(HashSet<T> obj)
        {
            if (obj == null)
            {
                return 0;
            }

            int hash = 0;
            foreach (var item in obj)
            {
                hash ^= item.GetHashCode();
            }
            return hash;
        }
    }

    // Comparer for List<CompressionRange> to enable pooling
    public class ListComparer<T> : IEqualityComparer<List<T>> where T : IEquatable<T>
    {
        public bool Equals(List<T> x, List<T> y)
        {
            if (x == null && y == null)
            {
                return true;
            }

            if (x == null || y == null)
            {
                return false;
            }

            if (x.Count != y.Count)
            {
                return false;
            }

            for (int i = 0; i < x.Count; i++)
            {
                if (!x[i].Equals(y[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(List<T> obj)
        {
            if (obj == null)
            {
                return 0;
            }

            var hc = new HashCode();
            for (int i = 0; i < obj.Count; i++)
            {
                hc.Add(obj[i]);
            }
            return hc.ToHashCode();
        }
    }
}

namespace GreedyGrammarInductionLearner
{
    public class OptimalSolutionNode
    {
        // Static pools for object reuse to reduce memory footprint
        private static readonly HashSet<HashSet<ushort>> _posAssignmentPool = new HashSet<HashSet<ushort>>(new HashSetComparer<ushort>());
        private static readonly HashSet<List<CompressionRange>> _parsedListPool = new HashSet<List<CompressionRange>>(new ListComparer<CompressionRange>());

        public List<double> ParsedRatio { get; set; }
        public List<List<CompressionRange>> Parsed { get; set; }
        public List<HashSet<ushort>> POSAssignments { get; set; }
        public List<double> Lambdas { get; set; }
        public List<int[]> Horizons { get; set; }
        public HashSet<OptimalSolutionNode> ParentsInLocalOptimumList { get; set; }
        public List<int> IndexOfParsedSetInParent { get; set; }
        public ushort[] Set { get; set; }
        public ushort Depth { get; set; }
        public bool Printed { get; set; }
        public int Index { get; set; }
        private BannedPOSChain _bubbledBans;

        public BannedPOSChain BubbledBans => Volatile.Read(ref _bubbledBans);

        public OptimalSolutionNode(ushort[] sortedSet)
        {
            Set = sortedSet;
            Depth = (ushort)sortedSet.Length;

            Parsed = new List<List<CompressionRange>>();
            ParsedRatio = new List<double>();
            Lambdas = new List<double>();
            Horizons = new List<int[]>();

            POSAssignments = new List<HashSet<ushort>>();
            ParentsInLocalOptimumList = new HashSet<OptimalSolutionNode>();
            IndexOfParsedSetInParent = new List<int>();

        }
        public OptimalSolutionNode(List<CompressionRange> parsed, ushort[] set, ushort depth, HashSet<Rule> posMappings, double parsedRatio, double lambda, ReadOnlySpan<int> horizon, int indexOfParsedSetInParent, LatticeRuleSpace ruleSpace)
        {
            POSAssignments = new List<HashSet<ushort>>();

            // Create POS assignment HashSet and check if equivalent exists in pool
            var hs1 = new HashSet<ushort>();
            foreach (var item in posMappings)
            {
                hs1.Add(ruleSpace.POSAssignmentDictionary[item]);
            }

            // Try to get existing equivalent from pool, otherwise add new one
            if (_posAssignmentPool.TryGetValue(hs1, out var existingPosSet))
            {
                POSAssignments.Add(existingPosSet);
            }
            else
            {
                _posAssignmentPool.Add(hs1);
                POSAssignments.Add(hs1);
            }

            // Create parsed list and check if equivalent exists in pool
            var parsedCopy = new List<CompressionRange>(parsed);
            if (_parsedListPool.TryGetValue(parsedCopy, out var existingParsedList))
            {
                Parsed = [existingParsedList];
            }
            else
            {
                _parsedListPool.Add(parsedCopy);
                Parsed = [parsedCopy];
            }
            IndexOfParsedSetInParent = [indexOfParsedSetInParent];
            ParentsInLocalOptimumList = new HashSet<OptimalSolutionNode>();
            ParsedRatio = [parsedRatio];
            Lambdas = [lambda];
            Horizons = [horizon.ToArray()];
            Set = set;
            Depth = depth;
        }

        public void MergeBans(BannedPOSChain bans)
        {
            if (bans == null) return;

            while (true)
            {
                var current = Volatile.Read(ref _bubbledBans);
                var merged = BannedPOSChain.MergeCompact(bans, current);
                if (Interlocked.CompareExchange(ref _bubbledBans, merged, current) == current)
                    return;
            }
        }

        public void Add(OptimalSolutionNode optimalSolutionNode)
        {
            var newPosMapping = optimalSolutionNode.POSAssignments[0];

            // Check if the new mapping is a superset of any existing mapping.
            foreach (var existingPosMapping in POSAssignments)
            {
                if (newPosMapping.IsSupersetOf(existingPosMapping))
                {
                    return; // The new mapping is redundant, so do nothing.
                }
            }

            // Remove any existing mappings that are supersets of the new mapping.
            var indicesToRemove = new List<int>();
            for (int i = 0; i < POSAssignments.Count; i++)
            {
                if (POSAssignments[i].IsSupersetOf(newPosMapping))
                {
                    indicesToRemove.Add(i);
                }
            }

            for (int i = indicesToRemove.Count - 1; i >= 0; i--)
            {
                int index = indicesToRemove[i];
                POSAssignments.RemoveAt(index);
                Parsed.RemoveAt(index);
                ParsedRatio.RemoveAt(index);
                Lambdas.RemoveAt(index);
                Horizons.RemoveAt(index);
                IndexOfParsedSetInParent.RemoveAt(index);
            }

            // Add the new, more general mapping.
            POSAssignments.Add(newPosMapping);
            Parsed.Add(optimalSolutionNode.Parsed[0]);
            ParsedRatio.Add(optimalSolutionNode.ParsedRatio[0]);
            Lambdas.Add(optimalSolutionNode.Lambdas[0]);
            Horizons.Add(optimalSolutionNode.Horizons[0]);
            IndexOfParsedSetInParent.Add(optimalSolutionNode.IndexOfParsedSetInParent[0]);
        }

        internal bool RemoveParetoPrunedIncompleteAlternatives(
            (int depth, double lambda)[] paretoSnapshot,
            double paretoThickness)
        {
            bool removed = false;
            int depth = Set.Length;
            for (int i = Lambdas.Count - 1; i >= 0; i--)
            {
                if (ParsedCoversAllEvidence(Parsed[i]))
                    continue;

                if (!ParetoManager.ShouldPrune(paretoSnapshot, paretoThickness, depth, Lambdas[i]))
                    continue;

                RemoveAlternativeAt(i);
                removed = true;
            }

            return removed;
        }

        private static bool ParsedCoversAllEvidence(List<CompressionRange> parsedCompressed)
        {
            return parsedCompressed != null && parsedCompressed.Count == 0;
        }

        private void RemoveAlternativeAt(int index)
        {
            POSAssignments.RemoveAt(index);
            Parsed.RemoveAt(index);
            ParsedRatio.RemoveAt(index);
            Lambdas.RemoveAt(index);
            Horizons.RemoveAt(index);
            IndexOfParsedSetInParent.RemoveAt(index);
        }

        public List<ContextFreeGrammar> GetGrammars(LatticeRuleSpace ruleSpace)
        {
            List<ContextFreeGrammar> grammars = new List<ContextFreeGrammar>();
            var coreRules = ruleSpace[Set];

            foreach (var posAssignments in POSAssignments)
            {
                HashSet<Rule> POSMappings = new HashSet<Rule>();
                foreach (var index in posAssignments)
                {
                    POSMappings.Add(ruleSpace.POSAssignmentRules[index]);
                }

                var allRules = new List<Rule>(coreRules.Count + POSMappings.Count);
                allRules.AddRange(coreRules);
                allRules.AddRange(POSMappings);
                var grammar = new ContextFreeGrammar(allRules);
                grammars.Add(grammar);
            }
            return grammars;
        }
    }
}
