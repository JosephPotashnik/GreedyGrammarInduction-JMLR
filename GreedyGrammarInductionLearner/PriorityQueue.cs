// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;

namespace GreedyGrammarInductionLearner;
public class PriorityQueue
{
    private readonly SortedDictionary<ushort, Queue<ushort[]>> _priorityQueues;

    public PriorityQueue()
    {
        _priorityQueues = new SortedDictionary<ushort, Queue<ushort[]>>();
    }

    /// <summary>
    /// Gets the first (lowest) key in the sorted dictionary.
    /// </summary>
    private ushort GetFirstKey()
    {
        using var enumerator = _priorityQueues.GetEnumerator();
        enumerator.MoveNext();
        return enumerator.Current.Key;
    }

    /// <summary>
    /// Enqueues an item with the given priority.
    /// </summary>
    /// <param name="priority">The priority of the item (lower is higher priority).</param>
    /// <param name="item">The item to enqueue.</param>
    public void Enqueue(ushort priority, ushort[] set)
    {
        if (!_priorityQueues.TryGetValue(priority, out var value))
        {
            value = new Queue<ushort[]>();
            _priorityQueues[priority] = value;
        }
        value.Enqueue(set);
    }

    /// <summary>
    /// Dequeues the highest-priority item.
    /// </summary>
    /// <returns>The dequeued item.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the queue is empty.</exception>
    public (ushort, ushort[], int) Dequeue()
    {
        if (IsEmpty())
        {
            throw new InvalidOperationException("The priority queue is empty.");
        }

        var priority = GetFirstKey();

        var queue = _priorityQueues[priority];
        int count = queue.Count;
        if (count > 0)
        {
            var set = queue.Dequeue();
            if (queue.Count == 0)
            {
                _priorityQueues.Remove(priority);
            }
            return (priority, set, count);
        }


        throw new InvalidOperationException("The priority queue is empty.");
    }

    /// <summary>
    /// Dequeues the highest-priority item that satisfies the depth constraint.
    /// Automatically discards items that are too deep based on current shortest depth.
    /// </summary>
    /// <param name="currentShortestDepth">Current shortest depth of global optimum</param>
    /// <param name="maxDepthBetweenSubSolutions">Maximum allowed depth difference</param>
    /// <returns>The dequeued item, or null if no valid item exists</returns>
    public (ushort, ushort[], int)? DequeueWithDepthFilter(long currentShortestDepth, int maxDepthBetweenSubSolutions)
    {
        while (!IsEmpty())
        {
            var priority = GetFirstKey();
            var queue = _priorityQueues[priority];

            if (queue.Count == 0)
            {
                _priorityQueues.Remove(priority);
                continue;
            }

            // Check if this priority level is too deep
            if (currentShortestDepth != long.MaxValue &&
                priority - currentShortestDepth > maxDepthBetweenSubSolutions - 1)
            {
                // Discard all items at this priority level and deeper
                _priorityQueues.Remove(priority);
                continue;
            }

            var set = queue.Dequeue();
            int count = queue.Count;

            if (queue.Count == 0)
            {
                _priorityQueues.Remove(priority);
            }

            return (priority, set, count + 1);
        }

        return null;
    }

    internal void Clear()
    {
        _priorityQueues.Clear();
    }

    internal int RemoveWhere(Func<ushort, ushort[], bool> shouldRemove)
    {
        int removed = 0;
        var priorities = new List<ushort>(_priorityQueues.Keys);
        for (int i = 0; i < priorities.Count; i++)
        {
            ushort priority = priorities[i];
            if (!_priorityQueues.TryGetValue(priority, out var queue))
                continue;

            int originalCount = queue.Count;
            if (originalCount == 0)
            {
                _priorityQueues.Remove(priority);
                continue;
            }

            var retained = new Queue<ushort[]>(originalCount);
            while (queue.Count > 0)
            {
                var set = queue.Dequeue();
                if (shouldRemove(priority, set))
                {
                    removed++;
                    continue;
                }

                retained.Enqueue(set);
            }

            if (retained.Count == 0)
            {
                _priorityQueues.Remove(priority);
            }
            else
            {
                _priorityQueues[priority] = retained;
            }
        }

        return removed;
    }

    /// <summary>
    /// Peeks at the depth (priority) of the next item without dequeuing it.
    /// </summary>
    /// <returns>The depth of the next item, or null if the queue is empty.</returns>
    public ushort? PeekDepth()
    {
        if (IsEmpty())
        {
            return null;
        }

        return GetFirstKey();
    }

    /// <summary>
    /// Peeks at the next item without dequeuing it.
    /// </summary>
    /// <returns>Tuple of (depth, set, count) for the next item, or null if empty.</returns>
    public (ushort depth, ushort[] set, int count)? Peek()
    {
        if (IsEmpty())
        {
            return null;
        }

        var priority = GetFirstKey();
        var queue = _priorityQueues[priority];

        if (queue.Count == 0)
        {
            return null;
        }

        var set = queue.Peek();
        int count = queue.Count;

        return (priority, set, count);
    }

    /// <summary>
    /// Checks if the priority queue is empty.
    /// </summary>
    /// <returns>True if empty; otherwise, false.</returns>
    public bool IsEmpty()
    {
        return _priorityQueues.Count == 0;
    }
}
