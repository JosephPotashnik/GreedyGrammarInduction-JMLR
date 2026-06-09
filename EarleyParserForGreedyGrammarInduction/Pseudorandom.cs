// Copyright (c) 2026 Joseph Potashnik.
// Licensed under the MIT License. See LICENSE.txt for details.
using System;
using System.Security.Cryptography;
using System.Threading;

namespace EarleyParserForGreedyGrammarInduction
{
    public static class Pseudorandom
    {
        private static readonly ThreadLocal<Random> Prng = new ThreadLocal<Random>(() => new Random(NextSeed()));

        private static int NextSeed()
        {
            var bytes = new byte[sizeof(int)];
            RandomNumberGenerator.Fill(bytes);
            return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
        }

        public static int NextInt(int range) => Prng.Value.Next(range);

        public static int NextInt() => Prng.Value.Next();
    }
}