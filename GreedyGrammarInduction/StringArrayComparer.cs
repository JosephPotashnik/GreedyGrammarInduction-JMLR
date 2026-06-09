// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;

namespace GreedyGrammarInduction
{
    sealed class IntArrayComparer : IEqualityComparer<int[]>
    {
        public static IntArrayComparer Shared { get; } = new IntArrayComparer();

        private IntArrayComparer() { }

        public bool Equals(int[] x, int[] y)
        {
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(int[] obj)
        {
            var hashCode = new HashCode();
            foreach (var element in obj)
            {
                hashCode.Add(element);
            }

            return hashCode.ToHashCode();
        }
    }
}
