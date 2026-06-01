// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;

using System.Runtime.CompilerServices;
using EarleyParserForGreedyGrammarInduction;
using static GreedyGrammarInductionLearner.ArrayCompressor;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// Extensions and utilities for grammar manipulation
    /// </summary>
    public static class GrammarExtensions
    {

        public static SymbolTable s_symbolTable;

        // ── Per-thread scratch ────────────────────────────────────────────────
        [ThreadStatic] private static HashSet<int> t_scratchPosAssignmentLHS;
        [ThreadStatic] private static HashSet<int> t_scratchGrammarNTs;      // ComputeNTsAndHighestIndex
        [ThreadStatic] private static HashSet<int> t_scratchNtCheckArr;      // ComputeNTsAndHighestIndex
        [ThreadStatic] private static CandidateGrammarRules t_scratchCandidateRules;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static CandidateGrammarRules GetCandidateRulesBuffer()
        {
            return t_scratchCandidateRules ??= new CandidateGrammarRules();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (ContextFreeGrammar grammar, HashSet<Rule> POSMappings, Dictionary<int, int> minLengths) ExtendCoreRulesWithPOSAssignments(
            HashSet<int> strictlyRHSNonterminals,
            HashSet<Rule> POSAssignmentsOptions,
            HashSet<Rule> previousPosMapping,
            List<Rule> coreRules)
        {
            HashSet<Rule> POSMappings = new HashSet<Rule>();
            POSMappings.UnionWith(POSAssignmentsOptions);
            ContextFreeGrammar grammar = null;

            if (previousPosMapping != null && previousPosMapping.Count > 0)
            {
                POSMappings.UnionWith(previousPosMapping);
            }

            // Cheap early filter: strictly-RHS NTs without POS are definitely unproductive.
            // This avoids building the grammar in the common case.
            var isStrictlyRHSNonProductive = RuleProductivityTester.IsStrictlyRHSNTNonProductive(strictlyRHSNonterminals, POSMappings);
            if (isStrictlyRHSNonProductive)
            {
                return (null, null, null);
            }

            // Check for duplicate POS assignment rules with the same LHS (per-thread scratch).
            var posAssignmentLHS = t_scratchPosAssignmentLHS ??= new HashSet<int>();
            posAssignmentLHS.Clear();
            foreach (var posRule in POSMappings)
            {
                if (!posAssignmentLHS.Add(posRule.LeftHandSide))
                {
                    // Duplicate LHS found in POS assignments - invalid grammar
                    return (null, null, null);
                }
            }

            var candidateRules = GetCandidateRulesBuffer();
            candidateRules.Build(coreRules, null, POSMappings);

            if (RuleProductivityTester.AreThereRedundantPOSRules(candidateRules.Rules, POSMappings, null, strictlyRHSNonterminals))
            {
                // If there is a rule x such that the current pos assignments entail that it can be subsumed under another rule
                // which includes its pos assignments as a subset, discard the grammar, as we already have a solution without rule x.
                // For instance, a grammar containing both rules X1 -> X1 X4 and X1 -> X2 X4 with assignments X1 -> PN and X2 -> PN (X2 appears only on RHS of rules).
                candidateRules.ClearSources();
                return (null, null, null);
            }

            // Full productivity check via min-lengths fixpoint.
            // Catches all cases including recursive cycles without base cases
            // (which the strictly-RHS check above cannot detect).
            // The minLengths are reused by GetGrammarShape later, avoiding redundant computation.
            var (isProductive, minLengths) = RuleProductivityTester.CheckProductivity(candidateRules.Rules);
            if (!isProductive)
            {
                candidateRules.ClearSources();
                return (null, null, null);
            }

            grammar = candidateRules.ToGrammar();
            candidateRules.ClearSources();

            return (grammar, POSMappings, minLengths);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (ContextFreeGrammar grammar, Dictionary<int, int> minLengths) ExtendCoreRulesWithValidatedPOSAssignments(
            HashSet<Rule> POSAssignmentsOptions,
            HashSet<Rule> previousPosMapping,
            List<Rule> coreRules,
            HashSet<int> strictlyRHSNonterminals)
        {
            var candidateRules = GetCandidateRulesBuffer();
            candidateRules.Build(coreRules, previousPosMapping, POSAssignmentsOptions);

            if (RuleProductivityTester.AreThereRedundantPOSRules(
                candidateRules.Rules,
                POSAssignmentsOptions,
                previousPosMapping,
                strictlyRHSNonterminals))
            {
                candidateRules.ClearSources();
                return (null, null);
            }

            var (isProductive, minLengths) = RuleProductivityTester.CheckProductivity(candidateRules.Rules);
            if (!isProductive)
            {
                candidateRules.ClearSources();
                return (null, null);
            }

            var grammar = candidateRules.ToGrammar();
            candidateRules.ClearSources();

            return (grammar, minLengths);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (ContextFreeGrammar grammar, Dictionary<int, int> minLengths) ExtendCoreRulesWithValidatedPOSAssignmentIndices(
            ushort[] POSAssignmentIndices,
            HashSet<Rule> previousPosMapping,
            List<Rule> coreRules,
            HashSet<int> strictlyRHSNonterminals,
            LatticeRuleSpace ruleSpace)
        {
            var candidateRules = GetCandidateRulesBuffer();
            candidateRules.Build(coreRules, previousPosMapping, POSAssignmentIndices, ruleSpace);

            if (RuleProductivityTester.AreThereRedundantPOSRules(
                candidateRules.Rules,
                POSAssignmentIndices,
                previousPosMapping,
                strictlyRHSNonterminals,
                ruleSpace))
            {
                candidateRules.ClearSources();
                return (null, null);
            }

            var (isProductive, minLengths) = RuleProductivityTester.CheckProductivity(candidateRules.Rules);
            if (!isProductive)
            {
                candidateRules.ClearSources();
                return (null, null);
            }

            var grammar = candidateRules.ToGrammar();
            candidateRules.ClearSources();

            return (grammar, minLengths);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HashSet<Rule> MaterializePOSMappings(HashSet<Rule> posAssignments, HashSet<Rule> previousPosMapping)
        {
            var capacity = (posAssignments?.Count ?? 0) + (previousPosMapping?.Count ?? 0);
            var posMappings = new HashSet<Rule>(capacity);
            if (previousPosMapping != null && previousPosMapping.Count > 0)
            {
                posMappings.UnionWith(previousPosMapping);
            }
            posMappings.UnionWith(posAssignments);
            return posMappings;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HashSet<Rule> MaterializePOSMappings(
            ushort[] posAssignmentIndices,
            HashSet<Rule> previousPosMapping,
            LatticeRuleSpace ruleSpace)
        {
            var capacity = (posAssignmentIndices?.Length ?? 0) + (previousPosMapping?.Count ?? 0);
            var posMappings = new HashSet<Rule>(capacity);
            if (previousPosMapping != null && previousPosMapping.Count > 0)
            {
                posMappings.UnionWith(previousPosMapping);
            }

            if (posAssignmentIndices != null)
            {
                var posAssignmentRules = ruleSpace.POSAssignmentRules;
                for (int i = 0; i < posAssignmentIndices.Length; i++)
                    posMappings.Add(posAssignmentRules[posAssignmentIndices[i]]);
            }

            return posMappings;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (HashSet<int> grammarNTs, int highestIndex) ComputeNTsAndHighestIndex(List<Rule> rules)
        {
            // Pool both the returned grammarNTs set and the transient ntcheckArr set per thread.
            // The caller is expected to consume grammarNTs synchronously before the next call on this thread.
            var grammarNTs = t_scratchGrammarNTs ??= new HashSet<int>();
            grammarNTs.Clear();
            var startNT = s_symbolTable.GetId(Grammar.StartSymbol);
            int highestIndex = 0;

            // Get all nonterminals of rules.
            foreach (var r1 in rules)
            {
                if (r1.LeftHandSide != startNT)
                {
                    grammarNTs.Add(r1.LeftHandSide);
                }

                foreach (var rhs in r1.RightHandSide)
                {
                    grammarNTs.Add(rhs);
                }
            }

            var ntcheckArr = t_scratchNtCheckArr ??= new HashSet<int>();
            ntcheckArr.Clear();
            try
            {
                foreach (var item in grammarNTs)
                {
                    if (item == startNT)
                    {
                        continue;
                    }

                    var str = s_symbolTable.GetSymbol(item);
                    var str1 = str[1..];
                    int index = int.Parse(str1);
                    ntcheckArr.Add(index);

                    if (index > highestIndex)
                    {
                        highestIndex = index;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("WARNING!!! Error while computing grammar nonterminals: " + e.Message);
                throw;
            }


            return (grammarNTs, highestIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AccumulatePreviousOptimalMappingandParsed(
            OptimalSolutionNode subSolutionNode,
            List<List<CompressionRange>> previousParseds,
            List<HashSet<Rule>> previousPosMappings,
            List<int> listOfIndicesInParent,
            LatticeRuleSpace ruleSpace)
        {

            foreach (var previousMapping in subSolutionNode.POSAssignments)
            {
                HashSet<Rule> mapping = new HashSet<Rule>();
                foreach (var index in previousMapping)
                {
                    mapping.Add(ruleSpace.POSAssignmentRules[index]);
                }
                previousPosMappings.Add(mapping);
            }

            foreach (var p in subSolutionNode.Parsed)
            {
                previousParseds.Add(p);
            }

            for (int i = 0; i < subSolutionNode.Parsed.Count; i++)
            {
                listOfIndicesInParent.Add(i);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string PrintRequiredEvidence(ReadOnlySpan<int> grammarShape)
        {
            string s = $"Evidence required to learn grammar: \r\n";
            int totalDepthsCount = 0;

            // Note: the last item in the shape vector is a flag, not used to signify depth.
            for (int i = 0; i < grammarShape.Length - 1; i++)
            {
                if (grammarShape[i] != 0)
                {
                    totalDepthsCount += grammarShape[i];
                    s += $"[length {i}: {grammarShape[i]} sentences], ";
                }
            }
            s = s[..^2];

            int totalEvidence = totalDepthsCount;
            s += "total unique sentences required: " + totalEvidence;
            return s;
        }
    }
}
