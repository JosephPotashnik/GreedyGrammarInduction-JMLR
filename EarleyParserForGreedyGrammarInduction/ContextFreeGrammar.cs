// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System.Collections.Generic;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// Implements Context Free Grammar - a list of context free production Rules.
    /// It supports the tentative addition and substraction of rules, as well as the possibility to accept/reject these changes.
    ///
    /// Note: The terminals corresponding to parts of speech (e.g. D -> 'the', A -> 'big') appear in a separate lexicon.json file
    /// See CFGExample.txt and the bundled InputData grammars for examples.
    /// </summary>
    public class ContextFreeGrammar : Grammar
    {
        public ContextFreeGrammar(IEnumerable<Rule> rules) : base(rules) { }
    }
}
