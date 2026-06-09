// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using System.Text;
using EarleyParserForGreedyGrammarInduction;

namespace GreedyGrammarInductionLearner;

internal class ParetoManager
{
    // 0.05 means we keep things within 5% of the frontier's best values
    private readonly double _thickness;
    public double Thickness => _thickness;

    public ParetoManager(double thickness = 0.0)
    {
        _thickness = thickness;
    }
    private volatile int _version;
    public int Version => _version;

    public List<(ushort[] x, int depth, double lambda)> ParetoFront = new List<(ushort[] x, int depth, double lambda)>();

    public bool ShouldPrune(int depth, double lambda)
    {
        foreach (var global in ParetoFront)
        {
            // We only prune if the Global is better or equal than the fragment, allowing for a margin of _thickness (the ribbon).
            // PLUS the allowed margin (the ribbon).
            bool rulesBetterOrEqual = global.depth * (1 + _thickness) <= depth;
            bool lambdaBetterOrEqual = global.lambda * (1 + _thickness) <= lambda;

            if (rulesBetterOrEqual && lambdaBetterOrEqual)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Creates a lightweight snapshot of the Pareto front for lock-free ShouldPrune checks.
    /// Only copies (depth, lambda) - the ushort[] sets are not needed for pruning decisions.
    /// </summary>
    public (int depth, double lambda)[] CreateSnapshot()
    {
        var snapshot = new (int depth, double lambda)[ParetoFront.Count];
        for (int i = 0; i < ParetoFront.Count; i++)
            snapshot[i] = (ParetoFront[i].depth, ParetoFront[i].lambda);
        return snapshot;
    }

    /// <summary>
    /// Lock-free ShouldPrune against a pre-computed snapshot. Same logic as the instance method.
    /// </summary>
    public static bool ShouldPrune((int depth, double lambda)[] snapshot, double thickness, int candidateDepth, double candidateLambda)
    {
        for (int i = 0; i < snapshot.Length; i++)
        {
            bool rulesBetterOrEqual = snapshot[i].depth * (1 + thickness) <= candidateDepth;
            bool lambdaBetterOrEqual = snapshot[i].lambda * (1 + thickness) <= candidateLambda;

            if (rulesBetterOrEqual && lambdaBetterOrEqual)
            {
                return true;
            }
        }
        return false;
    }

    public static bool TryGetPruneLambdaThreshold((int depth, double lambda)[] snapshot, double thickness, int candidateDepth, out double threshold)
    {
        threshold = double.PositiveInfinity;
        bool found = false;
        double multiplier = 1 + thickness;

        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].depth * multiplier <= candidateDepth)
            {
                double candidateThreshold = snapshot[i].lambda * multiplier;
                if (candidateThreshold < threshold)
                    threshold = candidateThreshold;
                found = true;
            }
        }

        return found;
    }

    public void UpdateFrontier(ushort[] newCandidate, int depth, double lambda, HashSet<ushort[]> globalOptimalSolutions)
    {
        if (_thickness == 0.0)
        {
            // Exact front: reject exact duplicates (same depth and lambda)
            bool anyDominatesOrEqual = false;
            bool exactDuplicate = false;
            for (int i = 0; i < ParetoFront.Count; i++)
            {
                var w = ParetoFront[i];
                if (w.depth <= depth && w.lambda <= lambda)
                {
                    anyDominatesOrEqual = true;
                    if (w.depth == depth && w.lambda == lambda)
                    {
                        exactDuplicate = true;
                        break;
                    }
                }
            }
            if (anyDominatesOrEqual && exactDuplicate) return;

            // Remove dominated solutions
            for (int i = ParetoFront.Count - 1; i >= 0; i--)
            {
                var w = ParetoFront[i];
                if (depth <= w.depth && lambda <= w.lambda)
                {
                    globalOptimalSolutions.Remove(w.x);
                    ParetoFront.RemoveAt(i);
                }
            }
        }

        // When thickness > 0: keep all globals - no rejection, no removal.
        // The ribbon controls search pruning (ShouldPrune) but not which solutions to keep.

        ParetoFront.Add((newCandidate, depth, lambda));
        globalOptimalSolutions.Add(newCandidate);
        _version++;
    }
}
