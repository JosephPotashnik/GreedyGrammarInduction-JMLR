// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner.SearchSpace;
using Microsoft.Extensions.Logging;
using static GreedyGrammarInductionLearner.ArrayCompressor;

namespace GreedyGrammarInductionLearner
{
    /// <summary>
    /// Read-only context containing infrastructure objects passed through the search algorithm.
    /// Reduces parameter count and improves cache locality.
    /// </summary>
#pragma warning disable CA1815 // Override equals and operator equals on value types - not needed for parameter passing
    public readonly struct SearchContext
    {
        public readonly OptimalSolutionsTracker OptimalSolutionsTracker { get; init; }
        public readonly Dictionary<int, int> POSHashingDict { get; init; }
        public readonly EvidenceTreesShape EvidenceCalculator { get; init; }
        public readonly LatticeRuleSpace RuleSpace { get; init; }

        // Pre-computed parent parses (identical for all nodes in a root's BFS)
        public readonly bool HasRootOptimalNode { get; init; }
        public readonly OptimalSolutionNode RootOptimalNode { get; init; }
        public readonly double PreviousMinFitness { get; init; }
        public readonly List<List<CompressionRange>> ListOfPreviousParsed { get; init; }
        public readonly List<HashSet<Rule>> PreviousPosMappings { get; init; }
        public readonly List<int> ListOfIndicesInParent { get; init; }
        public readonly List<Rule> RootCoreRules { get; init; }

        // Wildcard-pattern prune contexts, one per previous POS mapping. A null entry disables
        // the prune for that mapping (e.g. encoding overflow).
        public readonly List<RootPruneContext> RootPruneContextsByMapping { get; init; }
        public readonly List<int[]> NextUnparsedPosByMapping { get; init; }
        public readonly RootPOSConstraint RootPOSConstraint { get; init; }

        public readonly ILogger Logger { get; init; }
    }
#pragma warning restore CA1815

#pragma warning disable CA1815 // Override equals and operator equals on value types - not needed for queue payloads
    internal readonly struct PreviousMappingSelection
    {
        private readonly byte _mode;

        public readonly ulong Mask;
        public readonly int[] Indices;

        public bool UsesAllPreviousMappings => _mode == 0;
        public bool UsesMask => _mode == 1;
        public bool UsesIndices => _mode == 2;

        private PreviousMappingSelection(byte mode, ulong mask, int[] indices)
        {
            _mode = mode;
            Mask = mask;
            Indices = indices;
        }

        public static PreviousMappingSelection All => default;

        public static PreviousMappingSelection FromMask(ulong mask)
        {
            return new PreviousMappingSelection(1, mask, null);
        }

        public static PreviousMappingSelection FromIndices(int[] indices)
        {
            return indices == null || indices.Length == 0
                ? new PreviousMappingSelection(2, 0UL, Array.Empty<int>())
                : new PreviousMappingSelection(2, 0UL, indices);
        }
    }
#pragma warning restore CA1815


    /// <summary>
    /// Data for a potential optimal solution being evaluated.
    /// Groups related solution metrics to reduce parameter count.
    /// Must be a ref struct to support ReadOnlySpan.
    /// </summary>
#pragma warning disable CA1815 // Override equals and operator equals on value types - not needed for parameter passing
    public readonly ref struct SolutionData
    {
        public readonly List<CompressionRange> Parsed { get; init; }
        public readonly ReadOnlySpan<int> GrammarShapeVector { get; init; }
        public readonly ushort[] CurrentNodeSet { get; init; }
        public readonly ContextFreeGrammar Grammar { get; init; }
        public readonly HashSet<Rule> POSMappings { get; init; }
        public readonly double ParsedRatio { get; init; }
        public readonly double Lambda { get; init; }
        public readonly double Fitness { get; init; }
        public readonly OptimalSolutionNode Parent { get; init; }
        public readonly CanonicalGraphCandidate CanonicalCandidate { get; init; }
        public readonly int IndexParsedInParent { get; init; }
        public readonly HashSet<ushort> POSIndices { get; init; }
        public readonly BannedPOSChain BannedPOS { get; init; }
    }
#pragma warning restore CA1815

    public class HypothesesSearchSpace
    {
        private readonly struct FrontierQueueItem
        {
            public readonly ushort[] NodeSet;
            public readonly ushort AddedRule;
            public readonly BannedPOSChain Banned;

            // Cached at enqueue time. Default means every root previous-POS mapping survives
            // the wildcard prune (or there are no previous mappings for this root).
            public readonly PreviousMappingSelection PreviousMappingSelection;

            public bool UsesAllPreviousMappings => PreviousMappingSelection.UsesAllPreviousMappings;

            public FrontierQueueItem(
                ushort[] nodeSet,
                ushort addedRule,
                BannedPOSChain banned,
                PreviousMappingSelection previousMappingSelection)
            {
                NodeSet = nodeSet;
                AddedRule = addedRule;
                Banned = banned;
                PreviousMappingSelection = previousMappingSelection;
            }
        }
#pragma warning disable CA2000 // Dispose objects before losing scope
        internal static ReaderWriterLockSlim s_bannedLock = new ReaderWriterLockSlim();
        internal static readonly object _outerQueueLock = new object();
        internal static readonly object _optimalSolutionsTrackerLock = new object();
#pragma warning restore CA2000 // Dispose objects before losing scope

        private readonly ILogger _logger;
        private readonly Func<string, IProgress<double>> _progressFactory;
        private readonly LatticeRuleSpace _ruleSpace;
        private readonly int _maxNumberOfRules;
        private readonly int _maxDepthBetweenSubSolutions;
        private readonly int _maxDepthAfterMinimalGlobal;
        private readonly int _minimumSizeOfOptimalSubSolution;
        private readonly double _paretoRibbonThickness;
        private readonly bool _evidenceContainsEmptyString;
        private readonly bool _continueSearchAfterOptimalNode;
        private readonly bool _skipKnownOptimalStructuralNodesAcrossRoots;

        // Public property to access the optimal solutions tracker after search
        public OptimalSolutionsTracker OptimalSolutionsTracker { get; private set; }
        public LatticeRuleSpace RuleSpace => _ruleSpace;
        [ThreadStatic] private static List<Rule> t_guidedDeltaRules;
        [ThreadStatic] private static List<Rule> t_guidedCandidateRules;
        [ThreadStatic] private static ulong[] t_guidedAllowedCandidateBits;
        [ThreadStatic] private static ulong[] t_guidedMappingAllowedCandidateBits;
        [ThreadStatic] private static ulong[] t_guidedCandidateMappingBits;
        [ThreadStatic] private static List<Rule> t_currentCoreRules;
        [ThreadStatic] private static HashSet<int> t_strictlyRhsNonterminals;
        [ThreadStatic] private static List<ushort> t_deltaRuleIndices;

