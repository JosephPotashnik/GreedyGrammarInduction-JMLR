// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// this class is responsible of holding all ambiguous completed states with the same given LHS and same span.
    /// it contains a list of Reductor States/Items (completed Items).
    /// </summary>
    public class EarleySpan
    {
        public EarleySpan()
        {
            Reductors = new List<EarleyState>(4); // Pre-allocate with reasonable capacity
        }
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 23) + LeftHandSide.GetHashCode();
                hash = (hash * 23) + EndColumn.Index;
                hash = (hash * 23) + StartColumn.Index;
                return hash;
            }
        }

        public int LeftHandSide { get; set; }
        public EarleyColumn StartColumn { get; set; }
        public EarleyColumn EndColumn { get; set; }
        public List<EarleyState> Reductors { get; }
        public void Add(EarleyState state) => Reductors.Add(state);

        //this function returns the total number of strings rooted at this Span
        //strings can be either:
        //(i) bracketed representation of the subtree, e.g. (START (NP (PN John)) (VP (V0 cried)))
        //(ii) parts of speech sequences of the subtree, e.g. PN V0 (PN = proper noun).
        public List<StringBuilder> GetFormattedString(
            Grammar g,
            Dictionary<EarleySpan, Color> visitedSpans,
            Dictionary<EarleyState, Color> visitedStates,
            bool onlyPartsOfSpeechSequences)
        {
            //the visited dictionary is aimed at detecting cycles in the parse forest 
            //(which may happen with grammars containing unit productions, eg. NP -> NP2, NP2 -> NP, NP -> D N)
            if (visitedSpans.TryGetValue(this, out var color) && color == Color.Gray)
            {
                return new List<StringBuilder>();
            }

            visitedSpans[this] = Color.Gray;
            try
            {
                var containedWrapperSBS = new List<StringBuilder>();
                var reductorCount = Reductors.Count;

                // Use for loop for better performance
                for (int i = 0; i < reductorCount; i++)
                {
                    var reductor = Reductors[i];
                    var containedSBS = reductor.GetFormattedString(g, visitedSpans, visitedStates, onlyPartsOfSpeechSequences);
                    var containedCount = containedSBS.Count;

                    for (int j = 0; j < containedCount; j++)
                    {
                        var sb = containedSBS[j];
                        if (!onlyPartsOfSpeechSequences)
                        {
                            sb.Insert(0, " ");
                            sb.Insert(0, SymbolTable.Instance.GetSymbol(LeftHandSide));
                            sb.Insert(0, "(");
                            sb.Append(')');
                        }
                        containedWrapperSBS.Add(sb);
                    }
                }

                return containedWrapperSBS;
            }
            finally
            {
                visitedSpans[this] = Color.Black;
            }
        }


        //this function counts the total number of subtrees rooted at this Span.
        public int CountDerivations(Dictionary<EarleySpan, int> visited)
        {
            int totalDerivations = 0;

            if (!visited.ContainsKey(this))
            {
                visited.Add(this, 0); //0 derivations means GRAY color - span is being processed but its descendants are still explored in the DFS.
            }

            // Use for loop for better performance
            var reductorCount = Reductors.Count;
            for (int i = 0; i < reductorCount; i++)
            {
                int count = Reductors[i].CountDerivations(visited);
                totalDerivations += count;
            }
            visited[this] = totalDerivations; //after all descendants are processed, store the count. count != 0 means BLACK color.

            return totalDerivations;
        }

    }

    /// <summary>
    /// this class manages a dictionary of completed states of same given LHS and all ranges of spans.
    /// Optimized with array-based lookups indexed by span length.
    /// </summary>
    internal class EarleySpansOfCompletedCategory
    {
        // Array-based storage indexed by span length
        internal EarleySpan[] _spansByLength;

        // Track which span indices are actually used (for fast Clear)
        private readonly List<int> _usedSpanIndices = new List<int>(16);

        internal EarleySpansOfCompletedCategory(int maxSpan)
        {
            // Array size = maxSpan + 1 (to include span 0)
            _spansByLength = new EarleySpan[maxSpan + 1];
        }

        public void Clear(EarleySpanPool pool, EarleyStatePool statePool)
        {
            // Clear only used spans (much faster than iterating entire array)
            for (int i = 0; i < _usedSpanIndices.Count; i++)
            {
                int spanIndex = _usedSpanIndices[i];
                var span = _spansByLength[spanIndex];
                foreach (var item in span.Reductors)
                {
                    statePool.Return(item);
                }
                pool.Return(span);
                // Set to null so next parse will create fresh span
                _spansByLength[spanIndex] = null;
            }
            _usedSpanIndices.Clear();
        }

        //Spontaneous dot shift creates new consequent States/Items for a given predecessor and all possible existing reductors.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SpontaneousDotShift(EarleyState state, Grammar grammar, EarleyStatePool statePool)
        {
            var rule = state.Rule;
            var dotIndex = state.DotIndex + 1;
            var startColumn = state.StartColumn;

            // Iterate only over used span indices to avoid null checks
            for (int i = 0; i < _usedSpanIndices.Count; i++)
            {
                int spanIndex = _usedSpanIndices[i];
                var reductorWithspan = _spansByLength[spanIndex];
                var newState = statePool.Rent(rule, dotIndex, startColumn);
                newState.Predecessor = state;
                newState.ReductorSpan = reductorWithspan;
                reductorWithspan.EndColumn.AddState(newState, grammar);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(int span, out EarleySpan _val)
        {
            _val = _spansByLength[span];
            return _val != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int span, EarleySpan val)
        {
            _spansByLength[span] = val;
            _usedSpanIndices.Add(span);  // Track this index for fast Clear
        }

    }

    /// <summary>
    /// this class manages a dictionary of completed nonterminals, and all their span objects
    /// each nonterminal may have several spans (of length 0,1,2,..etc). Each Span may have ambiguous Reductor States/Items.
    /// Optimized with array-based lookups for both category and span dimensions.
    /// </summary>
    internal class EarleySpans
    {
        // Array-based storage indexed by completedCategory (symbol ID)
        internal EarleySpansOfCompletedCategory[] _reductorsByCategory;

        // Track which category indices are actually used (for fast Clear)
        private readonly List<int> _usedCategoryIndices = new List<int>(16);

        private readonly int _sentenceLength;
        private readonly int _columnIndex;

        internal EarleySpans(int sentenceLength, int columnIndex)
        {
            _sentenceLength = sentenceLength;
            _columnIndex = columnIndex;

            // Initialize array-based lookup with size based on symbol table
            int maxSymbolId = SymbolTable.Instance.Count;
            _reductorsByCategory = new EarleySpansOfCompletedCategory[maxSymbolId];
        }

        //This function adds a new reductor to an appropriate Span object.
        //If the span object already exists, the function reports that it encountered local ambiguity.

        public void Clear(EarleySpanPool pool, EarleyStatePool statePool)
        {
            // Clear only used categories (much faster than iterating entire array)
            for (int i = 0; i < _usedCategoryIndices.Count; i++)
            {
                int categoryIndex = _usedCategoryIndices[i];
                var category = _reductorsByCategory[categoryIndex];
                category.Clear(pool, statePool);
                // Set to null so next parse will create fresh category
                _reductorsByCategory[categoryIndex] = null;
            }
            _usedCategoryIndices.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (EarleySpan, bool) AddState(EarleyState reductor, EarleySpanPool spanPool)
        {
            var completedCat = reductor.Rule.LeftHandSide;

            // Get or create category array (array-based lookup)
            var reductorsBySpan = _reductorsByCategory[completedCat];
            if (reductorsBySpan == null)
            {
                // Calculate max span for this column: sentenceLength - columnIndex
                int maxSpan = _sentenceLength - _columnIndex;
                reductorsBySpan = new EarleySpansOfCompletedCategory(maxSpan);
                _reductorsByCategory[completedCat] = reductorsBySpan;
                _usedCategoryIndices.Add(completedCat);  // Track this index for fast Clear
            }

            int span = reductor.EndColumn.Index - reductor.StartColumn.Index;

            bool localAmbiguityFound;
            EarleySpan reductorsInLength;

            if (!reductorsBySpan.TryGetValue(span, out reductorsInLength))
            {
                reductorsInLength = spanPool.Rent(reductor);
                reductorsBySpan.Add(span, reductorsInLength);
                localAmbiguityFound = false;
            }
            else
            {
                // Same span with same category - local ambiguity
                localAmbiguityFound = true;
            }

            reductorsInLength.Add(reductor);
            return (reductorsInLength, localAmbiguityFound);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(int completedCat, out EarleySpansOfCompletedCategory _innerDic)
        {
            _innerDic = _reductorsByCategory[completedCat];
            return _innerDic != null;
        }
    }
}
