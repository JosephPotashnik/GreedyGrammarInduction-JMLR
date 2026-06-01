// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EarleyParserForGreedyGrammarInduction;

namespace GreedyGrammarInductionLearner.SearchSpace
{
    /// <summary>
    /// A compact bitset for POS assignment rule indices.
    /// Supports arbitrary sizes via a dynamically-sized ulong[] array.
    /// </summary>
#pragma warning disable CA1815 // Override equals and operator equals on value types - not needed, used only for bitwise operations
    public struct POSBitset
#pragma warning restore CA1815
    {
        private readonly ulong[] _bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public POSBitset(int capacity)
        {
            _bits = new ulong[(capacity + 63) >> 6];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index)
        {
            _bits[index >> 6] |= 1UL << (index & 63);
        }

        /// <summary>
        /// Returns true if every bit set in this bitset is also set in candidate.
        /// i.e., this ⊆ candidate.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool IsSubsetOf(in POSBitset candidate)
        {
            for (int i = 0; i < _bits.Length; i++)
            {
                if ((_bits[i] & candidate._bits[i]) != _bits[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Sets all bits from other into this bitset (this |= other).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnionWith(in POSBitset other)
        {
            for (int i = 0; i < _bits.Length; i++)
            {
                _bits[i] |= other._bits[i];
            }
        }

        /// <summary>
        /// Copies this bitset minus the bits already covered by another bitset.
        /// Used to rewrite a full banned mapping into the new-assignment residual
        /// that CKY can test while building partial choice sets.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void CopyResidualExcluding(in POSBitset covered, ulong[] destination)
        {
            for (int i = 0; i < _bits.Length; i++)
            {
                destination[i] = _bits[i] & ~covered._bits[i];
            }
        }

        public readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                for (int i = 0; i < _bits.Length; i++)
                {
                    if (_bits[i] != 0) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Creates a POSBitset from a set of POS mapping rules using the rule-to-index dictionary.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static POSBitset FromPOSMappings(HashSet<Rule> posMappings, Dictionary<Rule, ushort> posDict, int capacity)
        {
            var bitset = new POSBitset(capacity);
            foreach (var rule in posMappings)
            {
                if (posDict.TryGetValue(rule, out ushort index))
                {
                    bitset.Set(index);
                }
            }
            return bitset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static POSBitset FromPOSMappings(
            HashSet<Rule> posMappings,
            HashSet<Rule> previousPosMappings,
            Dictionary<Rule, ushort> posDict,
            int capacity)
        {
            var bitset = new POSBitset(capacity);
            AddMappings(posMappings, posDict, ref bitset);
            AddMappings(previousPosMappings, posDict, ref bitset);
            return bitset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static POSBitset FromPOSMappingIndices(
            ushort[] posMappingIndices,
            HashSet<Rule> previousPosMappings,
            Dictionary<Rule, ushort> posDict,
            int capacity)
        {
            var bitset = new POSBitset(capacity);
            AddMappingIndices(posMappingIndices, ref bitset);
            AddMappings(previousPosMappings, posDict, ref bitset);
            return bitset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static POSBitset FromPOSMappingIndices(ushort[] posMappingIndices, int capacity)
        {
            var bitset = new POSBitset(capacity);
            AddMappingIndices(posMappingIndices, ref bitset);
            return bitset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddMappings(HashSet<Rule> posMappings, Dictionary<Rule, ushort> posDict, ref POSBitset bitset)
        {
            if (posMappings == null)
            {
                return;
            }

            foreach (var rule in posMappings)
            {
                if (posDict.TryGetValue(rule, out ushort index))
                {
                    bitset.Set(index);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddMappingIndices(ushort[] posMappingIndices, ref POSBitset bitset)
        {
            if (posMappingIndices == null)
            {
                return;
            }

            for (int i = 0; i < posMappingIndices.Length; i++)
            {
                bitset.Set(posMappingIndices[i]);
            }
        }
    }
}
