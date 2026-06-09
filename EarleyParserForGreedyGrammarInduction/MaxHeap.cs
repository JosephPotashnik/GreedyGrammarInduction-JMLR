// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// A straightforward implementation of a max heap.
    /// </summary>
    public class MaxHeap
    {
        public readonly List<int> Elements = [];
        public int Count => Elements.Count;
        public int Max => Elements[0];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int item)
        {
            Elements.Add(item);
            HeapifyUp(Elements.Count - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => Elements.Clear();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int PopMax()
        {
            var count = Elements.Count;
            if (count > 0)
            {
                var item = Elements[0];
                var lastIndex = count - 1;
                Elements[0] = Elements[lastIndex];
                Elements.RemoveAt(lastIndex);

                if (lastIndex > 0) // Only heapify if there are still elements
                {
                    HeapifyDown(0);
                }
                return item;
            }

            return 0; // throw new InvalidOperationException("no element in heap"); never arrives to this line.
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HeapifyUp(int index)
        {
            var elements = Elements;
            while (index > 0)
            {
                var parent = (index - 1) >> 1; // Bit shift is faster than division
                if (elements[index] <= elements[parent])
                {
                    break;
                }

                // Swap elements
                (elements[index], elements[parent]) = (elements[parent], elements[index]);
                index = parent;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HeapifyDown(int index)
        {
            var elements = Elements;
            var count = elements.Count;

            while (true)
            {
                var largest = index;
                var left = (index << 1) + 1; // Bit shift is faster than multiplication
                var right = left + 1;

                if (left < count && elements[left] > elements[largest])
                {
                    largest = left;
                }

                if (right < count && elements[right] > elements[largest])
                {
                    largest = right;
                }

                if (largest == index)
                {
                    break;
                }

                // Swap elements
                (elements[index], elements[largest]) = (elements[largest], elements[index]);
                index = largest;
            }
        }
    }
}
