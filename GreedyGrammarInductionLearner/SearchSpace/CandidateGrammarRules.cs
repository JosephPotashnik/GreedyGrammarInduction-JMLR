// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EarleyParserForGreedyGrammarInduction;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// Per-thread candidate grammar assembly buffer.
    /// Holds the exact rule sequence that is passed to ContextFreeGrammar today, while
    /// keeping the structural rules and compact POS assignment source explicit for the
    /// next validation checkpoints.
    /// </summary>
    internal sealed class CandidateGrammarRules
    {
        private readonly List<Rule> _rules = new List<Rule>();

        public List<Rule> Rules => _rules;
        public HashSet<Rule> PreviousPosMapping { get; private set; }
        public HashSet<Rule> POSAssignments { get; private set; }
        public ushort[] POSAssignmentIndices { get; private set; }
        public LatticeRuleSpace RuleSpace { get; private set; }

        public int Count => _rules.Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Build(
            List<Rule> coreRules,
            HashSet<Rule> previousPosMapping,
            HashSet<Rule> posAssignments)
        {
            ArgumentNullException.ThrowIfNull(coreRules);

            PreviousPosMapping = previousPosMapping;
            POSAssignments = posAssignments;
            POSAssignmentIndices = null;
            RuleSpace = null;

            ResetRules(coreRules, (previousPosMapping?.Count ?? 0) + (posAssignments?.Count ?? 0));
            AddRules(previousPosMapping);
            AddRules(posAssignments);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Build(
            List<Rule> coreRules,
            HashSet<Rule> previousPosMapping,
            ushort[] posAssignmentIndices,
            LatticeRuleSpace ruleSpace)
        {
            ArgumentNullException.ThrowIfNull(coreRules);
            ArgumentNullException.ThrowIfNull(ruleSpace);

            PreviousPosMapping = previousPosMapping;
            POSAssignments = null;
            POSAssignmentIndices = posAssignmentIndices;
            RuleSpace = ruleSpace;

            ResetRules(coreRules, (previousPosMapping?.Count ?? 0) + (posAssignmentIndices?.Length ?? 0));
            AddRules(previousPosMapping);
            AddPOSAssignmentRules(posAssignmentIndices, ruleSpace);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ContextFreeGrammar ToGrammar()
        {
            // ContextFreeGrammar enumerates the list immediately and does not retain it.
            return new ContextFreeGrammar(_rules);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearSources()
        {
            PreviousPosMapping = null;
            POSAssignments = null;
            POSAssignmentIndices = null;
            RuleSpace = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResetRules(List<Rule> coreRules, int additionalRuleCount)
        {
            _rules.Clear();
            _rules.EnsureCapacity(coreRules.Count + additionalRuleCount);
            _rules.AddRange(coreRules);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddRules(HashSet<Rule> rules)
        {
            if (rules == null || rules.Count == 0)
            {
                return;
            }

            _rules.AddRange(rules);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddPOSAssignmentRules(ushort[] posAssignmentIndices, LatticeRuleSpace ruleSpace)
        {
            if (posAssignmentIndices == null || posAssignmentIndices.Length == 0)
            {
                return;
            }

            var posAssignmentRules = ruleSpace.POSAssignmentRules;
            for (int i = 0; i < posAssignmentIndices.Length; i++)
            {
                _rules.Add(posAssignmentRules[posAssignmentIndices[i]]);
            }
        }
    }
}