        public HypothesesSearchSpace(
            ILogger logger,
            Func<string, IProgress<double>> progressFactory,
            LatticeRuleSpace ruleSpace,
            int maxNumberOfRules,
            int maxDepthBetweenSubSolutions,
            int maxDepthAfterMinimalGlobal,
            int minimumSizeOfOptimalSubSolution,
            bool evidenceContainsEmptyString,
            bool continueSearchAfterOptimalNode,
            bool skipKnownOptimalStructuralNodesAcrossRoots,
            double paretoRibbonThickness = 0.0)
        {
            _logger = logger;
            _progressFactory = progressFactory;
            _ruleSpace = ruleSpace;
            _maxNumberOfRules = maxNumberOfRules;
            _maxDepthBetweenSubSolutions = maxDepthBetweenSubSolutions;
            _maxDepthAfterMinimalGlobal = maxDepthAfterMinimalGlobal;
            _minimumSizeOfOptimalSubSolution = minimumSizeOfOptimalSubSolution;
            _paretoRibbonThickness = paretoRibbonThickness;
            _evidenceContainsEmptyString = evidenceContainsEmptyString;
            _continueSearchAfterOptimalNode = continueSearchAfterOptimalNode; 
            _skipKnownOptimalStructuralNodesAcrossRoots = skipKnownOptimalStructuralNodesAcrossRoots;
        }

