// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EarleyParserForGreedyGrammarInduction
{
    /// <summary>
    /// Lexicon class is responsible for storing all words with their possible Parts of Speech (POS).
    /// The corresponding production rules, PART-OF-SPEECH -> 'token', are kept separate from the Grammar class for purposes of clarity and functionality.
    /// Note: The grammar class allows lexicalized rules (e.g, A -> 'John', B -> 'John' 'left' , C -> 'John' D).
    /// 
    /// EarleyParser has access to the lexicon in order to create the relevant Earley Items [PART-OF-SPEECH -> 'token', i, i] 
    /// in a pre-processing step, according to the input sentence.
    /// 
    /// example of a lexicon may be found in Lexicon.json
    /// </summary>
    public class Lexicon
    {
        private static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public Lexicon()
        {
            WordWithPossiblePOS = new Dictionary<string, HashSet<int>>();
            POSWithPossibleWords = new Dictionary<int, HashSet<string>>();
        }

        // key = word, value = possible POS
        [JsonIgnore]
        public Dictionary<string, HashSet<int>> WordWithPossiblePOS { get; set; }

        // key = POS, value = words having the same POS.
        public Dictionary<int, HashSet<string>> POSWithPossibleWords { get; set; }

        [JsonIgnore]
        public HashSet<int> this[string word]
        {
            get
            {
                if (WordWithPossiblePOS.TryGetValue(word, out var value))
                {
                    return value;
                }

                return null;
            }
        }


        public static Lexicon ReadLexiconFromFile(string jsonFileName, bool updateGrammarPartsOfSpeech = true)
        {
            Lexicon lexicon;
            jsonFileName = Path.Combine([".", "InputData", "Lexicons", jsonFileName]);

            //deserialize JSON directly from a file
            using var file = File.OpenRead(jsonFileName);
            var stringLexicon = JsonSerializer.Deserialize<LexiconWithStringKeys>(file, s_jsonSerializerOptions);

            lexicon = new Lexicon();
            foreach (var kvp in stringLexicon.POSWithPossibleWords)
            {
                var posId = SymbolTable.Instance.GetId(kvp.Key);
                lexicon.POSWithPossibleWords[posId] = kvp.Value;
            }


            lexicon.PopulateDependentJsonPropertys();
            lexicon.Disambiguate();

            if (updateGrammarPartsOfSpeech)
            {
                Grammar.PartsOfSpeech = new HashSet<int>(lexicon.POSWithPossibleWords.Keys);
            }

            return lexicon;
        }

        public bool ContainsPOS(int posId) => POSWithPossibleWords.ContainsKey(posId);

        public void AddWordsToPOSCategory(int posCatId, string[] words)
        {
            foreach (var word in words)
            {
                if (!WordWithPossiblePOS.TryGetValue(word, out var set))
                {
                    set = new HashSet<int>();
                    WordWithPossiblePOS[word] = set;
                }

                set.Add(posCatId);
            }

            if (!POSWithPossibleWords.TryGetValue(posCatId, out var value))
            {
                value = new HashSet<string>();
                POSWithPossibleWords[posCatId] = value;
            }

            foreach (var word in words)
            {
                value.Add(word);
            }
        }


        //the function initializes WordWithPossiblePOS field after POSWithPossibleWords has been read from a json file.
        private void PopulateDependentJsonPropertys()
        {
            foreach (var kvp in POSWithPossibleWords)
            {
                var words = kvp.Value;
                foreach (var word in words)
                {
                    if (!WordWithPossiblePOS.TryGetValue(word, out var value))
                    {
                        value = new HashSet<int>();
                        WordWithPossiblePOS[word] = value;
                    }

                    value.Add(kvp.Key);
                }
            }
        }

        public void Disambiguate()
        {

            HashSet<string> ambiguousWords = new HashSet<string>();
            foreach (var kvp in POSWithPossibleWords)
            {
                var words = kvp.Value;
                var pos = kvp.Key;
                foreach (var word in words)
                {
                    if (WordWithPossiblePOS[word].Count > 1)
                    {
                        ambiguousWords.Add(word);
                    }
                }
            }

            foreach (var word in ambiguousWords)
            {
                var poses = WordWithPossiblePOS[word];
                WordWithPossiblePOS.Remove(word);

                foreach (var pos in poses)
                {
                    POSWithPossibleWords[pos].Remove(word);
                }
            }
        }

        public HashSet<(int rhs1, int rhs2)> GetBigramsOfData(string[][] data)
        {
            var bigrams = new HashSet<(int rhs1, int rhs2)>();

            foreach (var words in data)
            {
                for (var i = 0; i < words.Length - 1; i++)
                {
                    var rhs1 = words[i];
                    var rhs2 = words[i + 1];

                    var possiblePOSForRHS1 = WordWithPossiblePOS[rhs1];
                    var possiblePOSForRHS2 = WordWithPossiblePOS[rhs2];

                    foreach (var pos1 in possiblePOSForRHS1)
                    {
                        foreach (var pos2 in possiblePOSForRHS2)
                        {
                            bigrams.Add((pos1, pos2));
                        }
                    }
                }
            }

            return bigrams;
        }

        private class LexiconWithStringKeys
        {
            public Dictionary<string, HashSet<string>> POSWithPossibleWords { get; set; }
        }
    }
}
