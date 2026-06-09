// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Collections.Generic;

namespace EarleyParserForGreedyGrammarInduction;

public class ScannedRulesDict
{
    public Dictionary<int, Rule> ScannedRules { get; set; }

    public ScannedRulesDict(Lexicon universalLexicon)
    {
        ScannedRules = new Dictionary<int, Rule>();
        var emptystring = Array.Empty<int>();
        foreach (var posId in universalLexicon.POSWithPossibleWords.Keys)
        {
            var scannedStateRule = new Rule(posId, emptystring);
            ScannedRules.Add(posId, scannedStateRule);
        }
    }
}
