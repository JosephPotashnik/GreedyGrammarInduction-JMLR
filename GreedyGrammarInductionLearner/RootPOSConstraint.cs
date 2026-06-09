// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System.Collections.Generic;
using EarleyParserForGreedyGrammarInduction;

namespace GreedyGrammarInductionLearner;

public sealed class RootPOSConstraint
{
    public HashSet<int> ForbiddenNewAssignmentLhs { get; }
    public ulong[] ForbiddenNewAssignmentMask { get; }

    private RootPOSConstraint(HashSet<int> forbiddenNewAssignmentLhs, ulong[] forbiddenNewAssignmentMask)
    {
        ForbiddenNewAssignmentLhs = forbiddenNewAssignmentLhs;
        ForbiddenNewAssignmentMask = forbiddenNewAssignmentMask;
    }

    public static RootPOSConstraint Build(ushort[] currentRootNodeSet, LatticeRuleSpace ruleSpace)
    {
        var rootRules = ruleSpace[currentRootNodeSet];
        return Build(rootRules, ruleSpace);
    }

    public static RootPOSConstraint Build(List<Rule> rootRules, LatticeRuleSpace ruleSpace)
    {
        var forbiddenNewAssignmentLhs = new HashSet<int>();
        for (int ruleIdx = 0; ruleIdx < rootRules.Count; ruleIdx++)
        {
            var rhs = rootRules[ruleIdx].RightHandSide;
            for (int rhsIdx = 0; rhsIdx < rhs.Length; rhsIdx++)
            {
                forbiddenNewAssignmentLhs.Add(rhs[rhsIdx]);
            }
        }

        int words = (ruleSpace.POSAssignmentRules.Length + 63) >> 6;
        var forbiddenNewAssignmentMask = new ulong[words];

        if (forbiddenNewAssignmentLhs.Count != 0)
        {
            var posAssignmentRules = ruleSpace.POSAssignmentRules;
            for (int i = 0; i < posAssignmentRules.Length; i++)
            {
                if (forbiddenNewAssignmentLhs.Contains(posAssignmentRules[i].LeftHandSide))
                {
                    forbiddenNewAssignmentMask[i >> 6] |= 1UL << (i & 63);
                }
            }
        }

        return new RootPOSConstraint(forbiddenNewAssignmentLhs, forbiddenNewAssignmentMask);
    }

    public bool ForbidsNewAssignment(int lhs)
    {
        return ForbiddenNewAssignmentLhs.Contains(lhs);
    }
}
