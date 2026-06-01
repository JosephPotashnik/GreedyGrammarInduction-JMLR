// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EarleyParserForGreedyGrammarInduction;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// Generates adjacent nodes using canonical addition order (spanning tree).
    /// Only adds rules with index > the last added rule's index, guaranteeing
    /// each grammar hypothesis is reached by exactly one path.
    /// No visited set needed.
    /// </summary>
    public static class CanonicalAdjacencyGenerator
    {
        // Per-thread reusable result list. The caller drains this list synchronously before
        // invoking GetAdjacentsForBFS again on the same thread, so reusing it is safe.
        // The ushort[] entries themselves still escape into the BFS queue and are heap-allocated
        // per adjacent — only the outer list is pooled.
        [ThreadStatic] private static List<(ushort[] NodeSet, ushort AddedRule)> t_adjacentsBuf;
        [ThreadStatic] private static List<ushort> t_adjacentRuleIndicesBuf;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static List<(ushort[] NodeSet, ushort AddedRule)> GetAdjacentsForBFS(
            int maxRuleCount,
            List<Rule> rules,
            ushort[] currentNodeSet,
            LatticeRuleSpace ruleSpace,
            int addedRule)
        {
            var adjacentNodes = t_adjacentsBuf ??= new List<(ushort[] NodeSet, ushort AddedRule)>();
            adjacentNodes.Clear();

            var adjacentRuleIndices = GetAdjacentRuleIndicesForBFS(
                maxRuleCount,
                rules,
                currentNodeSet,
                ruleSpace,
                addedRule);

            for (int i = 0; i < adjacentRuleIndices.Count; i++)
            {
                ushort ruleIndex = adjacentRuleIndices[i];
                adjacentNodes.Add((MaterializeAdjacentNodeSet(currentNodeSet, ruleIndex), ruleIndex));
            }

            return adjacentNodes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static List<ushort> GetAdjacentRuleIndicesForBFS(
            int maxRuleCount,
            List<Rule> rules,
            ushort[] currentNodeSet,
            LatticeRuleSpace ruleSpace,
            int addedRule)
        {
            int minRuleIndex = addedRule;
            (var grammarNTs, int highestIndex) = GrammarExtensions.ComputeNTsAndHighestIndex(rules);
            var adjacentRuleIndices = t_adjacentRuleIndicesBuf ??= new List<ushort>();
            adjacentRuleIndices.Clear();

            if (rules.Count == maxRuleCount)
            {
                return adjacentRuleIndices;
            }

            GenerateAllAdjacentRuleIndices(highestIndex, grammarNTs, currentNodeSet, ruleSpace, adjacentRuleIndices, minRuleIndex);

            return adjacentRuleIndices;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort[] MaterializeAdjacentNodeSet(ushort[] sortedNodeSet, ushort addedRule)
        {
            int pos = Array.BinarySearch(sortedNodeSet, addedRule);
            return MaterializeAdjacentNodeSet(sortedNodeSet, addedRule, pos < 0 ? ~pos : pos);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GenerateAllAdjacentRuleIndices(
            int highestIndex,
            HashSet<int> grammarNTs,
            ushort[] currentNodeSet,
            LatticeRuleSpace ruleSpace,
            List<ushort> adjacentRuleIndices,
            int minRuleIndex)
        {
            int unaryLimit = Math.Min(highestIndex + 1, ruleSpace.NonTerminalsCount);

            // Unary rules: S -> Xi. S is always the start symbol and always reachable — no check.
            for (int i = 0; i < unaryLimit; i++)
            {
                ushort ruleIndex = ruleSpace.GetUnaryRuleIndex(i);
                if (ruleIndex <= minRuleIndex) continue;
                int pos = Array.BinarySearch(currentNodeSet, ruleIndex);
                if (pos < 0)
                {
                    adjacentRuleIndices.Add(ruleIndex);
                }
            }

            for (int i = 0; i < highestIndex; i++)
            {
                if (!grammarNTs.Contains(ruleSpace.NonTerminalIds[i]))
                    continue;
                GenerateBinaryRuleIndices(i, highestIndex, currentNodeSet, ruleSpace, adjacentRuleIndices, minRuleIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GenerateBinaryRuleIndices(
            int i,
            int highestIndex,
            ushort[] currentNodeSet,
            LatticeRuleSpace ruleSpace,
            List<ushort> adjacentRuleIndices,
            int minRuleIndex)
        {
            int limit = Math.Min(highestIndex + 1, ruleSpace.NonTerminalsCount);

            for (int j = 0; j < limit; j++)
            {
                for (int k = 0; k < limit; k++)
                {
                    ushort ruleIndex = ruleSpace.GetBinaryRuleIndex(i, j, k);
                    if (ruleIndex <= minRuleIndex) continue;
                    int pos = Array.BinarySearch(currentNodeSet, ruleIndex);
                    if (pos < 0)
                    {
                        adjacentRuleIndices.Add(ruleIndex);
                    }
                }
            }

            if (highestIndex + 1 < ruleSpace.NonTerminalsCount)
            {
                ushort ruleIndex = ruleSpace.GetBinaryRuleIndex(i, highestIndex, highestIndex + 1);
                if (ruleIndex > minRuleIndex)
                {
                    int pos = Array.BinarySearch(currentNodeSet, ruleIndex);
                    if (pos < 0)
                    {
                        adjacentRuleIndices.Add(ruleIndex);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort[] MaterializeAdjacentNodeSet(
            ushort[] sortedNodeSet,
            ushort addedRule,
            int insertAt)
        {
            var newSet = new ushort[sortedNodeSet.Length + 1];
            Array.Copy(sortedNodeSet, 0, newSet, 0, insertAt);
            newSet[insertAt] = addedRule;
            Array.Copy(sortedNodeSet, insertAt, newSet, insertAt + 1, sortedNodeSet.Length - insertAt);
            return newSet;
        }

    }
}
