// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EarleyParserForGreedyGrammarInduction
{
    public class LatticeRuleSpace
    {
        public readonly Rule[] AllRules;
        public readonly int NonTerminalsCount;
        public HashSet<int> POSes { get; set; }

        // Fast lookup: Is this rule an entry point (S -> Xi)?
        public readonly bool[] IsEntryRule;

        // Dependency Graph: RuleDependencies[rIdx] returns indices of rules
        // whose LHS matches a symbol in the RHS of rIdx.
        public readonly ushort[][] RuleDependencies;
        public int[] NonTerminalIds { get; set; }

        // Numbering: RHSNTIndices[rIdx] returns 0-based indices of NTs in RHS.
        // Used for the "Gap Check" without string parsing.
        public readonly byte[][] RHSNTIndices;

        // Map: Which rules have Xi as their LHS?
        public readonly ushort[][] RulesByLHS;

        public Rule[] POSAssignmentRules { get; set; }
        public Dictionary<Rule, ushort> POSAssignmentDictionary { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetUnaryRuleIndex(int i) => (ushort)(i * (NonTerminalsCount * NonTerminalsCount + 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetBinaryRuleIndex(int i, int j, int k)
            => (ushort)(i * (NonTerminalsCount * NonTerminalsCount + 1) + 1 + j * NonTerminalsCount + k);

        public LatticeRuleSpace(HashSet<int> partsOfSpeechCategories, int maxNonTerminals)
        {
            POSes = new HashSet<int>(partsOfSpeechCategories);
            NonTerminalsCount = maxNonTerminals;
            var startSymbolId = SymbolTable.Instance.GetId(Grammar.StartSymbol);

            // 1. Generate Non-Terminal IDs (X1...Xn)
            int[] ntIds = new int[maxNonTerminals];
            for (int i = 0; i < maxNonTerminals; i++)
            {
                ntIds[i] = SymbolTable.Instance.GetId($"X{i + 1}");
            }
            NonTerminalIds = ntIds;

            // 2. Build the Rule Array (Structured exactly like the indexing)
            // Unary (S -> Xi) + Binary (Xi -> Xj Xk)
            int ruleCount = maxNonTerminals + (maxNonTerminals * maxNonTerminals * maxNonTerminals);
            AllRules = new Rule[ruleCount];
            IsEntryRule = new bool[ruleCount];
            RHSNTIndices = new byte[ruleCount][];
            var rulesByLHSList = new List<ushort>[maxNonTerminals];
            for (int i = 0; i < maxNonTerminals; i++) rulesByLHSList[i] = new List<ushort>();

            // Fill Unary Rules: S -> Xi
            for (int i = 0; i < maxNonTerminals; i++)
            {
                ushort idx = GetUnaryRuleIndex(i);
                AllRules[idx] = new Rule(startSymbolId, new[] { ntIds[i] });
                IsEntryRule[idx] = true;
                RHSNTIndices[idx] = new byte[] { (byte)i };
                // S rules don't have an Xi LHS, they are root rules.
            }

            // Fill Binary Rules: Xi -> Xj Xk
            for (int i = 0; i < maxNonTerminals; i++)
            {
                for (int j = 0; j < maxNonTerminals; j++)
                {
                    for (int k = 0; k < maxNonTerminals; k++)
                    {
                        ushort idx = GetBinaryRuleIndex(i, j, k);
                        AllRules[idx] = new Rule(ntIds[i], new[] { ntIds[j], ntIds[k] });
                        RHSNTIndices[idx] = new byte[] { (byte)j, (byte)k };
                        rulesByLHSList[i].Add(idx);
                    }
                }
            }

            // Convert LHS list to flat arrays
            RulesByLHS = new ushort[rulesByLHSList.Length][];
            for (int i = 0; i < rulesByLHSList.Length; i++)
            {
                RulesByLHS[i] = rulesByLHSList[i].ToArray();
            }

            // 3. Precompute Rule Dependencies
            // A rule A -> B C depends on all rules where LHS is B or C.
            RuleDependencies = new ushort[ruleCount][];
            for (int r = 0; r < ruleCount; r++)
            {
                if (RHSNTIndices[r] == null)
                {
                    RuleDependencies[r] = Array.Empty<ushort>();
                    continue;
                }
                var deps = new HashSet<ushort>();
                foreach (byte ntIdx in RHSNTIndices[r])
                {
                    foreach (ushort targetRuleIdx in RulesByLHS[ntIdx])
                    {
                        deps.Add(targetRuleIdx);
                    }
                }
                var depsArray = new ushort[deps.Count];
                deps.CopyTo(depsArray);
                RuleDependencies[r] = depsArray;
            }

            List<Rule> POSAssignmentRulesList = new List<Rule>();
            POSAssignmentDictionary = [];
            for (int i = 0; i < NonTerminalsCount; i++)
            {
                foreach (var pos in partsOfSpeechCategories)
                {
                    var r = new Rule(ntIds[i], [pos]);
                    POSAssignmentDictionary[r] = (ushort)POSAssignmentRulesList.Count;
                    POSAssignmentRulesList.Add(r);
                }
            }
            POSAssignmentRules = POSAssignmentRulesList.ToArray();
        }


        public List<Rule> this[ushort[] set]
        {
            get
            {
                List<Rule> res = new List<Rule>(set.Length);
                for (int i = 0; i < set.Length; i++)
                {
                    res.Add(AllRules[set[i]]);
                }

                return res;
            }

        }

        public void CopyRulesTo(ushort[] set, List<Rule> destination)
        {
            destination.Clear();
            destination.EnsureCapacity(set.Length);
            for (int i = 0; i < set.Length; i++)
            {
                destination.Add(AllRules[set[i]]);
            }
        }

        public Rule GetRuleFromIndex(int index)
        {
            return AllRules[index];
        }

        public ushort[] GetCanonicalRuleIndices(List<Rule> canonicalRules)
        {
            var indices = new List<ushort>();

            foreach (var rule in canonicalRules)
            {
                ushort? index = FindRuleIndex(rule);
                if (index.HasValue)
                {
                    indices.Add(index.Value);
                }
                else
                {
                    throw new InvalidOperationException($"Canonical rule not found in rule space: {rule}");
                }
            }

            var arr = indices.ToArray();
            Array.Sort(arr);
            return arr;

        }

        /// <summary>
        /// Find the index of a specific rule in the LatticeRuleSpace
        /// Uses arithmetic indexing for efficiency
        /// </summary>
        public ushort FindRuleIndex(Rule rule)
        {
            // Handle START -> Xi rules (unary rules from START)
            if (rule.LeftHandSide == SymbolTable.Instance.GetId(Grammar.StartSymbol))
            {
                if (rule.RightHandSide.Length == 1)
                {
                    int rhsSymbol = rule.RightHandSide[0];
                    int xiIndex = GetXIndex(rhsSymbol); // Get the number from "Xi"

                    if (xiIndex >= 1 && xiIndex <= NonTerminalsCount)
                    {
                        return GetUnaryRuleIndex(xiIndex - 1);
                    }
                }
            }
            // Handle Xi -> Xj Xk rules (binary rules between nonterminals)
            else if (rule.RightHandSide.Length == 2)
            {
                int lhsIndex = GetXIndex(rule.LeftHandSide) - 1; // Convert X1->0, X2->1, etc.
                int rhs0Index = GetXIndex(rule.RightHandSide[0]) - 1;
                int rhs1Index = GetXIndex(rule.RightHandSide[1]) - 1;

                if (lhsIndex >= 0 && rhs0Index >= 0 && rhs1Index >= 0)
                {
                    return GetBinaryRuleIndex(lhsIndex, rhs0Index, rhs1Index);
                }
            }
            // Handle Xi -> POS rules (terminal rules)
            else if (rule.RightHandSide.Length == 1 && POSes.Contains(rule.RightHandSide[0]))
            {
                if (POSAssignmentDictionary.TryGetValue(rule, out ushort index))
                {
                    return index;
                }
            }

            throw new Exception("Rule not found in LatticeRuleSpace");
        }

        /// <summary>
        /// Extract the number from a symbol like "X1", "X2", "X3", etc.
        /// Returns -1 if not a valid Xi symbol
        /// </summary>
        private static int GetXIndex(int symbolId)
        {
            string symbolName = SymbolTable.Instance.GetSymbol(symbolId);

            if (symbolName.StartsWith('X') && symbolName.Length > 1)
            {
                if (int.TryParse(symbolName.AsSpan(1), out int index))
                {
                    return index;
                }
            }

            return -1;
        }

    }
}
