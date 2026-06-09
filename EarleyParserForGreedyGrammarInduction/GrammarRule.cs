// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Text.Json.Serialization;

namespace EarleyParserForGreedyGrammarInduction
{

    /// <summary>
    /// This class implements a production rule, which is of the form: Left Hand Side -> Right Hand Side
    /// Left hand side can be a single nonterminal, right hand side can be a sequence of nonterminals. 
    /// see class description of Nonterminal for more details about the nonterminal type.
    /// (Right hand side with length 0 expands to the empty string, i.e. an epsilon rule)
    /// </summary>
    public readonly struct Rule : IEquatable<Rule>
    {
        public int LeftHandSide { get; }
        public int[] RightHandSide { get; }
        [JsonIgnore]
        public int Type { get; }

        public Rule(Rule r)
        {
            LeftHandSide = r.LeftHandSide;
            RightHandSide = new int[r.RightHandSide.Length];
            Array.Copy(r.RightHandSide, RightHandSide, r.RightHandSide.Length);
        }

        public Rule(int leftHandSide, int[] rightHandSide, int type)
        {
            LeftHandSide = leftHandSide;
            if (rightHandSide != null)
            {
                var length = rightHandSide.Length;
                RightHandSide = new int[length];
                // Use Array.Copy for better performance than manual loop
                Array.Copy(rightHandSide, RightHandSide, length);
            }
            else
            {
                RightHandSide = Array.Empty<int>();
            }
            Type = type;
        }
        public bool IsLatticePosAssignmentRule()
        {
            return RightHandSide.Length > 0 && Grammar.PartsOfSpeech.Contains(RightHandSide[0]);
        }

        public Rule(int leftHandSide, int[] rightHandSide) : this(leftHandSide, rightHandSide, 0)
        {
        }

        public string ToFormattedStackString()
        {
            var rhsParts = new string[RightHandSide.Length];
            for (int i = 0; i < RightHandSide.Length; i++)
            {
                rhsParts[i] = SymbolTable.Instance.GetSymbol(RightHandSide[i]);
            }
            var rhs = string.Join(" ", rhsParts);
            return $"{SymbolTable.Instance.GetSymbol(LeftHandSide)} -> {rhs}";
        }


        public override string ToString()
        {
            // This is not ideal as it doesn't have access to the symbol table.
            // For debugging purposes, it might be better to use ToFormattedStackString.
            var rhs = string.Join(" ", RightHandSide);
            return $"{LeftHandSide} -> {rhs}";
        }

        public bool Equals(Rule other)
        {
            if (LeftHandSide != other.LeftHandSide)
            {
                return false;
            }

            if (RightHandSide.Length != other.RightHandSide.Length)
            {
                return false;
            }

            for (var i = 0; i < RightHandSide.Length; i++)
            {
                if (RightHandSide[i] != other.RightHandSide[i])
                {
                    return false;
                }
            }

            return true;
        }
        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(LeftHandSide);
            for (var i = 0; i < RightHandSide.Length; i++)
            {
                hc.Add(RightHandSide[i]);
            }
            return hc.ToHashCode();
        }

        public static bool operator ==(Rule left, Rule right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Rule left, Rule right)
        {
            return !(left == right);
        }

        public override bool Equals(object obj) => obj is Rule other && Equals(other);
    }
}
