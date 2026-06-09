// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EarleyParserForGreedyGrammarInduction;



[StructLayout(LayoutKind.Sequential)]
public struct CanonicalResult
{
    public IntPtr canonical_labeling;
    public IntPtr vertex_mapping;
    public int n;
    public double group_size;

    public override bool Equals(object _)
    {
        throw new NotImplementedException();
    }

    public override int GetHashCode()
    {
        throw new NotImplementedException();
    }

    public static bool operator ==(CanonicalResult left, CanonicalResult right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(CanonicalResult left, CanonicalResult right)
    {
        return !(left == right);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct CanonicalOrbitResultNative
{
    public IntPtr vertex_mapping;
    public IntPtr orbits;
    public int n;
    public double group_size;

    public override bool Equals(object obj)
    {
        return obj is CanonicalOrbitResultNative other &&
               vertex_mapping == other.vertex_mapping &&
               orbits == other.orbits &&
               n == other.n &&
               group_size.Equals(other.group_size);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(vertex_mapping, orbits, n, group_size);
    }

    public static bool operator ==(CanonicalOrbitResultNative left, CanonicalOrbitResultNative right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(CanonicalOrbitResultNative left, CanonicalOrbitResultNative right)
    {
        return !(left == right);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct AutomorphismResultNative
{
    public IntPtr generators;
    public int gen_count;
    public int n;
    public double group_size;

    public override bool Equals(object obj)
    {
        return obj is AutomorphismResultNative other &&
               generators == other.generators &&
               gen_count == other.gen_count &&
               n == other.n &&
               group_size.Equals(other.group_size);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(generators, gen_count, n, group_size);
    }

    public static bool operator ==(AutomorphismResultNative left, AutomorphismResultNative right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(AutomorphismResultNative left, AutomorphismResultNative right)
    {
        return !(left == right);
    }
}

public class NautyDLL : IDisposable
{
    private const string DLL_NAME = "nauty_wrapper";

    [DllImport(DLL_NAME, EntryPoint = "nauty_canonical_sparse_with_colors")]
    public static extern IntPtr nauty_canonical_sparse_with_colors(int[] starts, int[] edges, int n, int totalEdges, int[] colors);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nauty_free_result(IntPtr result);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nauty_canonical_labeling_with_orbits(int[] starts, int[] edges, int n, int totalEdges, int[] colors);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nauty_free_orbit_result(IntPtr result);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nauty_get_automorphism_generators(int[] starts, int[] edges, int n, int totalEdges, int[] colors);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nauty_free_automorphism_result(IntPtr result);

    private static (int[] starts, int[] edges, int[] colors, int n) FlattenGraph(BipartiteGraph graph)
    {
        int n = graph.AdjacencyList.Count;
        var starts = new int[n];
        int totalEdges = 0;

        for (int i = 0; i < n; i++)
        {
            totalEdges += graph.AdjacencyList[i].Count;
        }

        var edges = new int[totalEdges];
        int currentEdgeIndex = 0;
        for (int i = 0; i < n; i++)
        {
            starts[i] = currentEdgeIndex;
            var neighbors = graph.AdjacencyList[i];
            for (int j = 0; j < neighbors.Count; j++)
            {
                edges[currentEdgeIndex++] = neighbors[j];
            }
        }

        return (starts, edges, graph.VertexColors, n);
    }

    /// <summary>
    /// Get full canonical labeling with vertex mapping for a colored graph using a sparse representation.
    /// This is a more memory-efficient method for sparse graphs like ours.
    /// </summary>
    public static (int[,] canonicalMatrix, int[] vertexMapping, long groupSize) GetCanonicalLabelingSparseWithColors(BipartiteGraph graph)
    {
        var (canonicalFlat, vertexMapping, n, groupSize) = GetCanonicalLabelingSparseWithColorsFlat(graph);

        // Copy canonical matrix data from native memory
#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
        int[,] canonicalMatrix = new int[n, n];
#pragma warning restore CA1814 // Prefer jagged arrays over multidimensional

        for (int i = 0; i < n; i++)
        {
            int rowOffset = i * n;
            for (int j = 0; j < n; j++)
            {
                canonicalMatrix[i, j] = canonicalFlat[rowOffset + j];
            }
        }

        return (canonicalMatrix, vertexMapping, groupSize);
    }

    /// <summary>
    /// Get the canonical dense matrix as a flat row-major array, avoiding the extra
    /// multidimensional-array copy when callers immediately convert to another shape.
    /// </summary>
    public static (int[] canonicalFlat, int[] vertexMapping, int n, long groupSize) GetCanonicalLabelingSparseWithColorsFlat(BipartiteGraph graph)
    {
        var (starts, edges, colors, n) = FlattenGraph(graph);
        int totalEdges = edges.Length;

        IntPtr resultPtr = nauty_canonical_sparse_with_colors(starts, edges, n, totalEdges, colors);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Nauty sparse computation failed");
        }

        CanonicalResult result = Marshal.PtrToStructure<CanonicalResult>(resultPtr);

        int[] canonicalFlat = new int[n * n];
        Marshal.Copy(result.canonical_labeling, canonicalFlat, 0, n * n);

        // Copy vertex mapping from native memory
        int[] vertexMapping = new int[n];
        Marshal.Copy(result.vertex_mapping, vertexMapping, 0, n);

        long groupSize = (long)result.group_size;

        // Free the native memory allocated by the DLL to prevent memory leaks
        nauty_free_result(resultPtr);

        return (canonicalFlat, vertexMapping, n, groupSize);
    }

    /// <summary>
    /// Get canonical vertex mapping plus automorphism orbits without copying the canonical matrix.
    /// </summary>
    public static (int[] vertexMapping, int[] orbits, long groupSize) GetCanonicalLabelingWithOrbits(BipartiteGraph graph)
    {
        var (starts, edges, colors, n) = FlattenGraph(graph);
        int totalEdges = edges.Length;

        IntPtr resultPtr = nauty_canonical_labeling_with_orbits(starts, edges, n, totalEdges, colors);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Nauty canonical orbit computation failed");
        }

        var result = Marshal.PtrToStructure<CanonicalOrbitResultNative>(resultPtr);
        try
        {
            var vertexMapping = new int[result.n];
            Marshal.Copy(result.vertex_mapping, vertexMapping, 0, result.n);

            var orbits = new int[result.n];
            Marshal.Copy(result.orbits, orbits, 0, result.n);

            long groupSize = (long)result.group_size;
            return (vertexMapping, orbits, groupSize);
        }
        finally
        {
            nauty_free_orbit_result(resultPtr);
        }
    }

    /// <summary>
    /// Get automorphism generators for a colored sparse graph.
    /// </summary>
    public static (int[][] generators, long groupSize) GetAutomorphismGenerators(BipartiteGraph graph)
    {
        var (starts, edges, colors, n) = FlattenGraph(graph);
        int totalEdges = edges.Length;

        IntPtr resultPtr = nauty_get_automorphism_generators(starts, edges, n, totalEdges, colors);

        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Nauty automorphism computation failed");
        }

        var result = Marshal.PtrToStructure<AutomorphismResultNative>(resultPtr);

        try
        {
            int generatorCount = result.generators == IntPtr.Zero ? 0 : result.gen_count;
            int[][] generators = new int[generatorCount][];
            if (generatorCount > 0)
            {
                int[] flat = new int[generatorCount * result.n];
                Marshal.Copy(result.generators, flat, 0, flat.Length);

                for (int g = 0; g < generatorCount; g++)
                {
                    generators[g] = new int[result.n];
                    Array.Copy(flat, g * result.n, generators[g], 0, result.n);
                }
            }

            long groupSize = (long)result.group_size;
            return (generators, groupSize);
        }
        finally
        {
            nauty_free_automorphism_result(resultPtr);
        }

    }

    public void Dispose()
    {
        // No cleanup needed for stateless DLL calls
    }
}
