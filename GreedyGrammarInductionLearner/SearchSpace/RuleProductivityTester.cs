// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EarleyParserForGreedyGrammarInduction;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// Tests rule productivity for optimization decisions
    /// </summary>
    public static class RuleProductivityTester
    {
        // ── Per-thread scratch pools ──────────────────────────────────────────
        // Cleared on acquisition; all transient, never escape beyond the containing call.
        [ThreadStatic] private static HashSet<int> t_scratchProductive;
        [ThreadStatic] private static Rule[] t_scratchAddedRules;
        [ThreadStatic] private static HashSet<int> t_scratchLhsSet;
        [ThreadStatic] private static Dictionary<int, HashSet<int>> t_scratchAssignmentsPerNT;
        [ThreadStatic] private static Stack<HashSet<int>> t_intHashSetPool;
        [ThreadStatic] private static HashSet<int> t_scratchNonterminals;
        [ThreadStatic] private static Dictionary<int, int> t_scratchMinLengths;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static HashSet<int> RentIntHashSet()
        {
            var pool = t_intHashSetPool ??= new Stack<HashSet<int>>();
            if (pool.Count > 0) { var s = pool.Pop(); s.Clear(); return s; }
            return new HashSet<int>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnIntHashSet(HashSet<int> s)
        {
            (t_intHashSetPool ??= new Stack<HashSet<int>>()).Push(s);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Rule[] RentAddedRulesBuf(int required)
        {
            var buf = t_scratchAddedRules;
            if (buf == null || buf.Length < required)
            {
                buf = new Rule[Math.Max(required, 16)];
                t_scratchAddedRules = buf;
            }
            return buf;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void TestProductiveRuleOfNonOptimalNodes(OptimalSolutionNode currentRoot, List<ushort> indices, ref bool attemptAdjacents, LatticeRuleSpace ruleSpace)
        {
            var parentRules = ruleSpace[currentRoot.Set];
            TestProductiveRuleOfNonOptimalNodes(parentRules, indices, ref attemptAdjacents, ruleSpace);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void TestProductiveRuleOfNonOptimalNodes(List<Rule> parentRules, List<ushort> indices, ref bool attemptAdjacents, LatticeRuleSpace ruleSpace)
        {
            if (indices.Count == 0)
                return;

            // Start with all nonterminals from the root as known-productive.
            var productive = t_scratchProductive ??= new HashSet<int>();
            productive.Clear();
            foreach (var r in parentRules)
            {
                productive.Add(r.LeftHandSide);
                foreach (var rhs in r.RightHandSide)
                    productive.Add(rhs);
            }

            // Collect added rules into a pooled buffer.
            int indicesCount = indices.Count;
            var addedRules = RentAddedRulesBuf(indicesCount);
            for (int i = 0; i < indicesCount; i++)
                addedRules[i] = ruleSpace.GetRuleFromIndex(indices[i]);

            // Fixpoint: propagate productivity through added rules.
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < indicesCount; i++)
                {
                    var rule = addedRules[i];
                    if (productive.Contains(rule.LeftHandSide))
                        continue;

                    bool allRhsProductive = true;
                    foreach (var rhs in rule.RightHandSide)
                    {
                        if (!productive.Contains(rhs))
                        {
                            allRhsProductive = false;
                            break;
                        }
                    }

                    if (allRhsProductive)
                    {
                        productive.Add(rule.LeftHandSide);
                        changed = true;
                    }
                }
            }

            // Check which added rules still have non-productive RHS nonterminals
            var foundNonProductiveUse = false;
            for (int i = 0; i < indicesCount; i++)
            {
                var rule = addedRules[i];
                foreach (var rhs in rule.RightHandSide)
                {
                    if (!productive.Contains(rhs))
                    {
                        foundNonProductiveUse = true;
                        break;
                    }
                }
                if (foundNonProductiveUse) break;
            }

            // All rules are productive and the fitness is not optimal, this means we can never get optimal
            // fitness for any solution which includes this solution, do not search adjacents.
            if (!foundNonProductiveUse)
                attemptAdjacents = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsStrictlyRHSNTNonProductive(HashSet<int> strictlyRHSNonterminals, HashSet<Rule> totalAssignments)
        {
            var lhsSet = t_scratchLhsSet ??= new HashSet<int>();
            lhsSet.Clear();
            foreach (var rule in totalAssignments)
            {
                lhsSet.Add(rule.LeftHandSide);
            }

            // Check if any strictly RHS non-terminal has a POS assignment
            foreach (var nt in strictlyRHSNonterminals)
            {
                if (!lhsSet.Contains(nt))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (bool isProductive, Dictionary<int, int> minLengths) CheckProductivity(List<Rule> candidateRules)
        {
            var nonterminals = t_scratchNonterminals ??= new HashSet<int>();
            CollectAllNonterminals(candidateRules, nonterminals);

            var minLengths = t_scratchMinLengths ??= new Dictionary<int, int>();
            CalculateMinLengths(candidateRules, nonterminals, minLengths);

            foreach (var nt in nonterminals)
            {
                if (Grammar.PartsOfSpeech.Contains(nt))
                {
                    continue;
                }

                if (!minLengths.TryGetValue(nt, out var minLength) || minLength == int.MaxValue)
                {
                    return (false, null);
                }
            }

            return (true, minLengths);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CollectAllNonterminals(List<Rule> rules, HashSet<int> nonterminals)
        {
            nonterminals.Clear();
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                nonterminals.Add(rule.LeftHandSide);

                var rhs = rule.RightHandSide;
                for (int j = 0; j < rhs.Length; j++)
                {
                    nonterminals.Add(rhs[j]);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateMinLengths(
            List<Rule> rules,
            HashSet<int> nonterminals,
            Dictionary<int, int> minLengths)
        {
            minLengths.Clear();

            foreach (var nt in nonterminals)
            {
                minLengths[nt] = Grammar.PartsOfSpeech.Contains(nt) ? 1 : int.MaxValue;
            }

            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    int bodyLength = GetSymbolSequenceLength(rule.RightHandSide, minLengths);

                    if (bodyLength != int.MaxValue && bodyLength < minLengths[rule.LeftHandSide])
                    {
                        minLengths[rule.LeftHandSide] = bodyLength;
                        changed = true;
                    }
                }
            } while (changed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetSymbolSequenceLength(int[] rightHandSide, Dictionary<int, int> minLengths)
        {
            int totalLength = 0;
            for (int i = 0; i < rightHandSide.Length; i++)
            {
                int symbolLength = GetSymbolLength(rightHandSide[i], minLengths);
                if (symbolLength == int.MaxValue)
                {
                    return int.MaxValue;
                }

                totalLength += symbolLength;
            }

            return totalLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetSymbolLength(int symbol, Dictionary<int, int> minLengths)
        {
            if (Grammar.PartsOfSpeech.Contains(symbol))
            {
                return 1;
            }

            return minLengths.TryGetValue(symbol, out var length) ? length : int.MaxValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreThereRedundantPOSRules(Grammar currentHypothesis, HashSet<Rule> totalAssignments, HashSet<int> strictlyRHSNts)
        {
            return AreThereRedundantPOSRules(currentHypothesis, totalAssignments, null, strictlyRHSNts);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreThereRedundantPOSRules(
            Grammar currentHypothesis,
            HashSet<Rule> newAssignments,
            HashSet<Rule> previousAssignments,
            HashSet<int> strictlyRHSNts)
        {
            var assignmentsPerNT = BuildAssignmentsPerNt(newAssignments, previousAssignments);
            return AreThereRedundantPOSRules(currentHypothesis, assignmentsPerNT, strictlyRHSNts);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreThereRedundantPOSRules(
            List<Rule> candidateRules,
            HashSet<Rule> newAssignments,
            HashSet<Rule> previousAssignments,
            HashSet<int> strictlyRHSNts)
        {
            var assignmentsPerNT = BuildAssignmentsPerNt(newAssignments, previousAssignments);
            return AreThereRedundantPOSRules(candidateRules, assignmentsPerNT, strictlyRHSNts);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreThereRedundantPOSRules(
            Grammar currentHypothesis,
            ushort[] newAssignmentIndices,
            HashSet<Rule> previousAssignments,
            HashSet<int> strictlyRHSNts,
            LatticeRuleSpace ruleSpace)
        {
            var assignmentsPerNT = BuildAssignmentsPerNt(newAssignmentIndices, previousAssignments, ruleSpace);
            return AreThereRedundantPOSRules(currentHypothesis, assignmentsPerNT, strictlyRHSNts);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreThereRedundantPOSRules(
            List<Rule> candidateRules,
            ushort[] newAssignmentIndices,
            HashSet<Rule> previousAssignments,
            HashSet<int> strictlyRHSNts,
            LatticeRuleSpace ruleSpace)
        {
            var assignmentsPerNT = BuildAssignmentsPerNt(newAssignmentIndices, previousAssignments, ruleSpace);
            return AreThereRedundantPOSRules(candidateRules, assignmentsPerNT, strictlyRHSNts);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Dictionary<int, HashSet<int>> BuildAssignmentsPerNt(
            HashSet<Rule> newAssignments,
            HashSet<Rule> previousAssignments)
        {
            var assignmentsPerNT = t_scratchAssignmentsPerNT ??= new Dictionary<int, HashSet<int>>();
            foreach (var kv in assignmentsPerNT) ReturnIntHashSet(kv.Value);
            assignmentsPerNT.Clear();

            AddAssignmentsPerNt(newAssignments, assignmentsPerNT);
            AddAssignmentsPerNt(previousAssignments, assignmentsPerNT);
            return assignmentsPerNT;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Dictionary<int, HashSet<int>> BuildAssignmentsPerNt(
            ushort[] newAssignmentIndices,
            HashSet<Rule> previousAssignments,
            LatticeRuleSpace ruleSpace)
        {
            var assignmentsPerNT = t_scratchAssignmentsPerNT ??= new Dictionary<int, HashSet<int>>();
            foreach (var kv in assignmentsPerNT) ReturnIntHashSet(kv.Value);
            assignmentsPerNT.Clear();

            AddAssignmentsPerNt(newAssignmentIndices, assignmentsPerNT, ruleSpace);
            AddAssignmentsPerNt(previousAssignments, assignmentsPerNT);
            return assignmentsPerNT;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AreThereRedundantPOSRules(
            Grammar currentHypothesis,
            Dictionary<int, HashSet<int>> assignmentsPerNT,
            HashSet<int> strictlyRHSNts)
        {
            foreach (var rulesWithSameLHS in currentHypothesis.RulesByLHS)
            {
                if (rulesWithSameLHS == null)
                {
                    continue;
                }

                int count = rulesWithSameLHS.Count;
                for (int i = 0; i < count - 1; i++)
                {
                    var r1 = rulesWithSameLHS[i];

                    for (int j = i + 1; j < count; j++)
                    {
                        var r2 = rulesWithSameLHS[j];
                        if (IsRedundantPOSRulePair(r1, r2, assignmentsPerNT, strictlyRHSNts))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AreThereRedundantPOSRules(
            List<Rule> candidateRules,
            Dictionary<int, HashSet<int>> assignmentsPerNT,
            HashSet<int> strictlyRHSNts)
        {
            int count = candidateRules.Count;
            for (int i = 0; i < count - 1; i++)
            {
                var r1 = candidateRules[i];
                for (int j = i + 1; j < count; j++)
                {
                    var r2 = candidateRules[j];
                    if (r1.LeftHandSide != r2.LeftHandSide)
                    {
                        continue;
                    }

                    if (IsRedundantPOSRulePair(r1, r2, assignmentsPerNT, strictlyRHSNts))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsRedundantPOSRulePair(
            Rule r1,
            Rule r2,
            Dictionary<int, HashSet<int>> assignmentsPerNT,
            HashSet<int> strictlyRHSNts)
        {
            if (r1.RightHandSide.Length != r2.RightHandSide.Length)
            {
                return false;
            }

            bool r2Containsr1onRHS1 = false;
            bool r1Containsr2onRHS1 = false;

            if (strictlyRHSNts.Contains(r1.RightHandSide[0]) || strictlyRHSNts.Contains(r2.RightHandSide[0]))
            {
                bool found1 = assignmentsPerNT.TryGetValue(r1.RightHandSide[0], out var POSes1);
                bool found2 = assignmentsPerNT.TryGetValue(r2.RightHandSide[0], out var POSes2);

                if (found1 && found2)
                {
                    if (POSes1.IsSubsetOf(POSes2))
                    {
                        r2Containsr1onRHS1 = true;
                    }

                    if (POSes2.IsSubsetOf(POSes1))
                    {
                        r1Containsr2onRHS1 = true;
                    }
                }

                if (r1.RightHandSide.Length == 1 && (r2Containsr1onRHS1 || r1Containsr2onRHS1))
                {
                    return true;
                }
            }

            if (r1.RightHandSide.Length == 1)
            {
                return false;
            }

            bool r2Containsr1onRHS2 = false;
            bool r1Containsr2onRHS2 = false;

            if (strictlyRHSNts.Contains(r1.RightHandSide[1]) || strictlyRHSNts.Contains(r2.RightHandSide[1]))
            {
                bool found1 = assignmentsPerNT.TryGetValue(r1.RightHandSide[1], out var POSes1);
                bool found2 = assignmentsPerNT.TryGetValue(r2.RightHandSide[1], out var POSes2);

                if (found1 && found2)
                {
                    if (POSes1.IsSubsetOf(POSes2))
                    {
                        r2Containsr1onRHS2 = true;
                    }

                    if (POSes2.IsSubsetOf(POSes1))
                    {
                        r1Containsr2onRHS2 = true;
                    }
                }
            }

            if (r1.RightHandSide[0].Equals(r2.RightHandSide[0]))
            {
                if (r2Containsr1onRHS2 || r1Containsr2onRHS2)
                {
                    return true;
                }
            }

            if (r1.RightHandSide[1].Equals(r2.RightHandSide[1]))
            {
                if (r2Containsr1onRHS1 || r1Containsr2onRHS1)
                {
                    return true;
                }
            }

            if (r2Containsr1onRHS1 && r2Containsr1onRHS2)
            {
                return true;
            }

            if (r1Containsr2onRHS1 && r1Containsr2onRHS2)
            {
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddAssignmentsPerNt(HashSet<Rule> assignments, Dictionary<int, HashSet<int>> assignmentsPerNT)
        {
            if (assignments == null)
            {
                return;
            }

            foreach (var assignment in assignments)
            {
                if (!assignmentsPerNT.TryGetValue(assignment.LeftHandSide, out var POSes))
                {
                    POSes = RentIntHashSet();
                    assignmentsPerNT.Add(assignment.LeftHandSide, POSes);
                }
                POSes.Add(assignment.RightHandSide[0]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddAssignmentsPerNt(
            ushort[] assignmentIndices,
            Dictionary<int, HashSet<int>> assignmentsPerNT,
            LatticeRuleSpace ruleSpace)
        {
            if (assignmentIndices == null)
            {
                return;
            }

            var posAssignmentRules = ruleSpace.POSAssignmentRules;
            for (int i = 0; i < assignmentIndices.Length; i++)
            {
                var assignment = posAssignmentRules[assignmentIndices[i]];
                if (!assignmentsPerNT.TryGetValue(assignment.LeftHandSide, out var POSes))
                {
                    POSes = RentIntHashSet();
                    assignmentsPerNT.Add(assignment.LeftHandSide, POSes);
                }

                POSes.Add(assignment.RightHandSide[0]);
            }
        }
    }
}
