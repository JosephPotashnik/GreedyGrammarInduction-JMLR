// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// Grammar object encapsulates a set of production rules (which may be Context Free or Linear Indexed).
    /// It supports the tentative addition and substraction of rules, as well as the possibility to accept/reject these changes.
    /// Grammar object allows public access to Rules dictionary, which is used by EarleyParser for prediction of new Earley Items.
    /// 
    /// Grammar object is abstract. Instantiate either ContextFreeGrammar or LinearIndexedGrammar (see their classes).
    /// 
    /// Note: The terminals corresponding to parts of speech (e.g. D -> 'the', A -> 'big') appear in a separate lexicon.json file
    /// See CFGExample.txt and LIGExample.txt for examples of grammars.
    /// </summary>
    public abstract class Grammar
    {
        public const string GammaSymbol = "Gamma";
        public const string StartSymbol = "START";
        public const string EpsilonSymbol = "Epsilon";
        public const string StarSymbol = "*";
        public double Lambda { get; protected set; } //growth rate of the grammar, which is used as a heuristic in the search. 

        public static SymbolTable s_symbolTable = SymbolTable.Instance;

        // Rules organized by Left Hand Side as array indexed by symbol ID (O(1) lookup)
        // Array elements may be null if no rules exist for that LHS.
        public readonly List<Rule>[] RulesByLHS;

        // Cached array of non-null rule lists — avoids null-skipping scan and enumerator overhead on every traversal.
        private readonly List<Rule>[] _nonNullRuleLists;

        // Backward compatibility property - creates dictionary on-demand (avoid in hot paths!)
        public Dictionary<int, List<Rule>> Rules
        {
            get
            {
                var dict = new Dictionary<int, List<Rule>>();
                for (int i = 0; i < RulesByLHS.Length; i++)
                {
                    if (RulesByLHS[i] != null)
                    {
                        dict[i] = RulesByLHS[i];
                    }
                }
                return dict;
            }
        }

        public static HashSet<int> PartsOfSpeech;

        // ── Per-thread scratch pools for hot-path allocations ──────────────────────
        // Cleared on acquisition; returned content must be consumed before the next
        // scratch-using call on the same thread, or copied out if it escapes.
        [ThreadStatic] private static HashSet<int> t_scratchNts;          // CollectAllNonterminals target
        [ThreadStatic] private static Dictionary<int, int> t_scratchMinLens;    // CalculateMinLengths target
        [ThreadStatic] private static Dictionary<int, int> t_scratchCtxLens;    // CalculateContextLengths target
        [ThreadStatic] private static PriorityQueue<int, int> t_scratchCtxPQ;   // CalculateContextLengths PQ
        // CalculateLambda scratch
        [ThreadStatic] private static Dictionary<int, int> t_scratchNtMap;
        [ThreadStatic] private static List<FlatRule> t_scratchFlatList;
        [ThreadStatic] private static FlatRule[] t_scratchFlatArr;
        [ThreadStatic] private static int[] t_scratchRuleStart;
        // FindLatticeStrictlyRhsNonterminals scratch (2 transient, 1 output)
        [ThreadStatic] private static HashSet<int> t_scratchUpperLhs;
        [ThreadStatic] private static HashSet<int> t_scratchUpperLhsPos;
        // FindAllRecursiveNonterminals scratch
        [ThreadStatic] private static Dictionary<int, List<int>> t_scratchAdj;
        [ThreadStatic] private static HashSet<int> t_scratchDfsVisited;
        [ThreadStatic] private static Stack<List<int>> t_intListPool;
        // GenerateAllStringsCore_Fast scratch
        [ThreadStatic] private static Dictionary<int, int> t_scratchNtToIdx;
        [ThreadStatic] private static HashSet<ulong>[][] t_scratchDp;
        [ThreadStatic] private static List<int>[] t_scratchPopulatedLens;
        [ThreadStatic] private static int t_scratchDpSize;
        [ThreadStatic] private static int t_scratchDpMaxLen;
        [ThreadStatic] private static int[] t_scratchShiftAmounts;
        [ThreadStatic] private static List<int> t_scratchEpsilonLhsIdx;
        [ThreadStatic] private static List<(int lhs, ulong posEnc)> t_scratchTerminalRules;
        [ThreadStatic] private static List<(int lhs, int yIdx, int zIdx)> t_scratchBinaryRules;
        [ThreadStatic] private static List<(int lhs, int yIdx)> t_scratchUnitRules;
        [ThreadStatic] private static Stack<HashSet<ulong>> t_ulongSetPool;

        // Cached POS→compact-index mapping. PartsOfSpeech is reassigned at lexicon load
        // (effectively once per run). We key the cache by reference identity; if it ever
        // changes, the cache rebuilds under the lock.
        private static HashSet<int> s_cachedPosSource;
        private static Dictionary<int, int> s_cachedPosToIndex;
        private static int s_cachedBitsPerSymbol;
        private static readonly object s_posCacheLock = new object();

        private static (Dictionary<int, int> posToIndex, int bitsPerSymbol) GetCachedPosToIndex()
        {
            var src = PartsOfSpeech;
            if (!ReferenceEquals(src, s_cachedPosSource))
            {
                lock (s_posCacheLock)
                {
                    if (!ReferenceEquals(src, s_cachedPosSource))
                    {
                        int posCount = src.Count;
                        int bps = posCount <= 1 ? 1 : (32 - BitOperations.LeadingZeroCount((uint)(posCount - 1)));
                        var dict = new Dictionary<int, int>(posCount);
                        int p = 0;
                        foreach (int pos in src) dict[pos] = p++;
                        Volatile.Write(ref s_cachedPosToIndex, dict);
                        s_cachedBitsPerSymbol = bps;
                        Volatile.Write(ref s_cachedPosSource, src);
                    }
                }
            }
            return (s_cachedPosToIndex, s_cachedBitsPerSymbol);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static List<int> RentIntList()
        {
            var pool = t_intListPool ??= new Stack<List<int>>();
            if (pool.Count > 0)
            {
                var l = pool.Pop();
                l.Clear();
                return l;
            }
            return new List<int>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnIntList(List<int> list)
        {
            // Pool is lazily created by RentIntList; guard in case Return runs first on a thread.
            (t_intListPool ??= new Stack<List<int>>()).Push(list);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static HashSet<ulong> RentUlongSet()
        {
            var pool = t_ulongSetPool ??= new Stack<HashSet<ulong>>();
            if (pool.Count > 0)
            {
                var s = pool.Pop();
                s.Clear();
                return s;
            }
            return new HashSet<ulong>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnUlongSet(HashSet<ulong> s)
        {
            (t_ulongSetPool ??= new Stack<HashSet<ulong>>()).Push(s);
        }

        public Grammar(IEnumerable<Rule> rules1)
        {
            // Initialize array-based lookup with size based on current symbol table
            int maxSymbolId = s_symbolTable.Count;
            RulesByLHS = new List<Rule>[maxSymbolId];

            foreach (var rule in rules1)
            {
                // Populate array-based lookup
                if (RulesByLHS[rule.LeftHandSide] == null)
                {
                    RulesByLHS[rule.LeftHandSide] = new List<Rule>();
                }
                RulesByLHS[rule.LeftHandSide].Add(rule);
            }
            _nonNullRuleLists = Array.FindAll(RulesByLHS, static x => x != null);
        }

        public List<Rule> GetRules()
        {
            var result = new List<Rule>();
            foreach (var ruleList in _nonNullRuleLists)
                result.AddRange(ruleList);
            return result;
        }

        public override string ToString()
        {
            var rulesList = new List<Rule>();
            foreach (var ruleList in _nonNullRuleLists)
            {
                foreach (var rule in ruleList)
                {
                    rulesList.Add(rule);
                }
            }
            rulesList.Sort((a, b) =>
            {
                int cmp = a.LeftHandSide.CompareTo(b.LeftHandSide);
                if (cmp != 0) return cmp;
                return string.Join(",", a.RightHandSide).CompareTo(string.Join(",", b.RightHandSide));
            });

            var formattedStrings = new string[rulesList.Count];
            for (int i = 0; i < rulesList.Count; i++)
            {
                formattedStrings[i] = rulesList[i].ToFormattedStackString();
            }
            var s = string.Join("\r\n", formattedStrings);
            s += "\r\n";
            s += "Count: " + rulesList.Count + "\r\n";
            return s;
        }


        public static Grammar CreateGrammar(List<Rule> rules)
        {
            var g = new ContextFreeGrammar(rules);
            return g;
        }

        public static HashSet<int> FindLatticeStrictlyRhsNonterminals(List<Rule> coreRules)
        {
            var result = new HashSet<int>();
            FindLatticeStrictlyRhsNonterminalsInto(coreRules, result);
            return result;
        }

        // Scratch variant used on the hot path: caller owns the destination set.
        // Internal transient state comes from ThreadStatic scratch (no heap allocation).
        public static void FindLatticeStrictlyRhsNonterminalsInto(List<Rule> coreRules, HashSet<int> result)
        {
            result.Clear();
            var upperRulesLHSNonterminals = t_scratchUpperLhs ??= new HashSet<int>();
            upperRulesLHSNonterminals.Clear();
            var upperRulesLHSNonterminalsPOSRules = t_scratchUpperLhsPos ??= new HashSet<int>();
            upperRulesLHSNonterminalsPOSRules.Clear();
            var startId = s_symbolTable.GetId(StartSymbol);

            foreach (var r in coreRules)
            {
                if (r.IsLatticePosAssignmentRule())
                {
                    //POS rule use.
                    upperRulesLHSNonterminalsPOSRules.Add(r.LeftHandSide);
                }
                else
                {
                    //potentially recursive use.
                    if (r.LeftHandSide != startId)
                    {
                        upperRulesLHSNonterminals.Add(r.LeftHandSide);
                    }
                }
            }

            foreach (var r in coreRules)
            {
                if (!r.IsLatticePosAssignmentRule())
                {
                    foreach (var nt in r.RightHandSide)
                    {
                        if (!upperRulesLHSNonterminals.Contains(nt) && !upperRulesLHSNonterminalsPOSRules.Contains(nt))
                        {
                            result.Add(nt);
                        }
                    }
                }
            }
        }

        public HashSet<int> FindAllRecursiveNonterminals()
        {
            var nts = new List<int>();
            for (int i = 0; i < RulesByLHS.Length; i++)
            {
                if (RulesByLHS[i] != null)
                {
                    nts.Add(i);
                }
            }
            var recursiveNTs = new HashSet<int>();
            foreach (var nt in nts)
            {
                var visited = new HashSet<int>();
                if (FindRecursiveNonterminal(nt, nt, visited))
                {
                    recursiveNTs.Add(nt);
                }
            }
            return recursiveNTs;
        }

        /// <summary>
        /// Finds all recursive nonterminals directly from a rules list, without constructing a Grammar object.
        /// Builds a lightweight adjacency map (LHS → RHS NTs) and performs per-NT DFS cycle detection.
        /// Use this in hot paths to avoid the overhead of Grammar construction (RulesByLHS array allocation).
        /// </summary>
        public static HashSet<int> FindAllRecursiveNonterminals(List<Rule> rules)
        {
            var result = new HashSet<int>();
            FindAllRecursiveNonterminalsInto(rules, result);
            return result;
        }

        // Scratch variant for the hot path: caller owns the destination set; internal transient
        // state (adjacency dict, inner List<int> children, DFS visited set) is pooled per-thread.
        public static void FindAllRecursiveNonterminalsInto(List<Rule> rules, HashSet<int> result)
        {
            result.Clear();
            var adj = t_scratchAdj ??= new Dictionary<int, List<int>>();
            // Return any previously-rented child lists to the pool before clearing the dict.
            foreach (var kv in adj) ReturnIntList(kv.Value);
            adj.Clear();

            foreach (var rule in rules)
            {
                if (!adj.TryGetValue(rule.LeftHandSide, out var neighbors))
                {
                    neighbors = RentIntList();
                    adj[rule.LeftHandSide] = neighbors;
                }
                foreach (var rhs in rule.RightHandSide)
                {
                    neighbors.Add(rhs);
                }
            }

            var visited = t_scratchDfsVisited ??= new HashSet<int>();
            foreach (var nt in adj.Keys)
            {
                visited.Clear();
                if (FindRecursiveNonterminalStatic(nt, nt, visited, adj))
                {
                    result.Add(nt);
                }
            }
        }

        private static bool FindRecursiveNonterminalStatic(int targetNT, int currentNT, HashSet<int> visited, Dictionary<int, List<int>> adj)
        {
            visited.Add(currentNT);

            if (!adj.TryGetValue(currentNT, out var neighbors))
                return false;

            foreach (var rhs in neighbors)
            {
                if (targetNT == rhs)
                    return true;

                if (!visited.Contains(rhs))
                {
                    if (FindRecursiveNonterminalStatic(targetNT, rhs, visited, adj))
                        return true;
                }
            }
            return false;
        }

        public bool FindRecursiveNonterminal(int targetNT, int currentNT, HashSet<int> visited)
        {

            visited.Add(currentNT);

            var rules = RulesByLHS[currentNT];
            if (rules != null)
            {
                foreach (var r in rules)
                {
                    foreach (var rhs in r.RightHandSide)
                    {
                        if (targetNT == rhs)
                        {
                            return true;
                        }

                        if (!visited.Contains(rhs))
                        {
                            bool found = FindRecursiveNonterminal(targetNT, rhs, visited);
                            if (found)
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
            return false;
        }

        public static Dictionary<int, HashSet<int>> ComputePOSRulesAssignments1(List<Rule> rules)
        {
            Dictionary<int, HashSet<int>> posRulesAssignments = new Dictionary<int, HashSet<int>>();

            foreach (var rule in rules)

            {
                //assumption 1-to-1 relation between nonterminals and POS rules (who are of the form Xi ->POS)
                //it is borne out since you preserve it in your BFS traversal/hypothesis build.
                if (rule.IsLatticePosAssignmentRule())
                {
                    if (!posRulesAssignments.TryGetValue(rule.LeftHandSide, out var value))
                    {
                        value = new HashSet<int>();
                        posRulesAssignments.Add(rule.LeftHandSide, value);
                    }

                    posRulesAssignments[rule.LeftHandSide].Add(rule.RightHandSide[0]);
                }
            }

            return posRulesAssignments;
        }
        //Recursive Rules are the ones in which a nonterminal up the tree appears again in the right hand side
        //e.g., VP -> V3 S is a recursive rule since there is a path from the first instance of S to the second.


        public static int RenameVariables(List<Rule> rules, HashSet<int> partOfSpeechCategories)
        {
            var startId = s_symbolTable.GetId(StartSymbol);
            var replaceDic = new Dictionary<int, int>();
            bool encounteredStart = false;
            int runningIndex = 1;
            var epsilonId = s_symbolTable.GetId(EpsilonSymbol);

            foreach (var r1 in rules)
            {
                if (r1.LeftHandSide == startId)
                {
                    if (encounteredStart && (r1.RightHandSide.Length > 0 && r1.RightHandSide[0] != epsilonId))
                    {
                        throw new Exception("grammar is in illegal format. Please use a START -> Xi rule exactly once");
                    }
                    else
                    {
                        encounteredStart = true;
                    }
                }
                else
                {
                    if (!replaceDic.ContainsKey(r1.LeftHandSide))
                    {
                        replaceDic.Add(r1.LeftHandSide, s_symbolTable.GetId($"X{runningIndex++}"));
                    }
                }

                if (r1.RightHandSide.Length == 0)
                {
                    if (r1.LeftHandSide != startId)
                    {
                        throw new Exception("grammar is in illegal format. Please use epsilon only from the start rule START -> Epsilon exactly once");
                    }
                }
                foreach (var rhs in r1.RightHandSide)
                {
                    if (rhs == startId)
                    {
                        throw new Exception("grammar is in illegal format. Please do not use START symbol on the right hand side of rules");
                    }

                    if (rhs == epsilonId && r1.LeftHandSide != startId)
                    {
                        throw new Exception("grammar is in illegal format. Please use epsilon only from the start rule START -> Epsilon exactly once");
                    }

                    if (!partOfSpeechCategories.Contains(rhs))
                    {
                        if (!replaceDic.ContainsKey(rhs))
                        {
                            replaceDic.Add(rhs, s_symbolTable.GetId($"X{runningIndex++}"));
                        }
                    }
                }
            }

            ReplaceVariables(replaceDic, rules);
            return replaceDic.Count;
        }


        public static void ReplaceVariables(Dictionary<int, int> replaceDic, List<Rule> rules)
        {
            for (int j = 0; j < rules.Count; j++)
            {
                var rule = rules[j];
                var newLHS = rule.LeftHandSide;
                if (replaceDic.TryGetValue(rule.LeftHandSide, out var value))
                {
                    newLHS = value;
                }

                var newRHS = (int[])rule.RightHandSide.Clone();
                bool rhsChanged = false;
                for (var i = 0; i < newRHS.Length; i++)
                {
                    if (replaceDic.TryGetValue(newRHS[i], out var value1))
                    {
                        newRHS[i] = value1;
                        rhsChanged = true;
                    }
                }

                if (newLHS != rule.LeftHandSide || rhsChanged)
                {
                    rules[j] = new Rule(newLHS, newRHS, rule.Type);
                }
            }
        }


        public static void CreatePOSAssignmentRules(List<Rule> rules, HashSet<int> partOfSpeechCategories, int highIndex)
        {
            int nextAvailableIndex = highIndex + 1;
            var containedPOS = new Dictionary<int, int>();
            foreach (var rule in rules)
            {
                foreach (var rhs in rule.RightHandSide)
                {
                    if (partOfSpeechCategories.Contains(rhs))
                    {
                        if (!containedPOS.ContainsKey(rhs))
                        {
                            string nextNT = $"X{nextAvailableIndex++}";
                            containedPOS[rhs] = s_symbolTable.GetId(nextNT);
                        }
                    }
                }
            }

            ReplaceVariables(containedPOS, rules);

            foreach (var item in containedPOS)
            {
                var pos = item.Key;
                var NT = item.Value;

                var newRule = new Rule(NT, [pos]);
                rules.Add(newRule);
            }
        }

        /// <summary>
        /// Computes min derivation lengths for all nonterminals and checks that every
        /// non-terminal is productive (can derive a finite terminal string).
        /// This is the cheap first phase of GetGrammarShape — use it as an early filter
        /// before CalculateLambda, then pass the minLengths into GetGrammarShape to skip
        /// redundant recomputation.
        /// </summary>
        /// <returns>(true, minLengths) if all NTs are productive; (false, null) otherwise.</returns>
        public (bool isProductive, Dictionary<int, int> minLengths) CheckProductivity()
        {
            // Hot-path caller in ProcessSingleNode consumes minLengths immediately in GetGrammarShape,
            // so we can use ThreadStatic scratch for minLengths (it's rewritten on each call).
            var nonterminals = t_scratchNts ??= new HashSet<int>();
            CollectAllNonterminalsInto(nonterminals);
            var minLengths = t_scratchMinLens ??= new Dictionary<int, int>();
            minLengths.Clear();
            CalculateMinLengthsInto(nonterminals, null, minLengths);

            foreach (var nt in nonterminals)
            {
                if (PartsOfSpeech.Contains(nt))
                    continue;

                if (!minLengths.TryGetValue(nt, out var minLen) || minLen == int.MaxValue)
                    return (false, null);
            }

            return (true, minLengths);
        }

        // Lattice grammar representation:
        // - START -> Xi
        // - Xi -> Xj Xk
        // - Xi, Xj, and Xk are never POS symbols
        // - Xi -> POS
        // - do not include START ->  (epsilon rule) here; epsilon is handled outside/after learning
        public (bool, int[] CountsByLength) GetGrammarShape()
        {
            var nonterminals = new HashSet<int>();
            CollectAllNonterminalsInto(nonterminals);
            var minLengths = new Dictionary<int, int>(nonterminals.Count);
            CalculateMinLengthsInto(nonterminals, null, minLengths);

            foreach (var nt in nonterminals)
            {
                if (PartsOfSpeech.Contains(nt))
                    continue;

                if (!minLengths.TryGetValue(nt, out var minLen) || minLen == int.MaxValue)
                    return (true, null);
            }

            return GetGrammarShapeCore(minLengths, nonterminals);
        }

        public (bool unproductive, int[] CountsByLength, HashSet<string>[] PartsOfSpeechSequencesByLength) GetGrammarShapeIdealized()
        {
            var nonterminals = new HashSet<int>();
            CollectAllNonterminalsInto(nonterminals);
            var minLengths = new Dictionary<int, int>(nonterminals.Count);
            CalculateMinLengthsInto(nonterminals, null, minLengths);

            foreach (var nt in nonterminals)
            {
                if (PartsOfSpeech.Contains(nt))
                    continue;

                if (!minLengths.TryGetValue(nt, out var minLen) || minLen == int.MaxValue)
                    return (true, null, null);
            }

            var contextLengths = CalculateContextLengths(minLengths, nonterminals);
            int maxRuleLength = 0;
            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    int currentRuleLength = contextLengths[rule.LeftHandSide] + GetSymbolSequenceLength(rule.RightHandSide.AsSpan(), minLengths);
                    if (currentRuleLength > maxRuleLength)
                        maxRuleLength = currentRuleLength;
                }
            }

            if (maxRuleLength == int.MaxValue)
                return (true, null, null);

            var partsOfSpeechSequencesByLength = GenerateAllPartsOfSpeechSequencesCore(maxRuleLength, nonterminals);
            var countsByLength = new int[partsOfSpeechSequencesByLength.Length];
            for (int i = 0; i < countsByLength.Length; i++)
                countsByLength[i] = partsOfSpeechSequencesByLength[i].Count;

            return (false, countsByLength, partsOfSpeechSequencesByLength);
        }

        public (bool underfits, bool unproductive, int[] CountsByLength) GetGrammarShape(int[] maxEvidenceByLength)
        {
            var nonterminals = new HashSet<int>();
            CollectAllNonterminalsInto(nonterminals);
            var minLengths = new Dictionary<int, int>(nonterminals.Count);
            CalculateMinLengthsInto(nonterminals, null, minLengths);

            foreach (var nt in nonterminals)
            {
                if (PartsOfSpeech.Contains(nt))
                    continue;

                if (!minLengths.TryGetValue(nt, out var minLen) || minLen == int.MaxValue)
                    return (false, true, null);
            }

            return GetGrammarShapeCore(minLengths, nonterminals, maxEvidenceByLength);
        }

        public (bool underfits, bool unproductive, int[] CountsByLength) GetGrammarShape(Dictionary<int, int> precomputedMinLengths, int[] maxEvidenceByLength)
        {
            // Reuse the ThreadStatic nonterminals set. CheckProductivity populated it just before this call;
            // the DP work below doesn't persist references to it outside the method.
            var nonterminals = t_scratchNts ??= new HashSet<int>();
            CollectAllNonterminalsInto(nonterminals);
            return GetGrammarShapeCore(precomputedMinLengths, nonterminals, maxEvidenceByLength);
        }

        private (bool, int[] CountsByLength) GetGrammarShapeCore(Dictionary<int, int> minLengths, HashSet<int> nonterminals)
        {
            var contextLengths = CalculateContextLengths(minLengths, nonterminals);
            int maxRuleLength = 0;
            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    int currentRuleLength = contextLengths[rule.LeftHandSide] + GetSymbolSequenceLength(rule.RightHandSide.AsSpan(), minLengths);
                    if (currentRuleLength > maxRuleLength)
                        maxRuleLength = currentRuleLength;
                }
            }
            if (maxRuleLength == int.MaxValue)
                return (true, null);
            return (false, GenerateAllStringsCore(maxRuleLength, nonterminals, null));
        }

        private (bool underfits, bool unproductive, int[] CountsByLength) GetGrammarShapeCore(Dictionary<int, int> minLengths, HashSet<int> nonterminals, int[] maxEvidenceByLength)
        {
            // Step 2: Calculate the length of the shortest "context" needed to derive each non-terminal.
            var contextLengths = CalculateContextLengths(minLengths, nonterminals);

            // Step 3: For each rule, calculate the length of the shortest string that MUST use that rule,
            // and find the maximum among them.
            int maxRuleLength = 0;
            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    // The correct formula: L_r = c(A) + min_len(alpha)
                    int currentRuleLength = contextLengths[rule.LeftHandSide] + GetSymbolSequenceLength(rule.RightHandSide.AsSpan(), minLengths);

                    if (currentRuleLength > maxRuleLength)
                    {
                        maxRuleLength = currentRuleLength;
                    }
                }
            }

            if (maxRuleLength == int.MaxValue)
                return (false, true, null);

            // If the grammar's deepest rule is first exercised at a length beyond the evidence window,
            // the grammar is guaranteed to generate strings at that length — strings with no evidence
            // support. Reject immediately without running the DP.
            if (maxRuleLength > maxEvidenceByLength.Length - 1)
                return (true, false, null);

            var counts = GenerateAllStringsCore(maxRuleLength, nonterminals, maxEvidenceByLength);
            return counts == null ? (true, false, null) : (false, false, counts);
        }

        private HashSet<int> CollectAllNonterminals()
        {
            var nonterminals = new HashSet<int>(RulesByLHS.Length * 4);
            CollectAllNonterminalsInto(nonterminals);
            return nonterminals;
        }

        private void CollectAllNonterminalsInto(HashSet<int> nonterminals)
        {
            nonterminals.Clear();
            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    nonterminals.Add(rule.LeftHandSide);

                    var rhsSpan = rule.RightHandSide.AsSpan();
                    for (int j = 0; j < rhsSpan.Length; j++)
                    {
                        nonterminals.Add(rhsSpan[j]);
                    }
                }
            }
        }

        /// <summary>
        /// STEP 1: Calculates min_len(X) for every non-terminal X.
        /// This is the length of the shortest terminal string that can be derived from X.
        /// Complexity: O(|V| * |G|) where |V| is the number of non-terminals and |G| is the total grammar size.
        /// </summary>
        private Dictionary<int, int> CalculateMinLengths(HashSet<int> nonterminals)
        {
            var minLens = new Dictionary<int, int>(nonterminals.Count);
            CalculateMinLengthsInto(nonterminals, null, minLens);
            return minLens;
        }

        private Dictionary<int, int> CalculateMinLengths(HashSet<int> nonterminals, HashSet<int> treatAsTerminal)
        {
            var minLens = new Dictionary<int, int>(nonterminals.Count);
            CalculateMinLengthsInto(nonterminals, treatAsTerminal, minLens);
            return minLens;
        }

        private void CalculateMinLengthsInto(HashSet<int> nonterminals, HashSet<int> treatAsTerminal, Dictionary<int, int> minLens)
        {
            minLens.Clear();

            // Single pass initialization - more cache-friendly
            foreach (var nt in nonterminals)
            {
                minLens[nt] = (Grammar.PartsOfSpeech.Contains(nt) || (treatAsTerminal != null && treatAsTerminal.Contains(nt))) ? 1 : int.MaxValue;
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var ruleList in _nonNullRuleLists)
                {
                    var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                    for (int i = 0; i < rulesSpan.Length; i++)
                    {
                        ref readonly var rule = ref rulesSpan[i];

                        // Use span-based calculation for better performance
                        int bodyLength = GetSymbolSequenceLength(rule.RightHandSide.AsSpan(), minLens);

                        if (bodyLength != int.MaxValue && bodyLength < minLens[rule.LeftHandSide])
                        {
                            minLens[rule.LeftHandSide] = bodyLength;
                            changed = true;
                        }
                    }
                }
            } while (changed);
        }

        private static int GetSymbolSequenceLength(ReadOnlySpan<int> rightHandSide, IReadOnlyDictionary<int, int> minLengths)
        {
            int totalLength = 0;
            for (int i = 0; i < rightHandSide.Length; i++)
            {
                int symbolLength = GetSymbolLength(rightHandSide[i], minLengths);
                if (symbolLength == int.MaxValue)
                {
                    return int.MaxValue; // If any part is unreachable, the whole sequence is.
                }
                totalLength += symbolLength;
            }
            return totalLength;
        }

        private static int GetSymbolLength(int nt, IReadOnlyDictionary<int, int> minLengths)
        {
            if (Grammar.PartsOfSpeech.Contains(nt))
            {
                return 1;
            }

            if (!minLengths.TryGetValue(nt, out var length))
            {
                return int.MaxValue;
            }
            return length;
        }

        /// <summary>
        /// STEP 2: Calculates c(X) for every non-terminal X.
        /// This is the length of the shortest "context" string required to generate X in a full derivation from S.
        /// Implemented using Dijkstra's algorithm.
        /// Complexity: O(|G| * log|V|)
        /// </summary>
        private Dictionary<int, int> CalculateContextLengths(IReadOnlyDictionary<int, int> minLengths, HashSet<int> nonterminals)
        {
            return CalculateContextLengths(minLengths, nonterminals, null);
        }

        private Dictionary<int, int> CalculateContextLengths(IReadOnlyDictionary<int, int> minLengths, HashSet<int> nonterminals, HashSet<int> treatAsTerminal)
        {
            // Reuse per-thread scratch for both the context-length map and the Dijkstra priority queue.
            var contextLens = t_scratchCtxLens ??= new Dictionary<int, int>(nonterminals.Count);
            contextLens.Clear();
            foreach (var nt in nonterminals)
            {
                contextLens[nt] = int.MaxValue; // Initialize with infinity
            }

            var startSymbolId = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);
            contextLens[startSymbolId] = 0; // Context of the start symbol is 0.

            // Priority queue for Dijkstra's algorithm. Stores (NonTerminal, Cost).
            var pq = t_scratchCtxPQ ??= new PriorityQueue<int, int>();
            pq.Clear();
            pq.Enqueue(startSymbolId, 0);

            while (pq.TryDequeue(out var currentNt, out int currentCost))
            {
                // If we've found a shorter path already, skip.
                if (currentCost > contextLens[currentNt])
                {
                    continue;
                }

                // Find all rules where 'currentNt' is the head (left side).
                var rulesForCurrentNt = RulesByLHS[currentNt];
                if (rulesForCurrentNt != null)
                {
                    var rulesSpan = CollectionsMarshal.AsSpan(rulesForCurrentNt);
                    for (int ruleIdx = 0; ruleIdx < rulesSpan.Length; ruleIdx++)
                    {
                        ref readonly var rule = ref rulesSpan[ruleIdx];
                        var rhsSpan = rule.RightHandSide.AsSpan();

                        // For a rule B -> alpha A beta, the context for A is c(B) + len(alpha) + len(beta).
                        // This is where we calculate the new costs.
                        int leftContextLength = 0;
                        for (int i = 0; i < rhsSpan.Length; i++)
                        {
                            var symbol = rhsSpan[i];

                            // Check if the symbol is a non-terminal (not a part of speech or treated as terminal)
                            if (!Grammar.PartsOfSpeech.Contains(symbol) && !(treatAsTerminal != null && treatAsTerminal.Contains(symbol)))
                            {
                                // Calculate the min_len of the right context using span slice - no allocation
                                var rightContextSpan = rhsSpan[(i + 1)..];
                                int rightContextLength = GetSymbolSequenceLength(rightContextSpan, minLengths);

                                if (rightContextLength != int.MaxValue)
                                {
                                    // The new cost for this non-terminal (symbol) is the current context
                                    // plus the length of everything else in the rule body.
                                    int newCost = currentCost + leftContextLength + rightContextLength;

                                    if (newCost < contextLens[symbol])
                                    {
                                        contextLens[symbol] = newCost;
                                        pq.Enqueue(symbol, newCost);
                                    }
                                }
                            }

                            // Add the current symbol's min_len to the left context for the next symbol.
                            leftContextLength += GetSymbolLength(symbol, minLengths);
                        }
                    }
                }
            }

            return contextLens;
        }

        /// <summary>
        /// Computes the minimum sentence length at which any of the given target rules
        /// could first be exercised in a derivation from S.
        /// Nonterminals in treatAsTerminal are assumed to have minLength = 1
        /// (they will receive POS assignments via wildcard parsing).
        /// Returns int.MaxValue if no target rule can be reached.
        /// </summary>
        public int ComputeMinSentenceLengthForRules(IReadOnlyList<Rule> targetRules, HashSet<int> treatAsTerminal)
        {
            var nonterminals = new HashSet<int>(RulesByLHS.Length * 4);
            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    nonterminals.Add(rule.LeftHandSide);
                    var rhsSpan = rule.RightHandSide.AsSpan();
                    for (int j = 0; j < rhsSpan.Length; j++)
                    {
                        nonterminals.Add(rhsSpan[j]);
                    }
                }
            }

            var minLengths = CalculateMinLengths(nonterminals, treatAsTerminal);
            var contextLengths = CalculateContextLengths(minLengths, nonterminals, treatAsTerminal);

            int minSentenceLength = int.MaxValue;
            for (int i = 0; i < targetRules.Count; i++)
            {
                var rule = targetRules[i];
                if (!contextLengths.TryGetValue(rule.LeftHandSide, out var ctx) || ctx == int.MaxValue)
                    continue;

                int rhsLen = GetSymbolSequenceLength(rule.RightHandSide.AsSpan(), minLengths);
                if (rhsLen == int.MaxValue)
                    continue;

                int ruleLength = ctx + rhsLen;
                if (ruleLength < minSentenceLength)
                {
                    minSentenceLength = ruleLength;
                }
            }

            return minSentenceLength;
        }

        // Lattice grammar representation:
        // - START -> Xi
        // - Xi -> Xj Xk
        // - Xi, Xj, and Xk are never POS symbols
        // - Xi -> POS
        // - do not include START ->  (epsilon rule) here; epsilon is handled outside/after learning
        /// <summary>
        /// Generates all strings derivable from the start symbol up to a given maximum length,
        /// correctly handling epsilon productions and unit rules.
        /// </summary>
        /// <param name="maxLen">The maximum length of strings to generate.</param>
        /// <param name="nonterminals">A set of all non-terminal symbol IDs.</param>
        /// <returns></returns>
        public int[] GenerateAllStrings(int maxLen, HashSet<int> nonterminals)
            => GenerateAllStringsCore(maxLen, nonterminals, null);

        private HashSet<string>[] GenerateAllPartsOfSpeechSequencesCore(int maxLen, HashSet<int> nonterminals)
        {
            var symbolTable = SymbolTable.Instance;
            var symbols = new HashSet<int>(nonterminals);
            foreach (var pos in PartsOfSpeech)
                symbols.Add(pos);

            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    symbols.Add(rule.LeftHandSide);
                    var rhsSpan = rule.RightHandSide.AsSpan();
                    for (int j = 0; j < rhsSpan.Length; j++)
                        symbols.Add(rhsSpan[j]);
                }
            }

            var dp = new Dictionary<int, HashSet<string>[]>(symbols.Count);
            foreach (var symbol in symbols)
                dp[symbol] = CreateEmptySequenceBuckets(maxLen);

            foreach (var pos in PartsOfSpeech)
            {
                if (!dp.TryGetValue(pos, out var buckets))
                {
                    buckets = CreateEmptySequenceBuckets(maxLen);
                    dp[pos] = buckets;
                }

                if (maxLen >= 1)
                    buckets[1].Add(symbolTable.GetSymbol(pos));
            }

            var additions = new List<(int Length, string Sequence)>();
            bool changed;
            do
            {
                changed = false;

                foreach (var ruleList in _nonNullRuleLists)
                {
                    var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                    for (int i = 0; i < rulesSpan.Length; i++)
                    {
                        ref readonly var rule = ref rulesSpan[i];
                        additions.Clear();
                        CollectRightHandSideSequences(rule.RightHandSide, 0, 0, string.Empty, maxLen, dp, additions);

                        var lhsBuckets = dp[rule.LeftHandSide];
                        for (int j = 0; j < additions.Count; j++)
                        {
                            var addition = additions[j];
                            if (lhsBuckets[addition.Length].Add(addition.Sequence))
                                changed = true;
                        }
                    }
                }
            } while (changed);

            int startSymbolId = symbolTable.GetId(Grammar.StartSymbol);
            if (!dp.TryGetValue(startSymbolId, out var startBuckets))
                return CreateEmptySequenceBuckets(maxLen);

            return startBuckets;
        }

        private static HashSet<string>[] CreateEmptySequenceBuckets(int maxLen)
        {
            var buckets = new HashSet<string>[maxLen + 1];
            for (int i = 0; i < buckets.Length; i++)
                buckets[i] = new HashSet<string>();

            return buckets;
        }

        private static void CollectRightHandSideSequences(
            int[] rightHandSide,
            int index,
            int currentLength,
            string currentSequence,
            int maxLen,
            Dictionary<int, HashSet<string>[]> dp,
            List<(int Length, string Sequence)> additions)
        {
            if (currentLength > maxLen)
                return;

            if (index == rightHandSide.Length)
            {
                additions.Add((currentLength, currentSequence));
                return;
            }

            if (!dp.TryGetValue(rightHandSide[index], out var symbolBuckets))
                return;

            for (int length = 0; currentLength + length <= maxLen; length++)
            {
                var sequences = symbolBuckets[length];
                foreach (var sequence in sequences)
                {
                    CollectRightHandSideSequences(
                        rightHandSide,
                        index + 1,
                        currentLength + length,
                        ConcatenatePartOfSpeechSequences(currentSequence, sequence),
                        maxLen,
                        dp,
                        additions);
                }
            }
        }

        private static string ConcatenatePartOfSpeechSequences(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
                return right;

            if (string.IsNullOrEmpty(right))
                return left;

            return string.Concat(left, " ", right);
        }

        // Returns null if the grammar generates more distinct strings of some length l than maxEvidenceByLength[l].
        private int[] GenerateAllStringsCore(int maxLen, HashSet<int> nonterminals, int[] maxEvidenceByLength)
        {
            // Fast path: compact ulong encoding — each POS tag gets a compact index and is packed
            // into a ulong positionally. Zero allocation in the inner loop, O(1) hash per string.
            int posAlphabetSize = Grammar.PartsOfSpeech.Count;
            // posToIndex and bitsPerSymbol depend only on PartsOfSpeech (invariant across the search);
            // cached globally to avoid rebuilding per call.
            var (posToIndex, bitsPerSymbol) = GetCachedPosToIndex();
            if ((long)bitsPerSymbol * maxLen <= 64)
            {
                return GenerateAllStringsCore_Fast(maxLen, nonterminals, maxEvidenceByLength, bitsPerSymbol, posToIndex);
            }

            Console.WriteLine($"[Warning] GenerateAllStringsCore: ulong encoding requires {(long)bitsPerSymbol * maxLen} bits " +
                              $"(posAlphabetSize={posAlphabetSize}, maxLen={maxLen}). Falling back to string-based computation.");

            // String-based fallback (rare; triggered only when POS-count * maxLen > 64 bits).
            // Applies the same structural optimizations as the fast path where possible.
            var symbolTable = SymbolTable.Instance;
            int StartSymbolId = symbolTable.GetId(Grammar.StartSymbol);

            // Opt #2: Pre-classify rules once into typed lists — avoids rescanning all rules per length.
            var fbEpsilonLhsList = new List<int>();
            var fbTerminalRules = new List<(int lhsId, string termStr)>();
            var fbBinaryRules = new List<(int lhsId, int yId, int zId)>();
            var fbUnitRules = new List<(int lhsId, int yId)>();

            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    int rhsLen = rule.RightHandSide.Length;
                    if (rhsLen == 0)
                    {
                        fbEpsilonLhsList.Add(rule.LeftHandSide);
                    }
                    else if (rhsLen == 1)
                    {
                        int sym = rule.RightHandSide[0];
                        if (rule.IsLatticePosAssignmentRule())
                            fbTerminalRules.Add((rule.LeftHandSide, symbolTable.GetSymbol(sym)));
                        else if (nonterminals.Contains(sym))
                            fbUnitRules.Add((rule.LeftHandSide, sym));
                    }
                    else if (rhsLen == 2)
                    {
                        fbBinaryRules.Add((rule.LeftHandSide, rule.RightHandSide[0], rule.RightHandSide[1]));
                    }
                }
            }

            // Opt #6: gate unit-rule fixed-point entirely if no unit rules exist.
            bool fbHasUnitRules = fbUnitRules.Count > 0;

            // DP table: dp[ntId][length] -> set of distinct strings of that length derivable from ntId.
            var dp = new Dictionary<int, Dictionary<int, HashSet<string>>>(nonterminals.Count);
            foreach (var ntId in nonterminals)
                dp[ntId] = new Dictionary<int, HashSet<string>>(maxLen + 1);

            // Opt #5: track which lengths are populated per NT to skip null splits in binary rules.
            var fbPopulatedLengths = new Dictionary<int, List<int>>(nonterminals.Count);
            foreach (var ntId in nonterminals)
                fbPopulatedLengths[ntId] = new List<int>();

            for (int l = 0; l <= maxLen; l++)
            {
                // Step 1a: Epsilon rules (l == 0 only)
                if (l == 0)
                {
                    foreach (int lhsId in fbEpsilonLhsList)
                    {
                        if (!dp[lhsId].TryGetValue(0, out var value))
                        {
                            value = new HashSet<string>();
                            dp[lhsId][0] = value;
                            fbPopulatedLengths[lhsId].Add(0);
                        }
                        value.Add("");
                    }
                }

                // Step 1b: Terminal (POS assignment) rules (l == 1 only)
                if (l == 1)
                {
                    foreach (var (lhsId, termStr) in fbTerminalRules)
                    {
                        if (!dp[lhsId].TryGetValue(1, out var value))
                        {
                            value = new HashSet<string>();
                            dp[lhsId][1] = value;
                            fbPopulatedLengths[lhsId].Add(1);
                        }
                        value.Add(termStr);
                    }
                }

                // Step 2: Binary rules X -> Y Z (l > 0)
                // Opt #4: cache dp[lhs] and dest before the split loop.
                // Opt #5: iterate fbPopulatedLengths[yId] (sorted, non-null) instead of scanning 0..l.
                if (l > 0)
                {
                    foreach (var (lhsId, yId, zId) in fbBinaryRules)
                    {
                        if (!dp.TryGetValue(yId, out var dpY) || !dp.TryGetValue(zId, out var dpZ))
                            continue;

                        var dpLhs = dp[lhsId];
                        dpLhs.TryGetValue(l, out var dest); // null until first valid split

                        var popsY = fbPopulatedLengths[yId];
                        for (int pi = 0; pi < popsY.Count; pi++)
                        {
                            int lenY = popsY[pi];
                            if (lenY > l) break; // sorted ascending; no further valid splits possible

                            int lenZ = l - lenY;
                            if (!dpZ.TryGetValue(lenZ, out var stringsZ)) continue;

                            var stringsY = dpY[lenY]; // non-null by construction (lives in fbPopulatedLengths)
                            if (dest == null)
                            {
                                dest = new HashSet<string>();
                                dpLhs[l] = dest;
                                fbPopulatedLengths[lhsId].Add(l);
                            }

                            foreach (string sY in stringsY)
                                foreach (string sZ in stringsZ)
                                    dest.Add(string.Concat(sY, " ", sZ));
                        }
                    }
                }

                // Step 3: Unit rules X -> Y (fixed-point; opt #6: skipped entirely if none exist)
                if (fbHasUnitRules)
                {
                    bool changedInIteration;
                    do
                    {
                        changedInIteration = false;
                        foreach (var (lhsId, yId) in fbUnitRules)
                        {
                            if (!dp[yId].TryGetValue(l, out var stringsY)) continue;

                            if (!dp[lhsId].TryGetValue(l, out var stringsX))
                            {
                                stringsX = new HashSet<string>();
                                dp[lhsId][l] = stringsX;
                                fbPopulatedLengths[lhsId].Add(l);
                            }

                            int countBefore = stringsX.Count;
                            stringsX.UnionWith(stringsY);
                            if (stringsX.Count > countBefore) changedInIteration = true;
                        }
                    } while (changedInIteration);
                }

                if (maxEvidenceByLength != null && l < maxEvidenceByLength.Length)
                {
                    int count = dp.TryGetValue(StartSymbolId, out var startDpEarly) && startDpEarly.TryGetValue(l, out var startStringsEarly)
                        ? startStringsEarly.Count : 0;
                    if (count > maxEvidenceByLength[l])
                        return null;
                }
            }

            var countsByLength = new int[maxLen + 1];
            if (dp.TryGetValue(StartSymbolId, out var startSymbolDp))
                for (int l = 0; l <= maxLen; l++)
                    if (startSymbolDp.TryGetValue(l, out var stringsOfLengthL))
                        countsByLength[l] = stringsOfLengthL.Count;

            return countsByLength;
        }

        // Fast path for GenerateAllStringsCore: uses compact ulong encoding instead of HashSet<string>.
        // Each POS tag is assigned a compact index (0..posAlphabetSize-1) that fits in bitsPerSymbol bits.
        // A string of POS tags [p0, p1, ..., pL-1] is encoded as:
        //   p0 | (p1 << bitsPerSymbol) | (p2 << 2*bitsPerSymbol) | ...
        // Concatenation is a shift+OR: combine(sY of lenY, sZ) = sY | (sZ << (bitsPerSymbol * lenY))
        // Caller guarantees bitsPerSymbol * maxLen <= 64.
        private int[] GenerateAllStringsCore_Fast(
            int maxLen,
            HashSet<int> nonterminals,
            int[] maxEvidenceByLength,
            int bitsPerSymbol,
            Dictionary<int, int> posToIndex)
        {
            // Opt #3: Map NT IDs to compact 0-based indices for O(1) array access instead of dict lookup.
            int N = nonterminals.Count;
            var ntToIdx = t_scratchNtToIdx ??= new Dictionary<int, int>(N);
            ntToIdx.Clear();
            foreach (int nt in nonterminals)
                ntToIdx[nt] = ntToIdx.Count;

            // ── Rent per-thread DP scratch (jagged arrays + inner HashSets and Lists) ────────
            // dp[ntIdx][length] = set of ulong-encoded strings of that exact length derivable from that NT.
            // The outer arrays are grown on demand; inner HashSet<ulong>/List<int> are rented from pools.
            if (t_scratchDp == null || t_scratchDpSize < N || t_scratchDpMaxLen < maxLen + 1)
            {
                // Grow (or initial allocate). Preserve existing rows if possible to reduce churn.
                var newSize = Math.Max(N, t_scratchDpSize);
                var newMaxLen = Math.Max(maxLen + 1, t_scratchDpMaxLen);
                var newDp = new HashSet<ulong>[newSize][];
                var newPopulated = new List<int>[newSize];
                for (int i = 0; i < newSize; i++)
                {
                    newDp[i] = new HashSet<ulong>[newMaxLen];
                }
                t_scratchDp = newDp;
                t_scratchPopulatedLens = newPopulated;
                t_scratchDpSize = newSize;
                t_scratchDpMaxLen = newMaxLen;
            }
            var dp = t_scratchDp;
            var populatedLengths = t_scratchPopulatedLens;
            // Initialize the N rows we will use (clear cells and rent populatedLengths lists).
            for (int i = 0; i < N; i++)
            {
                // Ensure the inner array is large enough.
                if (dp[i] == null || dp[i].Length < maxLen + 1)
                {
                    dp[i] = new HashSet<ulong>[Math.Max(maxLen + 1, t_scratchDpMaxLen)];
                }
                else
                {
                    // Clear any residual non-null entries from a previous call (shouldn't occur if
                    // we always return sets in the cleanup below, but defensive).
                    var row = dp[i];
                    for (int l = 0; l < row.Length; l++) row[l] = null;
                }
                populatedLengths[i] = RentIntList();
            }

            int startSymbolId = Grammar.s_symbolTable.GetId(Grammar.StartSymbol);
            int startIdx = ntToIdx.TryGetValue(startSymbolId, out int si) ? si : -1;

            // Precompute shift amounts: shiftAmounts[l] = bitsPerSymbol * l.
            // ThreadStatic buffer grown on demand.
            if (t_scratchShiftAmounts == null || t_scratchShiftAmounts.Length < maxLen + 1)
                t_scratchShiftAmounts = new int[maxLen + 1];
            var shiftAmounts = t_scratchShiftAmounts;
            for (int l = 0; l <= maxLen; l++)
                shiftAmounts[l] = bitsPerSymbol * l;

            // Opt #2: Pre-classify rules into typed flat lists once, before the DP loop.
            // Avoids scanning all rules and checking their type on every length iteration.
            var epsilonLhsIndices = t_scratchEpsilonLhsIdx ??= new List<int>();
            epsilonLhsIndices.Clear();
            var terminalRules = t_scratchTerminalRules ??= new List<(int lhsIdx, ulong posEncoded)>();
            terminalRules.Clear();
            var binaryRules = t_scratchBinaryRules ??= new List<(int lhsIdx, int yIdx, int zIdx)>();
            binaryRules.Clear();
            var unitRules = t_scratchUnitRules ??= new List<(int lhsIdx, int yIdx)>();
            unitRules.Clear();

            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    if (!ntToIdx.TryGetValue(rule.LeftHandSide, out int lhsIdx))
                        continue;

                    int rhsLen = rule.RightHandSide.Length;
                    if (rhsLen == 0)
                    {
                        epsilonLhsIndices.Add(lhsIdx);
                    }
                    else if (rhsLen == 1)
                    {
                        int sym = rule.RightHandSide[0];
                        if (posToIndex.TryGetValue(sym, out int pIdx))
                            terminalRules.Add((lhsIdx, (ulong)pIdx));
                        else if (ntToIdx.TryGetValue(sym, out int yIdx))
                            unitRules.Add((lhsIdx, yIdx));
                    }
                    else if (rhsLen == 2)
                    {
                        int Y = rule.RightHandSide[0], Z = rule.RightHandSide[1];
                        if (ntToIdx.TryGetValue(Y, out int yIdx) && ntToIdx.TryGetValue(Z, out int zIdx))
                            binaryRules.Add((lhsIdx, yIdx, zIdx));
                    }
                }
            }

            // Opt #6: gate unit-rule fixed-point entirely if no unit rules exist (common case).
            bool hasUnitRules = unitRules.Count > 0;

            bool overGenerated = false;

            for (int l = 0; l <= maxLen; l++)
            {
                // Step 1a: Epsilon rules (l == 0 only)
                if (l == 0)
                {
                    foreach (int lhs in epsilonLhsIndices)
                    {
                        if (dp[lhs][0] == null) { dp[lhs][0] = RentUlongSet(); populatedLengths[lhs].Add(0); }
                        dp[lhs][0].Add(0UL); // empty string encoded as 0
                    }
                }

                // Step 1b: Terminal (POS assignment) rules (l == 1 only)
                if (l == 1)
                {
                    foreach (var (lhs, posEncoded) in terminalRules)
                    {
                        if (dp[lhs][1] == null) { dp[lhs][1] = RentUlongSet(); populatedLengths[lhs].Add(1); }
                        dp[lhs][1].Add(posEncoded);
                    }
                }

                // Step 2: Binary rules X -> Y Z (l > 0)
                // Opt #4: cache dp[lhs] row and dest before the split loop — no repeated array indexing inside.
                // Opt #5: iterate populatedLengths[yIdx] (sorted, non-null only) instead of scanning 0..l.
                if (l > 0)
                {
                    foreach (var (lhs, yIdx, zIdx) in binaryRules)
                    {
                        var dpY = dp[yIdx];
                        var dpZ = dp[zIdx];
                        var dpLhs = dp[lhs];
                        HashSet<ulong> dest = dpLhs[l]; // null until first valid split is found

                        var popsY = populatedLengths[yIdx];
                        for (int pi = 0; pi < popsY.Count; pi++)
                        {
                            int lenY = popsY[pi];
                            if (lenY > l) break; // sorted ascending; no further valid splits possible

                            int lenZ = l - lenY;
                            var stringsZ = dpZ[lenZ];
                            if (stringsZ == null) continue;

                            var stringsY = dpY[lenY]; // non-null by construction (lives in populatedLengths)
                            if (dest == null)
                            {
                                dest = RentUlongSet();
                                dpLhs[l] = dest;
                                populatedLengths[lhs].Add(l);
                            }

                            int shiftZ = shiftAmounts[lenY]; // sZ symbols start at position lenY
                            foreach (ulong sY in stringsY)
                                foreach (ulong sZ in stringsZ)
                                    dest.Add(sY | (sZ << shiftZ));
                        }
                    }
                }

                // Step 3: Unit rules X -> Y (fixed-point; opt #6: skipped entirely if none exist)
                if (hasUnitRules)
                {
                    bool changed;
                    do
                    {
                        changed = false;
                        foreach (var (lhs, yIdx) in unitRules)
                        {
                            var stringsY = dp[yIdx][l];
                            if (stringsY == null) continue;

                            if (dp[lhs][l] == null) { dp[lhs][l] = RentUlongSet(); populatedLengths[lhs].Add(l); }
                            var stringsX = dp[lhs][l];
                            int before = stringsX.Count;
                            stringsX.UnionWith(stringsY);
                            if (stringsX.Count > before) changed = true;
                        }
                    } while (changed);
                }

                // Early exit: grammar over-generates relative to evidence at this length
                if (maxEvidenceByLength != null && l < maxEvidenceByLength.Length)
                {
                    int count = startIdx >= 0 && dp[startIdx][l] != null ? dp[startIdx][l].Count : 0;
                    if (count > maxEvidenceByLength[l])
                    {
                        overGenerated = true;
                        break;
                    }
                }
            }

            // Collect counts for the start symbol (only if not short-circuited)
            int[] countsByLength = null;
            if (!overGenerated)
            {
                countsByLength = new int[maxLen + 1];
                if (startIdx >= 0)
                    for (int l = 0; l <= maxLen; l++)
                        countsByLength[l] = dp[startIdx][l]?.Count ?? 0;
            }

            // Return all rented HashSet<ulong> cells and populatedLengths lists to their pools.
            for (int i = 0; i < N; i++)
            {
                var pops = populatedLengths[i];
                var row = dp[i];
                for (int pi = 0; pi < pops.Count; pi++)
                {
                    int l = pops[pi];
                    ReturnUlongSet(row[l]);
                    row[l] = null;
                }
                ReturnIntList(pops);
                populatedLengths[i] = null;
            }

            return countsByLength;
        }


        private struct FlatRule
        {
            public const int Terminal = -1;
            public const int Unary = -2;

            public int TargetIdx;
            public int LeftIdx;   // Terminal = terminal (contributes z)
            public int RightIdx;  // Unary = unary rule (no right operand)

            public bool IsUnary => RightIdx == Unary;
        }

        // Fills the two out-buffers (sortedRules and ruleStart) without allocating per call.
        // The ThreadStatic scratch list grows as needed; flatArr is resized only when it must.
        private void PreCompileInto(Dictionary<int, int> ntMap, int size,
            out FlatRule[] sortedRules, out int[] ruleStart)
        {
            var flatList = t_scratchFlatList ??= new List<FlatRule>();
            flatList.Clear();
            var partsOfSpeech = PartsOfSpeech;

            foreach (var ruleList in _nonNullRuleLists)
            {
                if (ruleList.Count == 0) continue;
                int target = ntMap[ruleList[0].LeftHandSide];
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];
                    var rhs = rule.RightHandSide;
                    int left = partsOfSpeech.Contains(rhs[0])
                        ? FlatRule.Terminal
                        : ntMap[rhs[0]];
                    int right = rhs.Length == 2
                        ? (partsOfSpeech.Contains(rhs[1]) ? FlatRule.Terminal : ntMap[rhs[1]])
                        : FlatRule.Unary;
                    flatList.Add(new FlatRule { TargetIdx = target, LeftIdx = left, RightIdx = right });
                }
            }

            int sortedRulesCount = flatList.Count;
            if (t_scratchFlatArr == null || t_scratchFlatArr.Length < sortedRulesCount)
                t_scratchFlatArr = new FlatRule[Math.Max(sortedRulesCount, 16)];
            CollectionsMarshal.AsSpan(flatList).CopyTo(t_scratchFlatArr);
            sortedRules = t_scratchFlatArr;

            if (t_scratchRuleStart == null || t_scratchRuleStart.Length < size + 1)
                t_scratchRuleStart = new int[Math.Max(size + 1, 16)];
            ruleStart = t_scratchRuleStart;
            int ri = 0;
            for (int j = 0; j < size; j++)
            {
                ruleStart[j] = ri;
                while (ri < sortedRulesCount && sortedRules[ri].TargetIdx == j)
                    ri++;
            }
            ruleStart[size] = ri;
        }


        private bool IsConvergent(double z, int size, FlatRule[] sortedRules, int[] ruleStart)
        {
            Span<double> ntValues = stackalloc double[size];
            Span<double> nextValues = stackalloc double[size];
            ntValues.Clear();

            const int maxIters = 100;
            const double limit = 1e8;

            for (int iter = 0; iter < maxIters; iter++)
            {
                double delta = 0.0;

                for (int j = 0; j < size; j++)
                {
                    double sum = 0.0;

                    int end = ruleStart[j + 1];
                    for (int ri = ruleStart[j]; ri < end; ri++)
                    {
                        ref readonly var r = ref sortedRules[ri];
                        double v1 = r.LeftIdx == FlatRule.Terminal ? z : ntValues[r.LeftIdx];
                        if (r.IsUnary)
                        {
                            sum += v1;
                        }
                        else
                        {
                            double v2 = r.RightIdx == FlatRule.Terminal ? z : ntValues[r.RightIdx];
                            sum += v1 * v2;
                        }
                    }

                    if (sum > limit || double.IsNaN(sum) || double.IsInfinity(sum))
                    {
                        return false;
                    }

                    delta += Math.Abs(sum - ntValues[j]);
                    nextValues[j] = sum;
                }

                var tmp = ntValues;
                ntValues = nextValues;
                nextValues = tmp;

                if (delta < 1e-9)
                {
                    return true;
                }
            }

            return false;
        }

        public double CalculateLambda() => CalculateLambda(double.PositiveInfinity, out _);

        public double CalculateLambda(double pruneIfLambdaAtLeast, out bool exact)
        {
            var ntMap = t_scratchNtMap ??= new Dictionary<int, int>();
            ntMap.Clear();
            var partsOfSpeech = PartsOfSpeech;

            int counter = 0;
            foreach (var ruleList in _nonNullRuleLists)
            {
                if (ruleList.Count == 0) continue;
                int lhs = ruleList[0].LeftHandSide;
                if (!ntMap.ContainsKey(lhs))
                    ntMap[lhs] = counter++;
            }

            foreach (var ruleList in _nonNullRuleLists)
            {
                var rulesSpan = CollectionsMarshal.AsSpan(ruleList);
                for (int i = 0; i < rulesSpan.Length; i++)
                {
                    ref readonly var rule = ref rulesSpan[i];

                    // Right-hand side
                    var rhsSpan = rule.RightHandSide.AsSpan();
                    for (int j = 0; j < rhsSpan.Length; j++)
                    {
                        int symbol = rhsSpan[j];
                        if (partsOfSpeech.Contains(symbol))
                            continue;

                        if (!ntMap.ContainsKey(symbol))
                        {
                            ntMap[symbol] = counter++;
                        }
                    }
                }
            }

            int size = ntMap.Count;
            // 1. Bake the grammar into pooled flat arrays (no per-call heap alloc)
            PreCompileInto(ntMap, size, out FlatRule[] sortedRules, out int[] ruleStart);

            double lowZ = 0.0;
            double highZ = 1.0;
            exact = true;
            bool canEarlyPrune = !double.IsInfinity(pruneIfLambdaAtLeast)
                && !double.IsNaN(pruneIfLambdaAtLeast)
                && pruneIfLambdaAtLeast > 0.0;

            // Binary search for the radius of convergence
            for (int i = 0; i < 30; i++)
            {
                // Precision check: if the difference is smaller than 0.0001 lambda, stop.
                if (highZ - lowZ < 1e-7) break;

                double midZ = (lowZ + highZ) / 2.0;

                if (IsConvergent(midZ, size, sortedRules, ruleStart))
                    lowZ = midZ;
                else
                    highZ = midZ;

                if (canEarlyPrune && 1.0 / highZ >= pruneIfLambdaAtLeast)
                {
                    exact = false;
                    Lambda = 1.0 / highZ;
                    return Lambda;
                }
            }

            Lambda = 1.0 / lowZ;
            return Lambda;
        }
    }
}
