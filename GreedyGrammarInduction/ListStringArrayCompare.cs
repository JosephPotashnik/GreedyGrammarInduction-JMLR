// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;

namespace GreedyGrammarInduction
{
    class ListIntArrayCompare : IEqualityComparer<List<int[]>>
    {
        public static ListIntArrayCompare Shared { get; } = new ListIntArrayCompare();

        private ListIntArrayCompare() { }

        public bool Equals(List<int[]> x, List<int[]> y)
        {
            if (x.Count != y.Count)
            {
                return false;
            }
            var ySet = new HashSet<int[]>(y, IntArrayComparer.Shared);
            for (int i = 0; i < x.Count; i++)
            {
                if (!ySet.Contains(x[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(List<int[]> obj)
        {
            var hashCode = new HashCode();
            foreach (var element in obj)
            {
                foreach (var childElement in element)
                {
                    hashCode.Add(childElement);
                }
            }

            return hashCode.ToHashCode();
        }
    }
}
