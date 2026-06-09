// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner.SearchSpace;
using Microsoft.Extensions.Logging;
using static GreedyGrammarInductionLearner.SearchSpace.ArrayComparers;

namespace GreedyGrammarInductionLearner
{
    public class CanonicalAdjacencyArrayComparer : IEqualityComparer<int[][]>
    {
        public static CanonicalAdjacencyArrayComparer Shared { get; } = new CanonicalAdjacencyArrayComparer();

        public bool Equals(int[][] x, int[][] y)
        {
            if (x == null && y == null)
            {
                return true;
            }

            if (x == null || y == null)
            {
                return false;
            }

            if (x.Length != y.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Length; i++)
            {
                if (x[i].Length != y[i].Length)
                {
                    return false;
                }

                // Compare adjacency arrays (they should already be sorted from canonical form)
                for (int j = 0; j < x[i].Length; j++)
                {
                    if (x[i][j] != y[i][j])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public int GetHashCode(int[][] obj)
        {
            if (obj == null)
            {
                return 0;
            }

            var hc = new HashCode();
            hc.Add(obj.Length);
            for (int i = 0; i < obj.Length; i++)
            {
                hc.Add(obj[i].Length);
                for (int j = 0; j < obj[i].Length; j++)
                {
                    hc.Add(obj[i][j]);
                }
            }
            return hc.ToHashCode();
        }
    }

    public sealed class CanonicalColoredGraphKey
    {
        public CanonicalColoredGraphKey(int[][] adjacency, int[] colors)
        {
            Adjacency = adjacency;
            Colors = colors;
            HashCode = ComputeHash(adjacency, colors);
        }

        public int[][] Adjacency { get; }
        public int[] Colors { get; }
        public int HashCode { get; }

        private static int ComputeHash(int[][] adjacency, int[] colors)
        {
            var hc = new System.HashCode();
            if (colors == null)
            {
                hc.Add(0);
            }
            else
            {
                hc.Add(colors.Length);
                for (int i = 0; i < colors.Length; i++)
                {
                    hc.Add(colors[i]);
                }
            }

            hc.Add(CanonicalAdjacencyArrayComparer.Shared.GetHashCode(adjacency));
            return hc.ToHashCode();
        }
    }

    public class CanonicalColoredGraphKeyComparer : IEqualityComparer<CanonicalColoredGraphKey>
    {
        public static CanonicalColoredGraphKeyComparer Shared { get; } = new CanonicalColoredGraphKeyComparer();

        public bool Equals(CanonicalColoredGraphKey x, CanonicalColoredGraphKey y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x == null || y == null)
            {
                return false;
            }

            if (x.Colors.Length != y.Colors.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Colors.Length; i++)
            {
                if (x.Colors[i] != y.Colors[i])
                {
                    return false;
                }
            }

            return CanonicalAdjacencyArrayComparer.Shared.Equals(x.Adjacency, y.Adjacency);
        }

        public int GetHashCode(CanonicalColoredGraphKey obj)
        {
            return obj?.HashCode ?? 0;
        }
    }

    public readonly struct CanonicalGraphSignature : IEquatable<CanonicalGraphSignature>
    {
        private const int RefinementRounds = 3;

        public CanonicalGraphSignature(int vertexCount, int directedEdgeCount, ulong refinementHash, ulong colorDegreeHash)
        {
            VertexCount = vertexCount;
            DirectedEdgeCount = directedEdgeCount;
            RefinementHash = refinementHash;
            ColorDegreeHash = colorDegreeHash;
            HashCode = ComputeHashCode(vertexCount, directedEdgeCount, refinementHash, colorDegreeHash);
        }

        public int VertexCount { get; }
        public int DirectedEdgeCount { get; }
        public ulong RefinementHash { get; }
        public ulong ColorDegreeHash { get; }
        public int HashCode { get; }

        public static CanonicalGraphSignature Create(BipartiteGraph graph)
        {
            int n = graph.AdjacencyList.Count;
            int directedEdgeCount = 0;
            int maxDegree = 0;
            for (int i = 0; i < n; i++)
            {
                int degree = graph.AdjacencyList[i].Count;
                directedEdgeCount += degree;
                if (degree > maxDegree)
                {
                    maxDegree = degree;
                }
            }

            var current = System.Buffers.ArrayPool<ulong>.Shared.Rent(Math.Max(1, n));
            var next = System.Buffers.ArrayPool<ulong>.Shared.Rent(Math.Max(1, n));
            var neighborHashes = System.Buffers.ArrayPool<ulong>.Shared.Rent(Math.Max(1, maxDegree));

            try
            {
                ulong colorDegreeHash = 0xD6E8FEB86659FD93UL;
                for (int i = 0; i < n; i++)
                {
                    int degree = graph.AdjacencyList[i].Count;
                    ulong colorDegree = Mix(0x9E3779B97F4A7C15UL, unchecked((uint)graph.VertexColors[i]));
                    colorDegree = Mix(colorDegree, unchecked((uint)degree));
                    current[i] = colorDegree;
                    colorDegreeHash = Mix(colorDegreeHash, colorDegree);
                }

                for (int round = 0; round < RefinementRounds; round++)
                {
                    for (int vertex = 0; vertex < n; vertex++)
                    {
                        var neighbors = graph.AdjacencyList[vertex];
                        int degree = neighbors.Count;
                        for (int j = 0; j < degree; j++)
                        {
                            neighborHashes[j] = current[neighbors[j]];
                        }

                        if (degree > 1)
                        {
                            Array.Sort(neighborHashes, 0, degree);
                        }

                        ulong hash = Mix(0xA24BAED4963EE407UL + unchecked((uint)round), current[vertex]);
                        hash = Mix(hash, unchecked((uint)graph.VertexColors[vertex]));
                        hash = Mix(hash, unchecked((uint)degree));
                        for (int j = 0; j < degree; j++)
                        {
                            hash = Mix(hash, neighborHashes[j]);
                        }

                        next[vertex] = Avalanche(hash);
                    }

                    var tmp = current;
                    current = next;
                    next = tmp;
                }

                if (n > 1)
                {
                    Array.Sort(current, 0, n);
                }

                ulong refinementHash = 0xC2B2AE3D27D4EB4FUL;
                for (int i = 0; i < n; i++)
                {
                    refinementHash = Mix(refinementHash, current[i]);
                }

                return new CanonicalGraphSignature(n, directedEdgeCount, refinementHash, colorDegreeHash);
            }
            finally
            {
                System.Buffers.ArrayPool<ulong>.Shared.Return(current);
                System.Buffers.ArrayPool<ulong>.Shared.Return(next);
                System.Buffers.ArrayPool<ulong>.Shared.Return(neighborHashes);
            }
        }

        public bool Equals(CanonicalGraphSignature other)
        {
            return VertexCount == other.VertexCount &&
                   DirectedEdgeCount == other.DirectedEdgeCount &&
                   RefinementHash == other.RefinementHash &&
                   ColorDegreeHash == other.ColorDegreeHash;
        }

        public override bool Equals(object obj)
        {
            return obj is CanonicalGraphSignature other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode;
        }

        public static bool operator ==(CanonicalGraphSignature left, CanonicalGraphSignature right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(CanonicalGraphSignature left, CanonicalGraphSignature right)
        {
            return !left.Equals(right);
        }

        private static int ComputeHashCode(int vertexCount, int directedEdgeCount, ulong refinementHash, ulong colorDegreeHash)
        {
            unchecked
            {
                ulong hash = Mix(0x165667B19E3779F9UL, unchecked((uint)vertexCount));
                hash = Mix(hash, unchecked((uint)directedEdgeCount));
                hash = Mix(hash, refinementHash);
                hash = Mix(hash, colorDegreeHash);
                return (int)(hash ^ (hash >> 32));
            }
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            unchecked
            {
                hash ^= value + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
                return Avalanche(hash);
            }
        }

        private static ulong Avalanche(ulong value)
        {
            unchecked
            {
                value ^= value >> 30;
                value *= 0xBF58476D1CE4E5B9UL;
                value ^= value >> 27;
                value *= 0x94D049BB133111EBUL;
                value ^= value >> 31;
                return value;
            }
        }
    }

    public sealed class CanonicalGraphCandidate
    {
        private CanonicalColoredGraphKey _canonicalKey;

        internal CanonicalGraphCandidate(BipartiteGraph graph, CanonicalGraphSignature signature)
        {
            Graph = graph;
            Signature = signature;
        }

        internal BipartiteGraph Graph { get; }
        public CanonicalGraphSignature Signature { get; }
        internal CanonicalColoredGraphKey CanonicalKeyIfComputed => _canonicalKey;

        internal CanonicalColoredGraphKey GetCanonicalKey()
        {
            if (_canonicalKey != null)
            {
                return _canonicalKey;
            }

            _canonicalKey = OptimalSolutionsTracker.GetCanonicalColoredGraphKey(Graph);
            return _canonicalKey;
        }
    }

    public class OptimalSolutionsTracker
    {
        private readonly ILogger _logger;
        private readonly bool _evidenceContainsEmptyString;
        private int _index;
        public Dictionary<ushort[], OptimalSolutionNode> OptimalSolutionsMap { get; set; }
        public HashSet<ushort[]> GlobalOptimumsSolutions { get; set; }
        private ParetoManager _paretoManager;
        public Dictionary<CanonicalColoredGraphKey, OptimalSolutionNode> CanonicalAdjacencyMap { get; set; }
        private readonly Dictionary<CanonicalGraphSignature, List<CanonicalSignatureEntry>> _canonicalSignatureBuckets;
        private NautyDLL _nauty;
        public readonly object GlobalOptimumsLock = new object();
        public long CurrentShortestDepthOfGlobalOptimum;
        public List<(ContextFreeGrammar Grammar, int Index)> BestLearnedGrammars { get; private set; } = new();

        private sealed class CanonicalSignatureEntry
        {
            public CanonicalSignatureEntry(
                OptimalSolutionNode node,
                ushort[] nodeSet,
                ushort[] posIndices,
                CanonicalColoredGraphKey canonicalKey)
            {
                Node = node;
                NodeSet = nodeSet;
                PosIndices = posIndices;
                CanonicalKey = canonicalKey;
            }

            public OptimalSolutionNode Node { get; }
            public ushort[] NodeSet { get; }
            public ushort[] PosIndices { get; }
            public CanonicalColoredGraphKey CanonicalKey { get; set; }
        }

        public OptimalSolutionsTracker(ILogger logger, bool evidenceContainsEmptyString, double paretoRibbonThickness = 0.0)
        {
            OptimalSolutionsMap = new Dictionary<ushort[], OptimalSolutionNode>(SequenceEqualsComparer.Shared);
            GlobalOptimumsSolutions = new HashSet<ushort[]>(ArrayComparer.Shared);
            CanonicalAdjacencyMap = new Dictionary<CanonicalColoredGraphKey, OptimalSolutionNode>(CanonicalColoredGraphKeyComparer.Shared);
            _canonicalSignatureBuckets = new Dictionary<CanonicalGraphSignature, List<CanonicalSignatureEntry>>();
            CurrentShortestDepthOfGlobalOptimum = long.MaxValue;
            _paretoManager = new ParetoManager(paretoRibbonThickness);
            _logger = logger;
            _evidenceContainsEmptyString = evidenceContainsEmptyString;

            _nauty = new NautyDLL();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TestForOtherStronglyEquivalentGrammar(OptimalSolutionNode parent, ContextFreeGrammar grammar)
        {
            var canonicalKey = GetCanonicalColoredGraphKey(grammar);
            return TestForOtherStronglyEquivalentGrammar(parent, canonicalKey);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TestForOtherStronglyEquivalentGrammar(OptimalSolutionNode parent, CanonicalColoredGraphKey canonicalKey)
        {
            if (CanonicalAdjacencyMap.TryGetValue(canonicalKey, out var existingSolution))
            {
                if (parent != null)
                {
                    existingSolution.ParentsInLocalOptimumList.Add(parent);
                }
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TestForOtherStronglyEquivalentGrammar(
            OptimalSolutionNode parent,
            CanonicalGraphCandidate candidate,
            LatticeRuleSpace ruleSpace)
        {
            Debug.Assert(Monitor.IsEntered(this), "Canonical signature buckets must be accessed under the tracker lock.");

            if (!_canonicalSignatureBuckets.TryGetValue(candidate.Signature, out var bucket))
            {
                return false;
            }

            var candidateKey = candidate.GetCanonicalKey();
            for (int i = 0; i < bucket.Count; i++)
            {
                var existingEntry = bucket[i];
                var existingKey = GetExactCanonicalKey(existingEntry, ruleSpace);
                if (CanonicalColoredGraphKeyComparer.Shared.Equals(candidateKey, existingKey))
                {
                    if (parent != null)
                    {
                        existingEntry.Node.ParentsInLocalOptimumList.Add(parent);
                    }

                    return true;
                }
            }

            return false;
        }



        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CheckGlobalOptimum(List<double> parsedSentencesRatio)
        {
            bool foundGlobalOptimal = false;
            for (int i = 0; i < parsedSentencesRatio.Count; i++)
            {
                // Maximum optimal solution found!
                double TOLERANCE = 0.00001;
                if (Math.Abs(parsedSentencesRatio[i] - 1.0) < TOLERANCE)
                {
                    foundGlobalOptimal = true;
                    break;
                }
            }

            return foundGlobalOptimal;
        }

        public static bool CheckGlobalOptimum(double parsedSentencesRatio)
        {
            bool foundGlobalOptimal = false;

            // Maximum optimal solution found!
            double TOLERANCE = 0.00001;
            if (Math.Abs(parsedSentencesRatio - 1.0) < TOLERANCE)
            {
                foundGlobalOptimal = true;
            }


            return foundGlobalOptimal;
        }

        /// <summary>
        /// Register a new grammar with its canonical adjacency structure
        /// Call this when adding a new OptimalSolutionNode
        /// </summary>
        public void RegisterCanonicalMatrix(ContextFreeGrammar grammar, OptimalSolutionNode solutionNode)
        {
            RegisterCanonicalMatrix(GetCanonicalColoredGraphKey(grammar), solutionNode);
        }

        /// <summary>
        /// Register a new grammar with its canonical adjacency structure.
        /// </summary>
        public void RegisterCanonicalMatrix(CanonicalColoredGraphKey canonicalKey, OptimalSolutionNode solutionNode)
        {
            CanonicalAdjacencyMap[canonicalKey] = solutionNode;
        }

        public void RegisterCanonicalCandidate(
            CanonicalGraphCandidate candidate,
            OptimalSolutionNode solutionNode,
            HashSet<ushort> posIndices)
        {
            Debug.Assert(Monitor.IsEntered(this), "Canonical signature buckets must be accessed under the tracker lock.");
            if (!_canonicalSignatureBuckets.TryGetValue(candidate.Signature, out var bucket))
            {
                bucket = new List<CanonicalSignatureEntry>(1);
                _canonicalSignatureBuckets.Add(candidate.Signature, bucket);
            }

            var canonicalKey = candidate.CanonicalKeyIfComputed;
            var posIndexArray = new ushort[posIndices.Count];
            posIndices.CopyTo(posIndexArray);
            Array.Sort(posIndexArray);
            bucket.Add(new CanonicalSignatureEntry(solutionNode, solutionNode.Set, posIndexArray, canonicalKey));

            if (canonicalKey != null)
            {
                CanonicalAdjacencyMap[canonicalKey] = solutionNode;
            }
        }

        /// <summary>
        /// Convert a dense adjacency matrix to a space-efficient adjacency array
        /// Uses jagged arrays for optimal memory usage and performance
        /// </summary>
#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
        public static int[][] GetCanonicalAdjacencyArray(Grammar grammar)
        {
            var graph = BuildCanonicalGraph(grammar.GetRules());
            var (canonicalFlat, _, n, _) = NautyDLL.GetCanonicalLabelingSparseWithColorsFlat(graph);
            return ConvertFlatMatrixToAdjacencyArray(canonicalFlat, n);
        }

        public static CanonicalColoredGraphKey GetCanonicalColoredGraphKey(Grammar grammar)
        {
            var graph = BuildCanonicalGraph(grammar.GetRules());
            return GetCanonicalColoredGraphKey(graph);
        }

        public static CanonicalGraphCandidate CreateCanonicalGraphCandidate(Grammar grammar)
        {
            var graph = BuildCanonicalGraph(grammar.GetRules());
            var signature = CanonicalGraphSignature.Create(graph);
            return new CanonicalGraphCandidate(graph, signature);
        }

        internal static CanonicalColoredGraphKey GetCanonicalColoredGraphKey(BipartiteGraph graph)
        {
            var (canonicalFlat, vertexMapping, n, _) = NautyDLL.GetCanonicalLabelingSparseWithColorsFlat(graph);
            return new CanonicalColoredGraphKey(
                ConvertFlatMatrixToAdjacencyArray(canonicalFlat, n),
                ConvertToCanonicalColorArray(graph.VertexColors, vertexMapping));
        }

        private CanonicalColoredGraphKey GetExactCanonicalKey(CanonicalSignatureEntry entry, LatticeRuleSpace ruleSpace)
        {
            if (entry.CanonicalKey != null)
            {
                return entry.CanonicalKey;
            }

            var graph = BuildCanonicalGraph(entry.NodeSet, entry.PosIndices, ruleSpace);
            entry.CanonicalKey = GetCanonicalColoredGraphKey(graph);
            CanonicalAdjacencyMap[entry.CanonicalKey] = entry.Node;
            return entry.CanonicalKey;
        }

        private static BipartiteGraph BuildCanonicalGraph(List<Rule> rules)
        {
            (var graph, _) = CNFConverter.ConvertToEnhanced4ColorGraph(rules);
            return graph;
        }

        private static BipartiteGraph BuildCanonicalGraph(ushort[] nodeSet, ushort[] posIndices, LatticeRuleSpace ruleSpace)
        {
            var rules = new List<Rule>(nodeSet.Length + posIndices.Length);
            for (int i = 0; i < nodeSet.Length; i++)
            {
                rules.Add(ruleSpace.AllRules[nodeSet[i]]);
            }

            for (int i = 0; i < posIndices.Length; i++)
            {
                rules.Add(ruleSpace.POSAssignmentRules[posIndices[i]]);
            }

            return BuildCanonicalGraph(rules);
        }

        private static int[] ConvertToCanonicalColorArray(int[] originalColors, int[] vertexMapping)
        {
            var canonicalColors = new int[vertexMapping.Length];
            for (int i = 0; i < vertexMapping.Length; i++)
            {
                canonicalColors[i] = originalColors[vertexMapping[i]];
            }

            return canonicalColors;
        }

        private static int[][] ConvertMatrixToAdjacencyArray(int[,] matrix)
#pragma warning restore CA1814 // Prefer jagged arrays over multidimensional
        {
            int n = matrix.GetLength(0);
            var flat = new int[n * n];
            for (int i = 0; i < n; i++)
            {
                int rowOffset = i * n;
                for (int j = 0; j < n; j++)
                {
                    flat[rowOffset + j] = matrix[i, j];
                }
            }

            return ConvertFlatMatrixToAdjacencyArray(flat, n);
        }

        private static int[][] ConvertFlatMatrixToAdjacencyArray(int[] matrix, int n)
        {

            // First pass: count neighbors for each vertex to allocate exact sizes
            var neighborCounts = new int[n];
            for (int i = 0; i < n; i++)
            {
                int rowOffset = i * n;
                for (int j = 0; j < n; j++)
                {
                    if (matrix[rowOffset + j] != 0) // Non-zero means edge exists
                    {
                        neighborCounts[i]++;
                    }
                }
            }

            // Allocate jagged array with exact sizes
            var adjacencyArray = new int[n][];
            for (int i = 0; i < n; i++)
            {
                adjacencyArray[i] = new int[neighborCounts[i]];
            }

            // Second pass: fill the arrays
            var currentIndices = new int[n]; // Track current position in each array
            for (int i = 0; i < n; i++)
            {
                int rowOffset = i * n;
                for (int j = 0; j < n; j++)
                {
                    if (matrix[rowOffset + j] != 0)
                    {
                        adjacencyArray[i][currentIndices[i]++] = j;
                    }
                }
            }

            return adjacencyArray;
        }





        public static (int, int) ComputeNTsAndHighestIndex(HashSet<Rule> rules)
        {
            HashSet<int> grammarNTs = [];
            var startNT = new string(Grammar.StartSymbol);
            int highestIndex = 0;
            int highestIndexSymbolId = 0;
            //get all nonterminals of rules.
            foreach (var r1 in rules)
            {
                if (!r1.LeftHandSide.Equals(startNT))
                {
                    grammarNTs.Add(r1.LeftHandSide);
                }

                foreach (var rhs in r1.RightHandSide)
                {
                    if (!Grammar.PartsOfSpeech.Contains(rhs))
                    {
                        grammarNTs.Add(rhs);
                    }
                }
            }

            HashSet<int> ntcheckArr = [];
            foreach (var item in grammarNTs)
            {
                var str = SymbolTable.Instance.GetSymbol(item);
                if (str.Equals(Grammar.StartSymbol))
                {
                    continue;
                }

                var str1 = str[1..];
                int index = int.Parse(str1);
                ntcheckArr.Add(index);

                if (index > highestIndex)
                {
                    highestIndex = index;
                    highestIndexSymbolId = item;
                }
            }

            return (highestIndex, highestIndexSymbolId);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        public void AddLocalOptimalSolution(OptimalSolutionNode optimalSolution, ushort[] set)
        {
            optimalSolution.Index = _index;
            _index++;
            OptimalSolutionsMap[set] = optimalSolution;
        }

        public int ParetoVersion => _paretoManager.Version;

        public bool ShouldPrune(int depth, double lambda) => _paretoManager.ShouldPrune(depth, lambda);

        /// <summary>
        /// Creates a snapshot of the Pareto front for lock-free ShouldPrune checks.
        /// Takes GlobalOptimumsLock once to copy, then the snapshot can be used without locking.
        /// </summary>
        public ((int depth, double lambda)[] Snapshot, double Thickness) SnapshotParetoFront()
        {
            lock (GlobalOptimumsLock)
            {
                return (_paretoManager.CreateSnapshot(), _paretoManager.Thickness);
            }
        }

        public void AddGlobalOptimum(ushort[] x, double lambda)
        {
            int depth = x.Length;

            lock (GlobalOptimumsLock)
            {
                _paretoManager.UpdateFrontier(x, depth, lambda, GlobalOptimumsSolutions); 
            }

            long initialValue = Interlocked.Read(ref CurrentShortestDepthOfGlobalOptimum);
            //This is a thread-safe way to do `min(CurrentShortestDepthOfGlobalOptimum, depth)`
            while (initialValue > depth)
            {
                long previousValue = Interlocked.CompareExchange(ref CurrentShortestDepthOfGlobalOptimum, depth, initialValue);
                if (previousValue == initialValue || previousValue <= depth)
                {
                    break;
                }
                initialValue = Interlocked.Read(ref CurrentShortestDepthOfGlobalOptimum);
            }
        }

        private class ConsolidatedNode
        {
            public ContextFreeGrammar Grammar { get; set; }
            public List<int> OriginalIndices { get; set; }
            public HashSet<ConsolidatedNode> Parents { get; set; }
            public int Depth { get; set; }
            public bool IsGlobalOptimum { get; set; }
            public bool Printed { get; set; }
            public List<int[]> Horizons { get; set; }
            public string Key => Grammar.ToString();
            public int[] Horizon => Horizons.Count == 0 ? Array.Empty<int>() : Horizons[0];
            public bool HasHorizonConflict => Horizons.Count > 1;

            public ConsolidatedNode(ContextFreeGrammar grammar, int depth, bool isGlobalOptimum, int[] horizon)
            {
                Grammar = grammar;
                OriginalIndices = new List<int>();
                Parents = new HashSet<ConsolidatedNode>();
                Depth = depth;
                IsGlobalOptimum = isGlobalOptimum;
                Printed = false;
                Horizons = new List<int[]>();
                AddHorizon(horizon);
            }

            public void AddHorizon(int[] horizon)
            {
                horizon ??= Array.Empty<int>();
                foreach (var existingHorizon in Horizons)
                {
                    if (HorizonsEqual(existingHorizon, horizon))
                    {
                        return;
                    }
                }

                Horizons.Add(horizon);
            }

            private static bool HorizonsEqual(int[] left, int[] right)
            {
                if (ReferenceEquals(left, right))
                {
                    return true;
                }

                if (left == null || right == null || left.Length != right.Length)
                {
                    return false;
                }

                for (int i = 0; i < left.Length; i++)
                {
                    if (left[i] != right[i])
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void PrintConsolidatedOptimalSolutionsGraph(LatticeRuleSpace ruleSpace)
        {
            if (GlobalOptimumsSolutions.Count == 0)
            {
                return;
            }

            var grammarToConsolidatedNode = new Dictionary<string, ConsolidatedNode>();
            var oldNodeToConsolidatedNodes = new Dictionary<OptimalSolutionNode, List<ConsolidatedNode>>();

            // First Pass: Create consolidated nodes for all solutions
            foreach (var oldNode in OptimalSolutionsMap.Values)
            {
                var consolidatedNodesForOldNode = new List<ConsolidatedNode>();
                oldNodeToConsolidatedNodes[oldNode] = consolidatedNodesForOldNode;

                var grammars = oldNode.GetGrammars(ruleSpace);
                for (int i = 0; i < grammars.Count; i++)
                {
                    var conciseGrammar = RemoveRedundantNonterminals(grammars[i]);
                    var key = conciseGrammar.ToString();
                    var horizon = i < oldNode.Horizons.Count ? oldNode.Horizons[i] : Array.Empty<int>();

                    if (!grammarToConsolidatedNode.TryGetValue(key, out var consolidatedNode))
                    {
                        double TOLERANCE = 0.00001;
                        bool hasFullCoverage = Math.Abs(oldNode.ParsedRatio[i] - 1.0) < TOLERANCE;
                        bool isOnParetoFront = GlobalOptimumsSolutions.Contains(oldNode.Set);
                        bool isGlobalOptimum = hasFullCoverage && isOnParetoFront;
                        consolidatedNode = new ConsolidatedNode(conciseGrammar, oldNode.Depth, isGlobalOptimum, horizon);
                        grammarToConsolidatedNode[key] = consolidatedNode;
                    }
                    else
                    {
                        consolidatedNode.AddHorizon(horizon);
                    }

                    consolidatedNode.OriginalIndices.Add(oldNode.Index);
                    consolidatedNodesForOldNode.Add(consolidatedNode);
                }
            }

            // Second Pass: Wire up parents
            foreach (var oldNode in OptimalSolutionsMap.Values)
            {
                if (oldNodeToConsolidatedNodes.TryGetValue(oldNode, out var consolidatedChildren))
                {
                    foreach (var parent in oldNode.ParentsInLocalOptimumList)
                    {
                        if (oldNodeToConsolidatedNodes.TryGetValue(parent, out var consolidatedParents))
                        {
                            foreach (var child in consolidatedChildren)
                            {
                                foreach (var consParent in consolidatedParents)
                                {
                                    child.Parents.Add(consParent);
                                }
                            }
                        }
                    }
                }
            }

            // Third Pass: Print the graph
            var globalOptimums = new List<ConsolidatedNode>();
            foreach (var n in grammarToConsolidatedNode.Values)
            {
                if (n.IsGlobalOptimum)
                {
                    globalOptimums.Add(n);
                }
            }
            globalOptimums.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

            _logger.LogInformation($"---- {globalOptimums.Count} Global Solutions Found ------");
            int runningIndex = 1;
            var allGlobals = new List<(ContextFreeGrammar Grammar, int Index)>();

            foreach (var globalOptimum in globalOptimums)
            {
                if (globalOptimum.Printed)
                {
                    continue;
                }

                Queue<ConsolidatedNode> queue = new Queue<ConsolidatedNode>();
                queue.Enqueue(globalOptimum);
                globalOptimum.Printed = true;

                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    node.OriginalIndices.Sort();
                    var distinctIndices = new HashSet<int>(node.OriginalIndices);
                    string originalIndicesStr = string.Join(", ", distinctIndices);

                    string s1;
                    if (node.IsGlobalOptimum)
                    {
                        int currentIndex = runningIndex++;
                        var lambda = node.Grammar.CalculateLambda();
                        s1 = $"\r\n{currentIndex}. Global Optimum (Depth {node.Depth}) Growth Rate: {lambda:0.###} [Indices: {originalIndicesStr}]:\r\n{node.Grammar}";
                        s1 += FormatHorizonStats(node.Horizon);
                        s1 += FormatHorizonConflictWarning(node);
                        if (_evidenceContainsEmptyString)
                        {
                            s1 += "\r\nSTART -> ε";
                        }
                        allGlobals.Add((node.Grammar, currentIndex));
                    }
                    else
                    {
                        s1 = $"\r\nLocal Optimum (Depth {node.Depth}) [Indices: {originalIndicesStr}]:\r\n{node.Grammar}";
                        s1 += FormatHorizonStats(node.Horizon);
                        s1 += FormatHorizonConflictWarning(node);
                    }

                    var parentIndices = new List<string>();
                    foreach (var p in node.Parents)
                    {
                        var distinctParentIndices = new HashSet<int>(p.OriginalIndices);
                        parentIndices.Add(string.Join(", ", distinctParentIndices));
                    }

                    if (parentIndices.Count != 0)
                    {
                        s1 += $"\r\nSolution was obtained from the following solutions: {string.Join(", ", parentIndices)}";
                    }
                    _logger.LogInformation(s1);

                    foreach (var parent in node.Parents)
                    {
                        if (!parent.Printed)
                        {
                            parent.Printed = true;
                            queue.Enqueue(parent);
                        }
                    }
                }
            }

            BestLearnedGrammars = allGlobals;
        }

        private static string FormatHorizonStats(int[] horizon)
        {
            horizon ??= Array.Empty<int>();
            long totalStrings = 0;
            for (int i = 0; i < horizon.Length; i++)
            {
                totalStrings += horizon[i];
            }

            return $"Grammar counts vector: {FormatHorizon(horizon)}, total strings: {totalStrings}, Rule coverage length: {horizon.Length - 1}";
        }

        private static string FormatHorizonConflictWarning(ConsolidatedNode node)
        {
            if (!node.HasHorizonConflict)
            {
                return string.Empty;
            }

            var horizons = new List<string>(node.Horizons.Count);
            foreach (var horizon in node.Horizons)
            {
                horizons.Add(FormatHorizon(horizon));
            }

            return $"\r\nWARNING: Consolidated grammar has conflicting Horizons: {string.Join("; ", horizons)}";
        }

        private static string FormatHorizon(int[] horizon)
        {
            horizon ??= Array.Empty<int>();
            return $"[{string.Join(", ", horizon)}]";
        }

        public static ContextFreeGrammar RemoveRedundantNonterminals(Grammar grammar)
        {
            var rules = grammar.GetRules();
            List<Rule> coreRulesGlobal = new List<Rule>();
            foreach (var r in rules)
            {
                if (!r.IsLatticePosAssignmentRule())
                {
                    coreRulesGlobal.Add(r);
                }
            }

            var strictlyRHSNonterminals = Grammar.FindLatticeStrictlyRhsNonterminals(coreRulesGlobal);

            List<Rule> compactRules = [];
            Dictionary<int, HashSet<int>> posRulesAssignments = Grammar.ComputePOSRulesAssignments1(rules);

            foreach (var rule in rules)
            {
                //discard unused POS rule
                if (strictlyRHSNonterminals.Contains(rule.LeftHandSide))
                {
                    if (posRulesAssignments[rule.LeftHandSide].Count != 1)
                    {
                        compactRules.Add(rule);
                    }
                }
                else
                {
                    //replace rule
                    int[] newRHS = null;
                    for (int i = 0; i < rule.RightHandSide.Length; i++)
                    {
                        if (strictlyRHSNonterminals.Contains(rule.RightHandSide[i]))
                        {
                            if (posRulesAssignments[rule.RightHandSide[i]].Count == 1)
                            {
                                if (newRHS == null)
                                {
                                    newRHS = new int[rule.RightHandSide.Length];
                                    Array.Copy(rule.RightHandSide, newRHS, rule.RightHandSide.Length);
                                }

                                // Get the single element from the HashSet
                                using var enumerator = posRulesAssignments[rule.RightHandSide[i]].GetEnumerator();
                                enumerator.MoveNext();
                                newRHS[i] = enumerator.Current;
                            }

                        }
                    }
                    if (newRHS != null)
                    {
                        compactRules.Add(new Rule(rule.LeftHandSide, newRHS, rule.Type));
                    }
                    else
                    {
                        compactRules.Add(rule);
                    }
                }
            }

            return new ContextFreeGrammar(compactRules);
        }


    }
}
