// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace EarleyParserForGreedyGrammarInduction
{
    //Color is used to detect cycles when visiting completed States (Gray to black transition).
    public enum Color
    {
        White,
        Gray,
        Black
    }


    /// <summary>
    /// This class implements Earley Item / Earley State (Item and State are interchangeable terms).
    /// It contains :
    /// (i) the Rule, the position of the dot, reference to its Start and Earley Set (Earley Column)
    /// (ii) pointers to its antecedents (children): reductorSpan (span with all ambiguous reductors) and predecesssor, if any.
    /// (iii) pointers to its consequents (parents).
    /// (iv) pointer to the Span object if the State is completed. (the Span contains all ambiguous states spanning same input)
    /// (v) boolean fields of Added/Removed. They are used to keep track of the latest reparse(s) changes.
    /// </summary>
    /// 

    public class EarleyState : IEquatable<EarleyState>
    {
        //(i) basic members:
        public Rule Rule { get; set; }
        public EarleyColumn StartColumn { get; set; }
        public EarleyColumn EndColumn { get; set; }
        public int DotIndex { get; set; }
        //(ii) antecedents (children) pointers:
        public EarleyState Predecessor { get; set; }
        public EarleySpan ReductorSpan { get; set; }

        public EarleyState()
        {

        }

        public bool Equals(EarleyState other) => this == other;

        //this function counts the total number of subtrees rooted at this State/Item.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CountDerivations(Dictionary<EarleySpan, int> visited)
        {
            int derivationsFromReductorSpan = 1;
            int derivationsFromPredecessor = 0;

            //reductor
            if (ReductorSpan != null)
            {
                //note: cycles are counted zero times (discarded)
                if (visited.TryGetValue(ReductorSpan, out derivationsFromReductorSpan))
                {
                    if (derivationsFromReductorSpan == 0)
                    {
                        //means GRAY color - a cycle occurred.
                        //leave the number of derivations 0. - do not count cycles.
                        return 0;
                    }
                }
                else
                {
                    derivationsFromReductorSpan = ReductorSpan.CountDerivations(visited);
                }
            }

            //predecessor
            if (DotIndex > 1 && Predecessor != null)
            {
                derivationsFromPredecessor = Predecessor.CountDerivations(visited);
            }

            return derivationsFromPredecessor > 0 ?
                derivationsFromPredecessor * derivationsFromReductorSpan :
                derivationsFromReductorSpan;
        }

        //this function returns the total number of strings rooted at this State/Item
        //strings can be either:
        //(i) bracketed representation of the subtree, e.g. (START (NP (PN John)) (VP (V0 cried)))
        //(ii) parts of speech sequences of the subtree, e.g. PN V0 (PN = proper noun).
        public List<StringBuilder> GetFormattedString(
            Grammar g,
            Dictionary<EarleySpan, Color> visitedSpans,
            Dictionary<EarleyState, Color> visitedStates,
            bool onlyPartsOfSpeechSequence)
        {
            List<StringBuilder> containedSBSReductor = null;
            List<StringBuilder> containedSBSPredecessor = null;
            List<StringBuilder> combinedSBS;

            if (visitedStates.TryGetValue(this, out var stateColor) && stateColor == Color.Gray)
            {
                return new List<StringBuilder>();
            }

            visitedStates[this] = Color.Gray;
            try
            {
                if (ReductorSpan != null)
                {
                    //discard cycles. see documentation in CountDerivations()
                    if (visitedSpans.TryGetValue(ReductorSpan, out var color))
                    {
                        if (color == Color.Gray)
                        {
                            return new List<StringBuilder>();
                        }
                    }

                    containedSBSReductor = ReductorSpan.GetFormattedString(g, visitedSpans, visitedStates, onlyPartsOfSpeechSequence);
                }
                else
                {
                    var sb = new StringBuilder();

                    if (onlyPartsOfSpeechSequence)
                    {
                        if (Grammar.PartsOfSpeech.Contains(Rule.LeftHandSide))
                        {
                            sb.Append(SymbolTable.Instance.GetSymbol(Rule.LeftHandSide));
                        }
                    }
                    else
                    {
                        var rhsLength = Rule.RightHandSide.Length;
                        for (int i = 0; i < rhsLength; i++)
                        {
                            var rhsItem = Rule.RightHandSide[i];
                            if (g.Rules.ContainsKey(rhsItem))
                            {
                                continue;
                            }

                            if (i > 0)
                            {
                                sb.Append(' ');
                            }
                            Console.WriteLine("Asked to call GetFormattedString for string tokens (not just parts of speech");
                            sb.Append(SymbolTable.Instance.GetSymbol(rhsItem));
                        }
                    }

                    containedSBSReductor = new List<StringBuilder> { sb };
                }

                //predecessor
                if (Predecessor != null && Predecessor.ReductorSpan != null)
                {
                    containedSBSPredecessor = Predecessor.GetFormattedString(g, visitedSpans, visitedStates, onlyPartsOfSpeechSequence);
                }

                if (containedSBSPredecessor != null)
                {
                    combinedSBS = new List<StringBuilder>();
                    var predecessorCount = containedSBSPredecessor.Count;
                    var reductorCount = containedSBSReductor.Count;

                    for (int i = 0; i < predecessorCount; i++)
                    {
                        var sb1 = containedSBSPredecessor[i];
                        for (int j = 0; j < reductorCount; j++)
                        {
                            var sb2 = containedSBSReductor[j];
                            var sb3 = new StringBuilder();
                            sb3.Append(sb1);
                            if (sb2.Length != 0)
                            {
                                sb3.Append(' ');
                            }
                            sb3.Append(sb2);
                            combinedSBS.Add(sb3);
                        }
                    }
                }
                else
                {
                    combinedSBS = containedSBSReductor;
                }

                return combinedSBS;
            }
            finally
            {
                visitedStates[this] = Color.Black;
            }
        }

        private static string RuleWithDotNotation(Rule rule, int dotIndex)
        {
            string[] s = new string[rule.RightHandSide.Length + 2];

            s[0] = SymbolTable.Instance.GetSymbol(rule.LeftHandSide) + " -> ";
            s[1 + dotIndex] = "$";
            for (int i = 0; i < dotIndex; i++)
            {
                s[1 + i] = SymbolTable.Instance.GetSymbol(rule.RightHandSide[i]);
            }

            for (int i = dotIndex; i < rule.RightHandSide.Length; i++)
            {
                s[2 + i] = SymbolTable.Instance.GetSymbol(rule.RightHandSide[i]);
            }

            return string.Join(" ", s);
        }


        //print the basic members of the State/Item: the dotted rule and the span indices.
        public override string ToString()
        {
            var endColumnIndex = "None";
            if (EndColumn != null)
            {
                endColumnIndex = EndColumn.Index.ToString();
            }

            return string.Format("{0} [{1}-{2}]", RuleWithDotNotation(Rule, DotIndex),
                StartColumn.Index, endColumnIndex);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 23) + Rule.GetHashCode();
                hash = (hash * 23) + DotIndex;
                hash = (hash * 23) + StartColumn.Index;
                return hash;
            }
        }
        public override bool Equals(object obj) => Equals(obj as EarleyState);
    }
}
