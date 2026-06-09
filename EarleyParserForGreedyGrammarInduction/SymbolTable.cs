// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EarleyParserForGreedyGrammarInduction
{
    public class SymbolTable
    {
        private static SymbolTable s_instance;
        public static SymbolTable Instance => s_instance;

        public static void Init()
        {
            s_instance = new SymbolTable();
        }

        private readonly Dictionary<string, int> _symbolToId;
        private readonly List<string> _idToSymbol;
        private int _nextId;

        private SymbolTable()
        {
            _symbolToId = [];
            _idToSymbol = [];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetId(string symbol)
        {
            if (!_symbolToId.TryGetValue(symbol, out int id))
            {
                id = _nextId++;
                _symbolToId[symbol] = id;
                _idToSymbol.Add(symbol);
            }
            return id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string GetSymbol(int id)
        {
            return _idToSymbol[id];
        }

        public int Count => _nextId;
    }
}
