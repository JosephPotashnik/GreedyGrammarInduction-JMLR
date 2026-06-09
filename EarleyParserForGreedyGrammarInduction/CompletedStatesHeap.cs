// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// this class implements the priority queue that is responsible for the correct BFS order over the completed Items
    /// in the Completed Items Agenda.
    /// </summary>
    internal class CompletedStatesHeap
    {
        private readonly MaxHeap _indicesHeap = new MaxHeap();
        private readonly Dictionary<int, Queue<EarleyState>> _items = [];
        internal int Count => _indicesHeap.Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Enqueue(EarleyState state)
        {
            var index = state.StartColumn.Index;
            if (!_items.TryGetValue(index, out var queue))
            {
                _indicesHeap.Add(index);
                queue = new Queue<EarleyState>(8); // Pre-allocate with reasonable capacity
                _items.Add(index, queue);
            }

            queue.Enqueue(state);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Clear()
        {
            _indicesHeap.Clear();
            _items.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Reset(EarleyStatePool pool)
        {
            foreach (var queue in _items.Values)
            {
                while (queue.Count > 0)
                {
                    pool.Return(queue.Dequeue());
                }
            }
            Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal EarleyState Dequeue()
        {
            var index = _indicesHeap.Max;
            var queue = _items[index];

            var state = queue.Dequeue();
            if (queue.Count == 0)
            {
                _items.Remove(index);
                _indicesHeap.PopMax();
            }

            return state;
        }
    }
}
