// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;

namespace EarleyParserForGreedyGrammarInduction
{
    public class EarleySpanPoolPooledObjectPolicy : IPooledObjectPolicy<EarleySpan>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EarleySpan Create()
        {
            return new EarleySpan();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Return(EarleySpan _)
        {
            return true;
        }
    }

    public class EarleySpanPool
    {
        private readonly ObjectPool<EarleySpan> _pool;
        private const int InitialPoolSize = 300; // Increased for better performance
        private const int MaxPoolSize = 8000;

        public EarleySpanPool()
        {
            var policy = new EarleySpanPoolPooledObjectPolicy();
            _pool = new DefaultObjectPool<EarleySpan>(policy, MaxPoolSize);

            // Pre-populate the pool more efficiently
            var spans = new EarleySpan[InitialPoolSize];
            for (int i = 0; i < InitialPoolSize; i++)
            {
                spans[i] = _pool.Get();
            }
            for (int i = 0; i < InitialPoolSize; i++)
            {
                _pool.Return(spans[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EarleySpan Rent(EarleyState state)
        {
            var span = _pool.Get();
            span.StartColumn = state.StartColumn;
            span.EndColumn = state.EndColumn;
            span.LeftHandSide = state.Rule.LeftHandSide;
            return span;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(EarleySpan span)
        {
            span.Reductors.Clear();
            // Return the object to the pool.
            _pool.Return(span);
        }
    }
}
