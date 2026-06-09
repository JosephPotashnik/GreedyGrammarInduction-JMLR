// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System.Collections.Generic;
using System.Data;

namespace EarleyParserForGreedyGrammarInduction;
// This class holds the representation of the labeled bipartite graph.
// It includes the adjacency list and an array for vertex colors,
// which encodes the ordered nature of the grammar for nauty.
public class BipartiteGraph
{
    // The graph's adjacency list. Each index corresponds to a vertex.
    public List<List<int>> AdjacencyList { get; private set; }

    // An array to store the color for each vertex.
    // Vertices of the same "type" (e.g., same non-terminal, same ordered production)
    // are assigned the same color.
    public int[] VertexColors { get; private set; }

    public BipartiteGraph(int numberOfVertices)
    {
        AdjacencyList = new List<List<int>>(numberOfVertices);
        for (int i = 0; i < numberOfVertices; i++)
        {
            AdjacencyList.Add(new List<int>());
        }
        VertexColors = new int[numberOfVertices];
    }
}
public class CNFConverter
{
    private const int CoreNonterminalColor = 0;
    private const int StartSymbolColor = 1;
    private const int ProductionColor = 2;
    private const int PositionOccurrenceColor = 3;
    private const int PositionMarkerColorBase = 100;
    private const int POSSymbolColorBase = 10_000;

    private static int CompareRules(Rule a, Rule b)
    {
        int cmp = a.LeftHandSide.CompareTo(b.LeftHandSide);
        if (cmp != 0)
        {
            return cmp;
        }

        var aRhs = a.RightHandSide;
        var bRhs = b.RightHandSide;
        cmp = aRhs.Length.CompareTo(bRhs.Length);
        if (cmp != 0)
        {
            return cmp;
        }

        for (int i = 0; i < aRhs.Length; i++)
        {
            cmp = aRhs[i].CompareTo(bRhs[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return a.Type.CompareTo(b.Type);
    }

    // Enhanced 4-colored version with better position encoding
    public static (BipartiteGraph, Dictionary<int, int>) ConvertToEnhanced4ColorGraph(List<Rule> rules)
    {
        HashSet<int> coreNonterminals = new HashSet<int>();
        HashSet<int> POSNonterminals = new HashSet<int>();
        int startSymbol = SymbolTable.Instance.GetId(Grammar.StartSymbol);

        foreach (var rule in rules)
        {
            coreNonterminals.Add(rule.LeftHandSide);
            foreach (var symbol in rule.RightHandSide)
            {
                if (!Grammar.PartsOfSpeech.Contains(symbol))
                {
                    coreNonterminals.Add(symbol);
                }
                else
                {
                    POSNonterminals.Add(symbol);
                }
            }
        }

        var allSymbolsSet = new HashSet<int>(coreNonterminals);
        allSymbolsSet.UnionWith(POSNonterminals);
        var allSymbols = new List<int>(allSymbolsSet);
        allSymbols.Sort();

        var sortedRules = new List<Rule>(rules);
        sortedRules.Sort(CompareRules);

        // Calculate vertices needed
        int symbolCount = allSymbols.Count;
        int productionCount = sortedRules.Count;
        int positionCount = 0;
        for (int i = 0; i < sortedRules.Count; i++)
        {
            positionCount += sortedRules[i].RightHandSide.Length;
        }

        // Add "position marker" nodes to encode order more explicitly
        int maxRhsLength = 0;
        for (int i = 0; i < sortedRules.Count; i++)
        {
            if (sortedRules[i].RightHandSide.Length > maxRhsLength)
            {
                maxRhsLength = sortedRules[i].RightHandSide.Length;
            }
        }
        int positionMarkerCount = maxRhsLength;

        int totalVertices = symbolCount + productionCount + positionCount + positionMarkerCount;
        var graph = new BipartiteGraph(totalVertices);

        int runningIndex = 0;

        // Symbol nodes: regular X_i nonterminals share a color, so Nauty can rename them.
        // START and each POS symbol are fixed by color and cannot be renamed.
        var symbolToIndex = new Dictionary<int, int>();
        foreach (var symbol in allSymbols)
        {
            symbolToIndex[symbol] = runningIndex;
            graph.VertexColors[runningIndex] = symbol == startSymbol
                ? StartSymbolColor
                : coreNonterminals.Contains(symbol)
                    ? CoreNonterminalColor
                    : POSSymbolColorBase + symbol;
            runningIndex++;
        }

        // Position marker nodes. Each RHS position has a fixed color, so grammar order is explicit.
        var positionMarkerIndex = new Dictionary<int, int>();
        for (int pos = 0; pos < maxRhsLength; pos++)
        {
            positionMarkerIndex[pos] = runningIndex;
            graph.VertexColors[runningIndex] = PositionMarkerColorBase + pos;
            runningIndex++;
        }

        // Production nodes
        var productionToIndex = new Dictionary<int, int>();
        for (int i = 0; i < sortedRules.Count; i++)
        {
            productionToIndex[i] = runningIndex;
            graph.VertexColors[runningIndex] = ProductionColor;
            runningIndex++;
        }

        // Position-occurrence nodes
        for (int ruleIdx = 0; ruleIdx < sortedRules.Count; ruleIdx++)
        {
            var rule = sortedRules[ruleIdx];
            int productionNode = productionToIndex[ruleIdx];
            int lhsSymbolNode = symbolToIndex[rule.LeftHandSide];

            // Production connects to LHS
            graph.AdjacencyList[productionNode].Add(lhsSymbolNode);
            graph.AdjacencyList[lhsSymbolNode].Add(productionNode);

            // Create occurrence nodes for each RHS position
            for (int pos = 0; pos < rule.RightHandSide.Length; pos++)
            {
                int occurrenceNode = runningIndex++;
                graph.VertexColors[occurrenceNode] = PositionOccurrenceColor;

                int rhsSymbolNode = symbolToIndex[rule.RightHandSide[pos]];
                int posMarkerNode = positionMarkerIndex[pos];

                // Triple connection: production -> occurrence -> symbol
                //                    occurrence -> position_marker
                graph.AdjacencyList[productionNode].Add(occurrenceNode);
                graph.AdjacencyList[occurrenceNode].Add(productionNode);

                graph.AdjacencyList[occurrenceNode].Add(rhsSymbolNode);
                graph.AdjacencyList[rhsSymbolNode].Add(occurrenceNode);

                graph.AdjacencyList[occurrenceNode].Add(posMarkerNode);
                graph.AdjacencyList[posMarkerNode].Add(occurrenceNode);
            }
        }

        return (graph, symbolToIndex);
    }
}
