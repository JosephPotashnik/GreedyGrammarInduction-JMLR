// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace EarleyParserForGreedyGrammarInduction
{

    /// <summary>
    /// This class generates list of Context Free or Context Sensitive Rules from a text file.
    /// see CFGExample.txt and LIGExample.txt for example grammars.
    /// </summary>
    public partial class GrammarFileReader
    {


        public static List<Rule> ReadRulesFromFile(string filename, bool isTest = false)
        {
            string line;
            var comment = '#';
            var dir = Directory.GetCurrentDirectory();

            if (!isTest)
            {
                filename = Path.Combine([".", "InputData", "ContextFreeGrammars", filename]);
            }
            else
            {
                filename = Path.Combine([".", "InputData", "TestGrammars", filename]);
            }

            var rules = new List<Rule>();
            using (var file = File.OpenText(filename))
            {
                while ((line = file.ReadLine()) != null)
                {

                    if (line.Length == 0 || line[0] == comment)
                    {
                        continue;
                    }

                    int found = line.IndexOf(". ");
                    if (found >= 0)
                    {
                        line = line[(found + 2)..];
                    }

                    var r = CreateRule(line);
                    if (r.HasValue)
                    {
                        rules.Add(r.Value);
                    }
                }
            }

            return rules;
        }

        private static readonly char[] _whitespace = { ' ', '	' };
        public static Rule? CreateRule(string s)
        {
            var removeArrow = s.Replace("->", "");
            var ruleType = RuleType.CFGRules;
            //string formatted incorrectly. (no "->" found).
            if (s == removeArrow)
            {
                return null;
            }

            var splitParts = removeArrow.Split(_whitespace, System.StringSplitOptions.RemoveEmptyEntries);
            var filteredParts = new List<string>();
            for (int i = 0; i < splitParts.Length; i++)
            {
                var trimmed = splitParts[i].Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    filteredParts.Add(trimmed);
                }
            }
            var nonTerminals = filteredParts.ToArray();

            var leftHandCat = SymbolTable.Instance.GetId(nonTerminals[0]);

            var rightHandCategories = new int[nonTerminals.Length - 1];
            for (var i = 1; i < nonTerminals.Length; i++)
            {
                rightHandCategories[i - 1] = SymbolTable.Instance.GetId(nonTerminals[i]);
            }
            return new Rule(leftHandCat, rightHandCategories, ruleType);
        }

        [GeneratedRegex(@"(?<BaseCategory>\w*)(\[(?<Stack>[\w\*]*)\])?")]
        private static partial Regex NonterminalRegex();
    }
}