        public bool Search(EvidenceTreesShape evidenceShapeVectorCalculator)
        {
            var optimalSolutionsTracker = new OptimalSolutionsTracker(_logger, _evidenceContainsEmptyString, _paretoRibbonThickness);
            OptimalSolutionsTracker = optimalSolutionsTracker; // Store for external access
            var posHashingDict = new Dictionary<int, int>();
            int posCounter = 0;
            PriorityQueue outerQueue = new PriorityQueue();
            foreach (var pos in _ruleSpace.POSes)
            {
                posHashingDict[pos] = posCounter++;
            }


            int lowestPriority = -1;
            int totalCountInDepth = 1;
            var enqueuedToOuterQueue = new ConcurrentDictionary<ushort[], byte>(ArrayComparers.ArrayComparer.Shared);
            outerQueue.Enqueue(0, Array.Empty<ushort>());
            enqueuedToOuterQueue.TryAdd(Array.Empty<ushort>(), 0);
            int outerQueueSweptParetoVersion = optimalSolutionsTracker.ParetoVersion;
            long outerQueueSweptShortestDepth = Interlocked.Read(ref optimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
            IProgress<double> progress = null;
            using var progressDisposable = progress as IDisposable;

            do
            {
                var currentShortestDepthOfGO = Interlocked.Read(ref optimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
                var currentParetoVersion = optimalSolutionsTracker.ParetoVersion;
                if (currentParetoVersion != outerQueueSweptParetoVersion ||
                    currentShortestDepthOfGO != outerQueueSweptShortestDepth)
                {
                    var (paretoSnapshot, paretoThickness) = optimalSolutionsTracker.SnapshotParetoFront();
                    PruneOuterQueueByCurrentState(
                        outerQueue,
                        enqueuedToOuterQueue,
                        optimalSolutionsTracker,
                        paretoSnapshot,
                        paretoThickness,
                        currentShortestDepthOfGO,
                        _maxDepthAfterMinimalGlobal);

                    outerQueueSweptParetoVersion = optimalSolutionsTracker.ParetoVersion;
                    outerQueueSweptShortestDepth = Interlocked.Read(ref optimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
                    currentShortestDepthOfGO = outerQueueSweptShortestDepth;
                }

                // Use enhanced dequeue that automatically filters out items that are too deep
                var dequeueResult = outerQueue.DequeueWithDepthFilter(currentShortestDepthOfGO, _maxDepthAfterMinimalGlobal);
                if (!dequeueResult.HasValue)
                {
                    // No more valid items in queue
                    break;
                }

                (var rootDepth, var set, int count) = dequeueResult.Value;

                if (rootDepth > lowestPriority)
                {

                    if (currentShortestDepthOfGO != long.MaxValue && rootDepth - currentShortestDepthOfGO >= _maxDepthAfterMinimalGlobal - 1)
                    {
                        outerQueue = null;
                        break;
                    }

                    lowestPriority = rootDepth;
                    totalCountInDepth = count;
                    var progressTitle = $"Considering {count} Local Optima in depth {rootDepth}";
                    _logger.LogInformation(progressTitle);
                    progress = _progressFactory(progressTitle);
                    progress.Report(1.0 / totalCountInDepth);
                }
                else
                {
                    var completedIterations = totalCountInDepth - count;
                    progress.Report((completedIterations + 1.0) / totalCountInDepth);
                }

                ProcessRootNode(
                    set,
                    rootDepth,
                    optimalSolutionsTracker,
                    posHashingDict,
                    evidenceShapeVectorCalculator,
                    outerQueue,
                    enqueuedToOuterQueue);


            } while (!outerQueue.IsEmpty());

            optimalSolutionsTracker.PrintConsolidatedOptimalSolutionsGraph(_ruleSpace);
            if (optimalSolutionsTracker.GlobalOptimumsSolutions.Count == 0)
            {
                _logger.LogInformation("Search Exhausted, No Global Solutions Found");
            }

            return false;
        }

        private void ProcessRootNode(
            ushort[] currentRootNodeSet,
            int rootDepth,
            OptimalSolutionsTracker optimalSolutionsTracker,
            Dictionary<int, int> posHashingDict,
            EvidenceTreesShape evidenceShapeVectorCalculator,
            PriorityQueue outerQueue,
            ConcurrentDictionary<ushort[], byte> enqueuedToOuterQueue)
        {
            // Pre-compute parent parses once for the root (identical across all BFS children)
            bool hasRootOptimalNode = optimalSolutionsTracker.OptimalSolutionsMap.TryGetValue(currentRootNodeSet, out var rootOptimalNode);
            OptimalSolutionNode parent = hasRootOptimalNode ? rootOptimalNode : null;
            var (previousMinFitness, listOfPreviousParsed, previousPosMappings, listOfIndicesInParent) = GetParentParses(parent, _ruleSpace);

            // Build one wildcard-pattern prune context per previous POS mapping. Each mapping is
            // a distinct root grammar for shortest-yield/context purposes.
            var rootCoreRules = _ruleSpace[currentRootNodeSet];
            var (rootPruneContextsByMapping, nextUnparsedPosByMapping) =
                BuildWildcardPruneMappingData(
                    rootCoreRules,
                    previousPosMappings,
                    listOfPreviousParsed,
                    evidenceShapeVectorCalculator);
            var rootPOSConstraint = RootPOSConstraint.Build(rootCoreRules, _ruleSpace);

            // Create context structs to reduce parameter passing overhead
            var context = new SearchContext
            {
                OptimalSolutionsTracker = optimalSolutionsTracker,
                POSHashingDict = posHashingDict,
                EvidenceCalculator = evidenceShapeVectorCalculator,
                RuleSpace = _ruleSpace,
                HasRootOptimalNode = hasRootOptimalNode,
                RootOptimalNode = rootOptimalNode,
                PreviousMinFitness = previousMinFitness,
                ListOfPreviousParsed = listOfPreviousParsed,
                PreviousPosMappings = previousPosMappings,
                ListOfIndicesInParent = listOfIndicesInParent,
                RootCoreRules = rootCoreRules,
                RootPruneContextsByMapping = rootPruneContextsByMapping,
                NextUnparsedPosByMapping = nextUnparsedPosByMapping,
                RootPOSConstraint = rootPOSConstraint,
                Logger = _logger,
            };

            var currentFrontierQueue = new ConcurrentQueue<FrontierQueueItem>();
            var nextFrontierQueue = new ConcurrentQueue<FrontierQueueItem>();
            var optimalNodes = new List<(OptimalSolutionNode, int)>();

            var adjacentRulesOfRoot = CanonicalAdjacencyGenerator.GetAdjacentRuleIndicesForBFS(
                _maxNumberOfRules,
                rootCoreRules,
                currentRootNodeSet,
                _ruleSpace,
                addedRule: -1);

            var rootBans = rootOptimalNode?.BubbledBans;
            var currentShortestDepthOfGO = Interlocked.Read(ref optimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
            bool useGuidedRootCandidates = TryBuildWildcardGuidedAllowedCandidates(
                adjacentRulesOfRoot,
                currentRootNodeSet,
                currentRootNodeSet,
                in context,
                out var guidedRootAllowedCandidates,
                out var guidedRootCandidateMappingBits,
                out int guidedRootMappingWordCount,
                out var guidedRootFallbackReason);
            if (!useGuidedRootCandidates && adjacentRulesOfRoot.Count != 0)
                LogWildcardGuidedFallback(
                    context.Logger,
                    "root-adjacency",
                    guidedRootFallbackReason,
                    adjacentRulesOfRoot.Count,
                    currentRootNodeSet,
                    currentRootNodeSet);

            int previousRootMappingCount = context.PreviousPosMappings == null ? 0 : context.PreviousPosMappings.Count;
            int rootChildDepth = currentRootNodeSet.Length + 1;
            for (int adjacentIndex = 0; adjacentIndex < adjacentRulesOfRoot.Count; adjacentIndex++)
            {
                if (useGuidedRootCandidates)
                {
                    bool allowed = IsGuidedCandidateAllowed(guidedRootAllowedCandidates, adjacentIndex);
                    if (!allowed)
                    {
                        continue;
                    }
                }

                ushort addedRule = adjacentRulesOfRoot[adjacentIndex];
                var childEnqueueMode = GetChildEnqueueMode(rootChildDepth, currentShortestDepthOfGO);
                if (childEnqueueMode == ChildEnqueueMode.Skip)
                {
                    continue;
                }

                var childNodeSet = CanonicalAdjacencyGenerator.MaterializeAdjacentNodeSet(currentRootNodeSet, addedRule);
                if (useGuidedRootCandidates)
                {
                    var guidedPreviousMappingSelection = GetGuidedPreviousMappingSelection(
                        guidedRootCandidateMappingBits,
                        guidedRootMappingWordCount,
                        previousRootMappingCount,
                        adjacentIndex);
                    currentFrontierQueue.Enqueue(new FrontierQueueItem(
                        childNodeSet,
                        addedRule,
                        rootBans,
                        guidedPreviousMappingSelection));
                    continue;
                }

                currentFrontierQueue.Enqueue(new FrontierQueueItem(
                        childNodeSet,
                        addedRule,
                        rootBans,
                        PreviousMappingSelection.All));
            }

            long localOptimumCount = 0;
            var paretoSnapshot = Array.Empty<(int depth, double lambda)>();
            var paretoThickness = 0.0;
            var lastParetoVersion = -1;
            do
            {
                var currentFrontierQueueCount = currentFrontierQueue.Count;

                // Performance optimization: Read once per iteration instead of per thread
                var currentShortestDepthSnapshot = Interlocked.Read(ref optimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);

                // Only re-snapshot Pareto front when it has actually changed
                var currentParetoVersion = optimalSolutionsTracker.ParetoVersion;
                if (currentParetoVersion != lastParetoVersion)
                {
                    (paretoSnapshot, paretoThickness) = optimalSolutionsTracker.SnapshotParetoFront();
                    lastParetoVersion = currentParetoVersion;
                }

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                };

                Parallel.For(
                    0,
                    currentFrontierQueueCount,
                    parallelOptions,
                    // 1. localInit: Runs ONCE per thread partition
                    () =>
                    {
                        // Check out a lane for this thread's lifespan during this parallel loop
                        if (!evidenceShapeVectorCalculator.TryCheckoutLane(out int laneIndex))
                        {
                            // With MaxDegreeOfParallelism enforced, this should theoretically never fail, 
                            // but you can throw or handle extreme edge cases here.
                            throw new InvalidOperationException("Thread starvation: No available lanes.");
                        }
                        return laneIndex;
                    },
                    // 2. body: Runs for EVERY item in the queue
                    (i, loopState, laneIndex) =>
                    {
                        bool success = currentFrontierQueue.TryDequeue(out var queueItem);
                        if (!success)
                        {
                            return laneIndex; // FIXED: Must return the local state
                        }

                        var currentNodeSet = queueItem.NodeSet;
                        var addedRule = queueItem.AddedRule;
                        var banned = queueItem.Banned;
                        var previousMappingSelection = queueItem.PreviousMappingSelection;

                        // Use snapshot instead of repeated Interlocked.Read (saves ~20-30 cycles per iteration)
                        if (currentShortestDepthSnapshot != long.MaxValue && currentNodeSet.Length - currentShortestDepthSnapshot > _maxDepthAfterMinimalGlobal - 1)
                        {
                            return laneIndex; // FIXED: Must return the local state
                        }

                        ProcessSingleNode(
                            currentNodeSet,
                            addedRule,
                            currentRootNodeSet,
                            rootDepth,
                            ref localOptimumCount,
                            in context,
                            nextFrontierQueue,
                            optimalNodes,
                            paretoSnapshot,
                            paretoThickness,
                            banned,
                            previousMappingSelection,
                            laneIndex); // FIXED: Pass the lane index down to your single node processor

                        return laneIndex; // FIXED: Must return the local state at the end of the iteration
                    },
                    // 3. localFinally: Runs ONCE per thread partition when it finishes its chunk
                    (laneIndex) =>
                    {
                        // Return the lane to the bag
                        evidenceShapeVectorCalculator.ReturnLane(laneIndex);
                    }
                );

                int depthOfNext = 0;
                if (!nextFrontierQueue.IsEmpty && nextFrontierQueue.TryPeek(out var peeked))
                {
                    depthOfNext = peeked.NodeSet.Length;
                }

                if (currentRootNodeSet.Length <= _minimumSizeOfOptimalSubSolution &&  depthOfNext > _minimumSizeOfOptimalSubSolution)
                {
                    currentFrontierQueue = new ConcurrentQueue<FrontierQueueItem>();
                }
                else
                {
                    var tempQueue = currentFrontierQueue;
                    currentFrontierQueue = nextFrontierQueue;
                    nextFrontierQueue = tempQueue;
                }

            } while (!currentFrontierQueue.IsEmpty);

            var optimalNodesArr = optimalNodes.ToArray();
            var validNodes = new Dictionary<ushort[], OptimalSolutionNode>(SequenceEqualsComparer.Shared);
            for (int i = 0; i < optimalNodesArr.Length; i++)
            {
                var node = optimalNodesArr[i].Item1;
                if (node != null)
                    validNodes[node.Set] = node;
            }

            var (latestParetoSnapshot, latestParetoThickness) = optimalSolutionsTracker.SnapshotParetoFront();

            foreach (var validNode in validNodes)
            {
                var node = validNode.Value;
                node.RemoveParetoPrunedIncompleteAlternatives(latestParetoSnapshot, latestParetoThickness);

                if (!HasAnyIncompleteParsed(node))
                    continue;

                var validSet = validNode.Key;
                ushort depth = (ushort)validSet.Length;
                var sortedSet = validSet;

                // Check depth constraint before enqueuing
                var currentShortestDepth = Interlocked.Read(ref optimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
                if (currentShortestDepth != long.MaxValue &&
                    depth - currentShortestDepth > _maxDepthAfterMinimalGlobal - 1)
                {
                    // Don't enqueue items that are already too deep
                    continue;
                }

                if (enqueuedToOuterQueue.TryAdd(sortedSet, 0))
                {
                    outerQueue.Enqueue(depth, sortedSet);
                }
            }
        }

        private static void PruneOuterQueueByCurrentState(
            PriorityQueue outerQueue,
            ConcurrentDictionary<ushort[], byte> enqueuedToOuterQueue,
            OptimalSolutionsTracker optimalSolutionsTracker,
            (int depth, double lambda)[] paretoSnapshot,
            double paretoThickness,
            long currentShortestDepth,
            int maxDepthAfterMinimalGlobal)
        {
            if (outerQueue == null || outerQueue.IsEmpty())
                return;

            outerQueue.RemoveWhere((priority, set) =>
            {
                if (currentShortestDepth != long.MaxValue &&
                    priority - currentShortestDepth > maxDepthAfterMinimalGlobal - 1)
                {
                    enqueuedToOuterQueue.TryRemove(set, out _);
                    return true;
                }

                if (paretoSnapshot.Length == 0)
                    return false;

                if (!optimalSolutionsTracker.OptimalSolutionsMap.TryGetValue(set, out var node))
                    return false;

                node.RemoveParetoPrunedIncompleteAlternatives(paretoSnapshot, paretoThickness);
                if (HasAnyIncompleteParsed(node))
                    return false;

                enqueuedToOuterQueue.TryRemove(set, out _);
                return true;
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ParsedCoversAllEvidence(List<CompressionRange> parsedCompressed)
        {
            return parsedCompressed != null && parsedCompressed.Count == 0;
        }

        private static bool HasAnyIncompleteParsed(OptimalSolutionNode node)
        {
            if (node?.Parsed == null || node.Parsed.Count == 0)
                return true;

            for (int i = 0; i < node.Parsed.Count; i++)
            {
                if (!ParsedCoversAllEvidence(node.Parsed[i]))
                    return true;
            }

            return false;
        }
        private static bool TryBuildWildcardGuidedAllowedCandidates(
            List<ushort> adjacentRuleIndices,
            ushort[] currentNodeSet,
            ushort[] currentRootNodeSet,
            in SearchContext context,
            out ulong[] allowedCandidateBits,
            out ulong[] candidateMappingBits,
            out int mappingWordCount,
            out string fallbackReason)
        {
            allowedCandidateBits = null;
            candidateMappingBits = null;
            mappingWordCount = 0;
            fallbackReason = null;

            int candidateCount = adjacentRuleIndices == null ? 0 : adjacentRuleIndices.Count;
            if (candidateCount == 0)
            {
                fallbackReason = "no candidates";
                return false;
            }

            if (currentNodeSet == null)
            {
                fallbackReason = "missing current node set";
                return false;
            }

            if (currentRootNodeSet == null)
            {
                fallbackReason = "missing current root node set";
                return false;
            }

            if (currentNodeSet.Length < currentRootNodeSet.Length)
            {
                fallbackReason = "current node set is shorter than root node set";
                return false;
            }

            int deltaCount = currentNodeSet.Length - currentRootNodeSet.Length;
            if (deltaCount >= 30)
            {
                fallbackReason = "delta rule count is too large for the guide mask";
                return false;
            }

            var rootContexts = context.RootPruneContextsByMapping;
            var nextUnparsedByMapping = context.NextUnparsedPosByMapping;
            if (rootContexts == null || nextUnparsedByMapping == null)
            {
                fallbackReason = "missing per-mapping root prune context or next-unparsed cache";
                return false;
            }

            int previousMappingCount = context.PreviousPosMappings == null ? 0 : context.PreviousPosMappings.Count;
            int mappingCount = previousMappingCount == 0 ? 1 : previousMappingCount;
            if (rootContexts.Count < mappingCount || nextUnparsedByMapping.Count < mappingCount)
            {
                fallbackReason = "per-mapping wildcard cache is smaller than the active mapping count";
                return false;
            }

            var deltaRules = t_guidedDeltaRules ??= new List<Rule>(Math.Max(4, deltaCount));
            deltaRules.Clear();
            deltaRules.EnsureCapacity(deltaCount);
            int ri = 0, rj = 0;
            while (ri < currentNodeSet.Length)
            {
                if (rj >= currentRootNodeSet.Length || currentNodeSet[ri] < currentRootNodeSet[rj])
                    deltaRules.Add(context.RuleSpace.GetRuleFromIndex(currentNodeSet[ri++]));
                else if (currentNodeSet[ri] == currentRootNodeSet[rj]) { ri++; rj++; }
                else rj++;
            }

            var candidateRules = t_guidedCandidateRules ??= new List<Rule>(candidateCount);
            candidateRules.Clear();
            candidateRules.EnsureCapacity(candidateCount);
            for (int i = 0; i < candidateCount; i++)
                candidateRules.Add(context.RuleSpace.GetRuleFromIndex(adjacentRuleIndices[i]));

            int wordCount = (candidateCount + 63) >> 6;
            if (t_guidedAllowedCandidateBits == null || t_guidedAllowedCandidateBits.Length < wordCount)
                t_guidedAllowedCandidateBits = new ulong[wordCount];
            else
                Array.Clear(t_guidedAllowedCandidateBits, 0, wordCount);

            allowedCandidateBits = t_guidedAllowedCandidateBits;
            if (previousMappingCount != 0)
            {
                mappingWordCount = (previousMappingCount + 63) >> 6;
                long mappingBitsLength = (long)candidateCount * mappingWordCount;
                if (mappingBitsLength > int.MaxValue)
                {
                    allowedCandidateBits = null;
                    mappingWordCount = 0;
                    fallbackReason = "candidate-to-mapping survivor bitset would exceed supported array size";
                    return false;
                }

                int mappingBitsArrayLength = (int)mappingBitsLength;
                if (t_guidedCandidateMappingBits == null || t_guidedCandidateMappingBits.Length < mappingBitsArrayLength)
                    t_guidedCandidateMappingBits = new ulong[mappingBitsArrayLength];
                else
                    Array.Clear(t_guidedCandidateMappingBits, 0, mappingBitsArrayLength);
                candidateMappingBits = t_guidedCandidateMappingBits;

                if (t_guidedMappingAllowedCandidateBits == null || t_guidedMappingAllowedCandidateBits.Length < wordCount)
                    t_guidedMappingAllowedCandidateBits = new ulong[wordCount];
            }

            for (int mappingIndex = 0; mappingIndex < mappingCount; mappingIndex++)
            {
                var pruneCtx = rootContexts[mappingIndex];
                var nextUnparsedPos = nextUnparsedByMapping[mappingIndex];

                if (nextUnparsedPos == null)
                {
                    if (previousMappingCount != 0 &&
                        context.ListOfPreviousParsed != null &&
                        mappingIndex < context.ListOfPreviousParsed.Count &&
                        ParsedCoversAllEvidence(context.ListOfPreviousParsed[mappingIndex]))
                    {
                        continue;
                    }

                    allowedCandidateBits = null;
                    candidateMappingBits = null;
                    mappingWordCount = 0;
                    fallbackReason = "mapping " + mappingIndex + " has no next-unparsed POS sequence but its parsed vector is not complete";
                    return false;
                }

                if (nextUnparsedPos.Length == 0)
                {
                    allowedCandidateBits = null;
                    candidateMappingBits = null;
                    mappingWordCount = 0;
                    fallbackReason = "mapping " + mappingIndex + " points to an empty next-unparsed sentence";
                    return false;
                }

                var mappingAllowedCandidateBits = previousMappingCount == 0
                    ? allowedCandidateBits
                    : t_guidedMappingAllowedCandidateBits;
                if (previousMappingCount != 0)
                    Array.Clear(mappingAllowedCandidateBits, 0, wordCount);
                if (!WildcardPrune.TryAddGuidedCandidates(
                        pruneCtx,
                        deltaRules,
                        candidateRules,
                        nextUnparsedPos,
                        mappingAllowedCandidateBits))
                {
                    allowedCandidateBits = null;
                    candidateMappingBits = null;
                    mappingWordCount = 0;
                    fallbackReason = "guided DP could not safely classify mapping " + mappingIndex;
                    return false;
                }

                if (previousMappingCount != 0)
                    AddGuidedMappingSurvivors(
                        allowedCandidateBits,
                        candidateMappingBits,
                        mappingAllowedCandidateBits,
                        candidateCount,
                        wordCount,
                        mappingWordCount,
                        mappingIndex);
            }

            return true;
        }

        private static void LogWildcardGuidedFallback(
            ILogger logger,
            string phase,
            string reason,
            int candidateCount,
            ushort[] currentNodeSet,
            ushort[] currentRootNodeSet)
        {
            if (logger == null || candidateCount <= 0)
                return;

            int currentDepth = currentNodeSet == null ? -1 : currentNodeSet.Length;
            int rootDepth = currentRootNodeSet == null ? -1 : currentRootNodeSet.Length;
            int deltaCount = currentDepth >= 0 && rootDepth >= 0 ? currentDepth - rootDepth : -1;
            logger.LogWarning(
                "[WILDCARD-GUIDE] Conservative fallback in {Phase}: keeping all {CandidateCount} candidates. Reason={Reason}; currentDepth={CurrentDepth}; rootDepth={RootDepth}; deltaRules={DeltaCount}.",
                phase,
                candidateCount,
                reason ?? "unspecified",
                currentDepth,
                rootDepth,
                deltaCount);
        }

        private static void AddGuidedMappingSurvivors(
            ulong[] allowedCandidateBits,
            ulong[] candidateMappingBits,
            ulong[] mappingAllowedCandidateBits,
            int candidateCount,
            int candidateWordCount,
            int mappingWordCount,
            int mappingIndex)
        {
            int mappingWord = mappingIndex >> 6;
            ulong mappingBit = 1UL << (mappingIndex & 63);
            for (int word = 0; word < candidateWordCount; word++)
            {
                ulong bits = mappingAllowedCandidateBits[word];
                allowedCandidateBits[word] |= bits;
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits);
                    int candidateIndex = (word << 6) + bit;
                    if (candidateIndex < candidateCount)
                        candidateMappingBits[candidateIndex * mappingWordCount + mappingWord] |= mappingBit;
                    bits &= bits - 1;
                }
            }
        }

        private static PreviousMappingSelection GetGuidedPreviousMappingSelection(
            ulong[] candidateMappingBits,
            int mappingWordCount,
            int previousMappingCount,
            int candidateIndex)
        {
            if (previousMappingCount == 0 || candidateMappingBits == null || mappingWordCount == 0)
                return PreviousMappingSelection.All;

            int offset = candidateIndex * mappingWordCount;
            if (mappingWordCount == 1)
            {
                ulong allMask = previousMappingCount == 64
                    ? ulong.MaxValue
                    : (1UL << previousMappingCount) - 1UL;
                ulong bits = candidateMappingBits[offset] & allMask;
                if (bits == allMask)
                    return PreviousMappingSelection.All;

                return PreviousMappingSelection.FromMask(bits);
            }

            int fullWords = previousMappingCount >> 6;
            int tailBits = previousMappingCount & 63;
            bool allMappingsSurvive = true;
            int survivorCount = 0;
            for (int word = 0; word < fullWords; word++)
            {
                ulong bits = candidateMappingBits[offset + word];
                if (bits != ulong.MaxValue)
                    allMappingsSurvive = false;
                survivorCount += BitOperations.PopCount(bits);
            }

            if (tailBits != 0)
            {
                ulong tailMask = (1UL << tailBits) - 1UL;
                ulong bits = candidateMappingBits[offset + fullWords] & tailMask;
                if (bits != tailMask)
                    allMappingsSurvive = false;
                survivorCount += BitOperations.PopCount(bits);
            }

            if (allMappingsSurvive)
                return PreviousMappingSelection.All;

            var result = new int[survivorCount];
            int resultIndex = 0;
            for (int word = 0; word < fullWords; word++)
            {
                ulong bits = candidateMappingBits[offset + word];
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits);
                    result[resultIndex++] = (word << 6) + bit;
                    bits &= bits - 1;
                }
            }

            if (tailBits != 0)
            {
                ulong bits = candidateMappingBits[offset + fullWords] & ((1UL << tailBits) - 1UL);
                while (bits != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(bits);
                    result[resultIndex++] = (fullWords << 6) + bit;
                    bits &= bits - 1;
                }
            }

            return PreviousMappingSelection.FromIndices(result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsGuidedCandidateAllowed(ulong[] allowedCandidateBits, int candidateIndex)
        {
            return ((allowedCandidateBits[candidateIndex >> 6] >> (candidateIndex & 63)) & 1UL) != 0UL;
        }

        private enum ChildEnqueueMode : byte
        {
            Enqueue,
            Skip
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ChildEnqueueMode GetChildEnqueueMode(int childDepth, long currentDepthOfGO)
        {
            if (currentDepthOfGO == long.MaxValue)
            {
                return ChildEnqueueMode.Enqueue;
            }

            int childDelta = childDepth - (int)currentDepthOfGO;
            int deepestAllowedDelta = _maxDepthAfterMinimalGlobal - 1;

            if (childDelta > deepestAllowedDelta)
            {
                return ChildEnqueueMode.Skip;
            }

            return ChildEnqueueMode.Enqueue;
        }

        internal struct RootParsingData
        {
            public List<byte[]> ParsedOfRoot { get; set; }
            public int HighestIndexInRoot { get; set; }
            public int HighestNonTerminalIdInRoot { get; set; }
            public ushort[] Set { get; set; }

        }

        private void ProcessSingleNode(
            ushort[] currentNodeSet,
            ushort addedRule,
            ushort[] currentRootNodeSet,
            int rootDepth,
            ref long localOptimumCount,
            in SearchContext context,
            ConcurrentQueue<FrontierQueueItem> nextFrontierQueue,
            List<(OptimalSolutionNode, int)> optimalNodes,
            (int depth, double lambda)[] paretoSnapshot,
            double paretoThickness,
            BannedPOSChain banned,
            PreviousMappingSelection previousMappingSelection,
            int laneIndex)
        {
            // Use pre-computed root optimal node lookup from SearchContext
            bool hasRootOptimalNode = context.HasRootOptimalNode;
            var rootOptimalNode = context.RootOptimalNode;
            OptimalSolutionNode parent = hasRootOptimalNode ? rootOptimalNode : null;

            // Heuristic: skip structural nodes that were already discovered as optimal
            // under another root/POS history. Default false because this can miss
            // cross-root POS alternatives for the same structural rule set.
            if (_skipKnownOptimalStructuralNodesAcrossRoots)
            {
                bool isRoot = currentNodeSet == currentRootNodeSet;
                if (!isRoot)
                {
                    lock (context.OptimalSolutionsTracker)
                    {
                        if (context.OptimalSolutionsTracker.OptimalSolutionsMap.ContainsKey(currentNodeSet))
                        {
                            return;
                        }
                    }
                }
            }

            var coreRules = t_currentCoreRules ??= new List<Rule>();
            context.RuleSpace.CopyRulesTo(currentNodeSet, coreRules);
            if (parent == null && currentNodeSet.Length > _minimumSizeOfOptimalSubSolution)
            {
                return;
            }

            var latestDepthAtNodeStart = Interlocked.Read(ref context.OptimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
            if (latestDepthAtNodeStart != long.MaxValue &&
                currentNodeSet.Length - latestDepthAtNodeStart > _maxDepthAfterMinimalGlobal - 1)
            {
                return;
            }

            // Use pre-computed parent parses from SearchContext
            var previousMinFitness = context.PreviousMinFitness;
            var listOfPreviousParsed = context.ListOfPreviousParsed;
            var previousPosMappings = context.PreviousPosMappings;
            var listOfIndicesInParent = context.ListOfIndicesInParent;

            var strictlyRHSNonterminals = t_strictlyRhsNonterminals ??= new HashSet<int>();
            Grammar.FindLatticeStrictlyRhsNonterminalsInto(coreRules, strictlyRHSNonterminals);
            (List<ushort[]> POSAssignmentsOptions, List<int> PreviousPOSMappingIndices) = context.EvidenceCalculator.MaxFitNextUnparsedSentences(
                laneIndex,
                coreRules,
                listOfPreviousParsed,
                previousPosMappings,
                context.RuleSpace,
                context.RootPOSConstraint,
                previousMappingSelection,
                coreStrictlyRHSNonterminals: strictlyRHSNonterminals,
                bannedPOS: banned);

            var localOptimumDetected = false;
            HashSet<OptimalSolutionNode> localOptimalNodesForBans = null;
            int posCapacity = context.RuleSpace.POSAssignmentRules.Length;
            var shortestDepthAtLoopStart = Interlocked.Read(ref context.OptimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
            bool onlyGlobalSolutionsAccepted =
                shortestDepthAtLoopStart != long.MaxValue &&
                currentNodeSet.Length - shortestDepthAtLoopStart >= _maxDepthAfterMinimalGlobal - 1;
            double lambdaPruneThreshold = ParetoManager.TryGetPruneLambdaThreshold(
                paretoSnapshot,
                paretoThickness,
                currentNodeSet.Length,
                out var threshold)
                    ? threshold
                    : double.PositiveInfinity;

            for (var k = 0; k < POSAssignmentsOptions.Count; k++)
            {
                var posAssignmentOption = POSAssignmentsOptions[k];
                if (posAssignmentOption == null)
                {
                    continue;
                }

                HashSet<Rule> previousPOSMapping = null;
                int previousMappingKey = -1;

                List<CompressionRange> previousParsed = null;
                int indexOfParsedInParent = 0;
                if (previousPosMappings.Count > 0)
                {
                    previousMappingKey = PreviousPOSMappingIndices == null ? 0 : PreviousPOSMappingIndices[k];
                    previousPOSMapping = previousPosMappings[previousMappingKey];
                    previousParsed = listOfPreviousParsed[previousMappingKey];
                    indexOfParsedInParent = listOfIndicesInParent[previousMappingKey];


                }

                (ContextFreeGrammar grammar, Dictionary<int, int> minLengths) = GrammarExtensions.ExtendCoreRulesWithValidatedPOSAssignmentIndices(
                    posAssignmentOption,
                    previousPOSMapping,
                    coreRules,
                    strictlyRHSNonterminals,
                    context.RuleSpace);

                if (grammar == null) continue;

                (var underfits, var unproductiveRules, var grammarShapeVector) = grammar.GetGrammarShape(minLengths, context.EvidenceCalculator.MaxDistinctByLength);

                if (underfits || unproductiveRules)
                {
                    if (underfits && !unproductiveRules)
                    {
                        banned = AddBannedPOSMapping(posAssignmentOption, previousPOSMapping, context.RuleSpace, banned);
                    }
                    // Shape overgeneration is monotone, so ban that POS mapping for descendants.
                    // Unproductive cases may be rescued by future structural rules, so just skip them.
                    continue;
                }

                (bool maxFitFound, double parsedRatio, double fitness, List<CompressionRange> parsed) = context.EvidenceCalculator.ComputeEvidenceVector(
                    laneIndex,
                    grammar,
                    grammarShapeVector,
                    previousParsed,
                    earlyExitOnUnparsed: true);

                if (!maxFitFound)
                {
                    // Ban this POS assignment for all descendants
                    var posBitset = POSBitset.FromPOSMappingIndices(
                        posAssignmentOption,
                        previousPOSMapping,
                        context.RuleSpace.POSAssignmentDictionary,
                        posCapacity);
                    banned = new BannedPOSChain(posBitset, banned);
                    continue; // Move immediately to next POS option, completely skipping Lambda
                }

                if (!onlyGlobalSolutionsAccepted)
                {
                    var latestShortestDepth = Interlocked.Read(ref context.OptimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
                    onlyGlobalSolutionsAccepted =
                        latestShortestDepth != long.MaxValue &&
                        currentNodeSet.Length - latestShortestDepth >= _maxDepthAfterMinimalGlobal - 1;
                }

                if (onlyGlobalSolutionsAccepted && !OptimalSolutionsTracker.CheckGlobalOptimum(parsedRatio))
                {
                    continue;
                }

                var lambda = grammar.CalculateLambda(
                    lambdaPruneThreshold,
                    out bool exactLambda);
                if (!exactLambda)
                {
                    continue;
                }

                bool pruned = ParetoManager.ShouldPrune(paretoSnapshot, paretoThickness, currentNodeSet.Length, lambda);
                if (pruned) continue;

                // OPTIMAL GRAMMAR FOUND!
                localOptimumDetected = true;

                    var currentShortestDepthOfGO = Interlocked.Read(ref context.OptimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
                    if (currentShortestDepthOfGO != long.MaxValue &&
                        currentNodeSet.Length - currentShortestDepthOfGO >= _maxDepthAfterMinimalGlobal - 1 &&
                        !OptimalSolutionsTracker.CheckGlobalOptimum(parsedRatio))
                    {
                        continue;
                    }

                    var canonicalCandidate = OptimalSolutionsTracker.CreateCanonicalGraphCandidate(grammar);

                    var POSMappings = GrammarExtensions.MaterializePOSMappings(posAssignmentOption, previousPOSMapping, context.RuleSpace);

                    // Pre-allocate with known capacity to avoid resizing
                    HashSet<ushort> newPosIndices = new(POSMappings.Count);
                    foreach (var posRule in POSMappings)
                    {
                        newPosIndices.Add(context.RuleSpace.POSAssignmentDictionary[posRule]);
                    }

                    var solutionData = new SolutionData
                    {
                        Parsed = parsed,
                        GrammarShapeVector = grammarShapeVector,
                        CurrentNodeSet = currentNodeSet,
                        Grammar = grammar,
                        POSMappings = POSMappings,
                        ParsedRatio = parsedRatio,
                        Lambda = lambda,
                        Fitness = fitness,
                        Parent = parent,
                        CanonicalCandidate = canonicalCandidate,
                        IndexParsedInParent = indexOfParsedInParent,
                        POSIndices = newPosIndices,
                        BannedPOS = banned
                    };

                    var optimalSolutionNode = SolutionManager.InsertOptimalSolution(
                        ref localOptimumCount,
                        in context,
                        optimalNodes,
                        in solutionData);

                    if (optimalSolutionNode != null)
                    {
                        localOptimalNodesForBans ??= [];
                        localOptimalNodesForBans.Add(optimalSolutionNode);
                    }
                
            }

            if (banned != null && localOptimalNodesForBans != null)
            {
                foreach (var node in localOptimalNodesForBans)
                    node.MergeBans(banned);
            }

            var currentDepthOfGO = Interlocked.Read(ref context.OptimalSolutionsTracker.CurrentShortestDepthOfGlobalOptimum);
            if (currentDepthOfGO != long.MaxValue && currentNodeSet.Length - currentDepthOfGO >= _maxDepthAfterMinimalGlobal - 1)
            {
                return;
            }

            var attemptAdjacents = coreRules.Count <= _minimumSizeOfOptimalSubSolution || currentNodeSet.Length - rootDepth < _maxDepthBetweenSubSolutions;


            if (!_continueSearchAfterOptimalNode && localOptimumDetected)
            {
                attemptAdjacents = false;
            }
            else if (coreRules.Count > _minimumSizeOfOptimalSubSolution && currentNodeSet.Length - rootDepth >= _maxDepthBetweenSubSolutions)
            {
                attemptAdjacents = false;
            }
            else
            {
                //Use cached lookup result from beginning of method instead of redundant dictionary access
                if (!localOptimumDetected && hasRootOptimalNode)
                {
                    // compute delta = currentNodeSet \ currentRootNodeSet
                    int indicesLength = currentNodeSet.Length - currentRootNodeSet.Length;
                    var deltaRules = t_deltaRuleIndices ??= new List<ushort>(Math.Max(4, indicesLength));
                    deltaRules.Clear();
                    deltaRules.EnsureCapacity(indicesLength);
                    int ri = 0, rj = 0;
                    while (ri < currentNodeSet.Length)
                    {
                        if (rj >= currentRootNodeSet.Length || currentNodeSet[ri] < currentRootNodeSet[rj])
                            deltaRules.Add(currentNodeSet[ri++]);
                        else if (currentNodeSet[ri] == currentRootNodeSet[rj]) { ri++; rj++; }
                        else rj++;
                    }
                    RuleProductivityTester.TestProductiveRuleOfNonOptimalNodes(context.RootCoreRules, deltaRules, ref attemptAdjacents, context.RuleSpace);
                }
            }

            if (attemptAdjacents)
            {
                var adjacentRules = CanonicalAdjacencyGenerator.GetAdjacentRuleIndicesForBFS(
                    _maxNumberOfRules,
                    coreRules,
                    currentNodeSet,
                    context.RuleSpace,
                    addedRule: addedRule);

                bool useGuidedCandidates = TryBuildWildcardGuidedAllowedCandidates(
                    adjacentRules,
                    currentNodeSet,
                    currentRootNodeSet,
                    in context,
                    out var guidedAllowedCandidates,
                    out var guidedCandidateMappingBits,
                    out int guidedMappingWordCount,
                    out var guidedFallbackReason);
                if (!useGuidedCandidates && adjacentRules.Count != 0)
                    LogWildcardGuidedFallback(
                        context.Logger,
                        "inner-adjacency",
                        guidedFallbackReason,
                        adjacentRules.Count,
                        currentNodeSet,
                        currentRootNodeSet);

                int previousMappingCount = context.PreviousPosMappings == null ? 0 : context.PreviousPosMappings.Count;
                int childDepth = currentNodeSet.Length + 1;
                for (int adjacentIndex = 0; adjacentIndex < adjacentRules.Count; adjacentIndex++)
                {
                    if (useGuidedCandidates)
                    {
                        bool allowed = IsGuidedCandidateAllowed(guidedAllowedCandidates, adjacentIndex);
                        if (!allowed)
                        {
                            continue;
                        }
                    }

                    ushort childAddedRule = adjacentRules[adjacentIndex];
                    var childEnqueueMode = GetChildEnqueueMode(childDepth, currentDepthOfGO);
                    if (childEnqueueMode == ChildEnqueueMode.Skip)
                    {
                        continue;
                    }

                    var childNodeSet = CanonicalAdjacencyGenerator.MaterializeAdjacentNodeSet(currentNodeSet, childAddedRule);
                    if (useGuidedCandidates)
                    {
                        var guidedPreviousMappingSelection = GetGuidedPreviousMappingSelection(
                            guidedCandidateMappingBits,
                            guidedMappingWordCount,
                            previousMappingCount,
                            adjacentIndex);
                        nextFrontierQueue.Enqueue(new FrontierQueueItem(
                            childNodeSet,
                            childAddedRule,
                            banned,
                            guidedPreviousMappingSelection));
                        continue;
                    }

                    nextFrontierQueue.Enqueue(new FrontierQueueItem(
                            childNodeSet,
                            childAddedRule,
                            banned,
                            PreviousMappingSelection.All));
                }
            }
        }


        private static BannedPOSChain AddBannedPOSMapping(
            ushort[] posMappings,
            HashSet<Rule> previousPosMappings,
            LatticeRuleSpace ruleSpace,
            BannedPOSChain banned)
        {
            if (posMappings == null && previousPosMappings == null) return banned;

            var posBitset = POSBitset.FromPOSMappingIndices(
                posMappings,
                previousPosMappings,
                ruleSpace.POSAssignmentDictionary,
                ruleSpace.POSAssignmentRules.Length);

            if (BannedPOSChain.IsBanned(banned, in posBitset))
                return banned;

            return new BannedPOSChain(posBitset, banned);
        }

        private static (List<RootPruneContext> Contexts, List<int[]> NextUnparsed) BuildWildcardPruneMappingData(
            List<Rule> rootCoreRules,
            List<HashSet<Rule>> previousPosMappings,
            List<List<CompressionRange>> listOfPreviousParsed,
            EvidenceTreesShape evidenceCalc)
        {
            int mappingCount = previousPosMappings == null || previousPosMappings.Count == 0
                ? 1
                : previousPosMappings.Count;
            var contexts = new List<RootPruneContext>(mappingCount);
            var nextUnparsed = new List<int[]>(mappingCount);
            var shadowEvidence = evidenceCalc.WildcardShadowEvidence;

            if (previousPosMappings == null || previousPosMappings.Count == 0)
            {
                var next = FindNextUnparsedPOSSequence(previousParsedCompressed: null, evidenceCalc);
                contexts.Add(WildcardPrune.BuildContext(rootCoreRules, rootPosMappings: null, maxLen: next?.Length ?? -1, shadowEvidence: shadowEvidence));
                nextUnparsed.Add(next);
                return (contexts, nextUnparsed);
            }

            for (int i = 0; i < previousPosMappings.Count; i++)
            {
                var singleMapping = new List<HashSet<Rule>>(1) { previousPosMappings[i] };
                var previousParsed = listOfPreviousParsed != null && i < listOfPreviousParsed.Count
                    ? listOfPreviousParsed[i]
                    : null;
                var next = FindNextUnparsedPOSSequence(previousParsed, evidenceCalc);
                contexts.Add(WildcardPrune.BuildContext(rootCoreRules, singleMapping, maxLen: next?.Length ?? -1, shadowEvidence: shadowEvidence));
                nextUnparsed.Add(next);
            }

            return (contexts, nextUnparsed);
        }

        /// <summary>
        /// Finds the POS sequence of the shortest sentence not parsed by one previous-POS-mapping
        /// option (or the first sentence if there is no parent). Returns null if no unparsed sentence
        /// exists or if the next unparsed sentence has unknown POS tags.
        /// </summary>
        private static int[] FindNextUnparsedPOSSequence(
            List<CompressionRange> previousParsedCompressed,
            EvidenceTreesShape evidenceCalc)
        {
            var sentencesPOS = evidenceCalc.SentencesPOS;
            if (previousParsedCompressed == null)
            {
                // No parent — first sentence (shortest, since sentences are ordered by length).
                return sentencesPOS.Length > 0 ? sentencesPOS[0] : null;
            }
            int nextUnparsedIndex = ArrayCompressor.FindFirstValue(
                previousParsedCompressed,
                0,
                sentencesPOS.Length);
            return nextUnparsedIndex >= 0 ? sentencesPOS[nextUnparsedIndex] : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double, List<List<CompressionRange>>, List<HashSet<Rule>>, List<int>) GetParentParses(
            OptimalSolutionNode parent,
            LatticeRuleSpace ruleSpace)
        {
            var listOfPreviousParsed = new List<List<CompressionRange>>();
            var previousPosMappings = new List<HashSet<Rule>>();
            var previousMinFitness = double.PositiveInfinity;
            List<int> listOfIndicesInParent = new List<int>();

            if (parent != null)
            {
                GrammarExtensions.AccumulatePreviousOptimalMappingandParsed(
                    parent,
                    listOfPreviousParsed,
                    previousPosMappings,
                    listOfIndicesInParent,
                    ruleSpace);
            }

            return (previousMinFitness, listOfPreviousParsed, previousPosMappings, listOfIndicesInParent);
        }  
    }
}
