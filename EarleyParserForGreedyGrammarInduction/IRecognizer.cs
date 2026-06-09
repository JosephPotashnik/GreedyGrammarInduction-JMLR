// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
namespace EarleyParserForGreedyGrammarInduction;

/// <summary>
/// Interface for grammar recognizers that can determine if a sentence
/// is accepted by a given grammar. Implementations are created per-sentence
/// and called repeatedly with different grammars.
/// </summary>
public interface IRecognizer
{
    /// <summary>
    /// Recognizes whether the sentence (provided at construction) is accepted by the given grammar.
    /// </summary>
    /// <param name="g">The grammar to recognize against</param>
    /// <returns>(accepted, parsedCode) where parsedCode is 1 if parsed, 0 otherwise</returns>
    (bool accepted, byte parsedCode) RecognizeSentence(Grammar g);

    /// <summary>
    /// Resets the recognizer state for reuse with a new grammar.
    /// </summary>
    void Reset();
}
