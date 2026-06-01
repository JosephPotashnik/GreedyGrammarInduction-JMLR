// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// High-performance comparers for arrays and lists with SIMD optimization
    /// </summary>
    public static class ArrayComparers
    {
        public class ArrayComparer : IEqualityComparer<ushort[]>
        {
            public static ArrayComparer Shared { get; } = new ArrayComparer();

            public bool Equals(ushort[] x, ushort[] y)
            {
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x.Length != y.Length)
                {
                    return false;
                }

                return x.AsSpan().SequenceEqual(y);
            }

            public int GetHashCode(ushort[] obj)
            {
                if (obj == null)
                {
                    return 0;
                }

                var hc = new HashCode();
                for (int i = 0; i < obj.Length; i++)
                {
                    hc.Add(obj[i]);
                }
                return hc.ToHashCode();
            }
        }

    }
}
