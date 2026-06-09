// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;

namespace EarleyParserForGreedyGrammarInduction
{
    public class EarleyStatePooledObjectPolicy : IPooledObjectPolicy<EarleyState>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EarleyState Create()
        {
            return new EarleyState();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Return(EarleyState _)
        {

            return true;
        }
    }

    public class EarleyStatePool
    {
        private readonly ObjectPool<EarleyState> _pool;
        private const int InitialPoolSize = 500;
        private const int MaxPoolSize = 10000;

        public EarleyStatePool()
        {
            var policy = new EarleyStatePooledObjectPolicy();
            _pool = new DefaultObjectPool<EarleyState>(policy, MaxPoolSize);

            // Pre-populate the pool more efficiently
            var states = new EarleyState[InitialPoolSize];
            for (int i = 0; i < InitialPoolSize; i++)
            {
                states[i] = _pool.Get();
            }
            for (int i = 0; i < InitialPoolSize; i++)
            {
                _pool.Return(states[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EarleyState Rent(Rule r, int dotIndex, EarleyColumn c)
        {
            var state = _pool.Get();
            state.Rule = r;
            state.DotIndex = dotIndex;
            state.StartColumn = c;
            return state;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(EarleyState state)
        {
            state.EndColumn = null;
            state.Predecessor = null;
            state.ReductorSpan = null;
            _pool.Return(state);
        }
    }
}
