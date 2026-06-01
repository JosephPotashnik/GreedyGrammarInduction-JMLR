// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace GreedyGrammarInductionLearner;


public class ArrayCompressor
{
    static public ushort Length;
    public class CompressionRange : IEquatable<CompressionRange>
    {
        public ushort Start { get; set; }
        public ushort End { get; set; }
        public byte Value { get; set; }

        public CompressionRange(ushort start, ushort end, byte value)
        {
            Start = start;
            End = end;
            Value = value;
        }

        public override string ToString()
        {
            return $"[{Start}-{End}] Value: {Value}";
        }

        public override int GetHashCode()
        {
            unchecked // Allow overflow
            {
                int hash = 17;
                hash = (hash * 31) + Start.GetHashCode();
                hash = (hash * 31) + End.GetHashCode();
                hash = (hash * 31) + Value.GetHashCode();
                return hash;
            }
        }

        public bool Equals(CompressionRange other)
        {
            return Start == other.Start && End == other.End && Value == other.Value;
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]

    public static List<CompressionRange> CompressArray(byte[] array)
    {
        List<CompressionRange> compressed = new List<CompressionRange>();

        if (array == null || array.Length == 0)
        {
            return compressed;
        }

        ushort start = 0;
        byte currentValue = array[0];

        for (ushort i = 1; i < array.Length; i++)
        {
            if (array[i] != currentValue)
            {
                if (currentValue != 1)
                {
                    // Add the current range
                    compressed.Add(new CompressionRange(start, (ushort)(i - 1), currentValue));
                }
                // Update the start and current value
                start = i;
                currentValue = array[i];
            }
        }
        if (currentValue != 1)
        {
            // Add the last range
            compressed.Add(new CompressionRange(start, (ushort)(array.Length - 1), currentValue));
        }

        return compressed;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecompressInto(List<CompressionRange> compressed, byte[] destination)
    {
        if (destination == null || destination.Length < Length)
        {
            throw new ArgumentException($"Destination array must be at least {Length} bytes long.");
        }

        // Reset the array to the default state (1 = parsed)
        Array.Fill(destination, (byte)1);

        if (compressed == null || compressed.Count == 0)
        {
            return;
        }

        // Overwrite the specific ranges with their unparsed values (0)
        for (int i = 0; i < compressed.Count; i++)
        {
            var range = compressed[i];
            Array.Fill(destination, range.Value, range.Start, range.End - range.Start + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindFirstValue(List<CompressionRange> compressed, byte value, int length)
    {
        if (compressed == null || compressed.Count == 0)
        {
            return -1;
        }

        for (int i = 0; i < compressed.Count; i++)
        {
            var range = compressed[i];
            if (range.Value == value && range.Start < length)
            {
                return range.Start;
            }
        }

        return -1;
    }


}
