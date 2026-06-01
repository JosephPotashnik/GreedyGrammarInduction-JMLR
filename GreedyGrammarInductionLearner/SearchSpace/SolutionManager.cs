// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;


namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// Manages optimal solutions, testing, and cleanup operations
    /// </summary>
    public static class SolutionManager
    {

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddParentIfExists(OptimalSolutionNode node, OptimalSolutionNode parent)
        {
            if (parent != null)
            {
                node.ParentsInLocalOptimumList.Add(parent);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OptimalSolutionNode InsertOptimalSolution(
    ref long localOptimumCount,
    in SearchContext context,
    List<(OptimalSolutionNode, int)> optimalNodes,
    in SolutionData solution)
        {
            lock (context.OptimalSolutionsTracker)
            {
                //  should prune (in the case of race conditions between two optimal nodes)
                bool shouldPrune = context.OptimalSolutionsTracker.ShouldPrune(solution.CurrentNodeSet.Length, solution.Lambda);
                if (shouldPrune)
                { 
                    return null;
                }

                // Check if solution already exists
                bool existingSolution = context.OptimalSolutionsTracker.OptimalSolutionsMap
                    .TryGetValue(solution.CurrentNodeSet, out var existingOptimalSolution);

                // Early exit if strongly equivalent grammar exists
                if (!existingSolution)
                {
                    bool stronglyEquivalent = solution.CanonicalCandidate != null
                        ? context.OptimalSolutionsTracker.TestForOtherStronglyEquivalentGrammar(solution.Parent, solution.CanonicalCandidate, context.RuleSpace)
                        : context.OptimalSolutionsTracker.TestForOtherStronglyEquivalentGrammar(solution.Parent, solution.Grammar);
                    if (stronglyEquivalent)
                    {
                        return null;
                    }
                }

                if (existingSolution)
                {
                    // Case 2: Solution exists
                    foreach (var p in existingOptimalSolution.POSAssignments)
                    {
                        if (solution.POSIndices.IsSupersetOf(p))
                        {
                            return null;
                        }
                    }
                }

                var optimalSolutionNode = new OptimalSolutionNode(solution.Parsed, solution.CurrentNodeSet, (ushort)solution.CurrentNodeSet.Length, solution.POSMappings, solution.ParsedRatio, solution.Lambda, solution.GrammarShapeVector, solution.IndexParsedInParent, context.RuleSpace);

                // Handle the three cases for inserting the solution
                OptimalSolutionNode targetNode;

                if (!existingSolution)
                {
                    // Case 1: First solution for this search path
                    Interlocked.Increment(ref localOptimumCount);
                    AddParentIfExists(optimalSolutionNode, solution.Parent);
                    // Add the new optimal solution to the tracker!
                    context.OptimalSolutionsTracker.AddLocalOptimalSolution(optimalSolutionNode, solution.CurrentNodeSet);
                    targetNode = optimalSolutionNode;
                }
                else
                {
                    // Case 2: Solution exists
                    existingOptimalSolution.Add(optimalSolutionNode);
                    AddParentIfExists(existingOptimalSolution, solution.Parent);
                    targetNode = existingOptimalSolution;
                }

                // Register and track the solution
                if (solution.CanonicalCandidate != null)
                    context.OptimalSolutionsTracker.RegisterCanonicalCandidate(solution.CanonicalCandidate, targetNode, solution.POSIndices);
                else
                    context.OptimalSolutionsTracker.RegisterCanonicalMatrix(solution.Grammar, targetNode);

                optimalNodes.Add((targetNode, targetNode.Parsed.Count - 1));

                // Check for global optimum
                bool foundGlobal = OptimalSolutionsTracker.CheckGlobalOptimum(solution.ParsedRatio);
                if (foundGlobal)
                {
                    context.OptimalSolutionsTracker.AddGlobalOptimum(optimalSolutionNode.Set, solution.Lambda);
                }

                return targetNode;
            }
        }

        public static bool TestDiscardSolution(
            int maxDepthBetweenSubSolutions,
            long shortestDepthOfGlobalOptimum,
            int depth,
            List<double> parsedRatio)
        {
            if (shortestDepthOfGlobalOptimum != long.MaxValue && depth - shortestDepthOfGlobalOptimum >= maxDepthBetweenSubSolutions - 1)
            {
                // In the deepest frontier, allow only global optima.
                bool foundGlobal = OptimalSolutionsTracker.CheckGlobalOptimum(parsedRatio);
                return !foundGlobal;
            }
            return false;
        }
    }
}
