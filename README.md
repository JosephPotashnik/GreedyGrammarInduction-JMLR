# Greedy Grammar Induction

This repository accompanies the arXiv preprint
[**"Greedy Grammar Induction with Indirect Negative Evidence"**](https://arxiv.org/abs/2312.15321).
It is prepared as the public reproducibility repository for journal review.

It contains the C# implementation of the learner, parser and grammar machinery,
benchmark configurations, fixed-seed test-suite logs, packaged native nauty
wrappers, and documentation needed for reproducibility.
Development-only Web API and React UI projects are intentionally
omitted because they are not required to reproduce the manuscript's
computational claims.

## Overview

The system learns context-free grammars from unlabeled sentences using a greedy
search through bounded structural grammar space. Candidate grammars are
evaluated by comparing their grammar-shape vectors against the evidence induced
by the observed sentence set.

The main pipeline is:

1. Generate or load evidence sentences.
2. Build canonical structural grammar extensions.
3. Filter canonical adjacents with wildcard-guided generation.
4. Use POS abduction to fit lexical/preterminal assignments.
5. Evaluate shape, evidence coverage, lambda, Pareto status, and equivalence.
6. Report learned grammars and post-learner verification results.

## Prerequisites

- Windows x64 or Linux x64
- .NET SDK 10.0 or newer, available from the official .NET 10 download page:
  <https://dotnet.microsoft.com/download/dotnet/10.0>

On Ubuntu, install the .NET SDK using Microsoft's official Linux package
instructions: <https://learn.microsoft.com/dotnet/core/install/linux-ubuntu>.

This repository does not require Node.js, npm, a browser, or a web server.

This repository includes packaged native nauty wrapper binaries for Windows x64
and Linux x64:

- Windows x64: `GreedyGrammarInduction/runtimes/win-x64/native/`, including
  `nauty_wrapper.dll` and its MinGW runtime dependencies.
- Linux x64: `GreedyGrammarInduction/runtimes/linux-x64/native/libnauty_wrapper.so`.

The Linux x64 binary was built and tested under Ubuntu on WSL2. The full
`TestSuiteFixedSeed.json` batch was validated from a clean clone of the
`JMLRPreparation` branch using the packaged Linux native binary. macOS is not
packaged with this repository.

To build a replacement native wrapper, compile
`GreedyGrammarInduction/nauty_wrapper.c` against a local nauty source
distribution. Download nauty from the official nauty and Traces page:
<https://pallini.di.uniroma1.it/>

Rebuilding the native wrapper is optional. On Linux, it requires standard build
tools:

```bash
sudo apt update
sudo apt install -y build-essential
```

After unpacking nauty, copy or reference `nauty_wrapper.c` from this repository
and compile it from the nauty source directory. On Linux:

```bash
gcc -O3 -DUSE_TLS -shared -fPIC \
  -o libnauty_wrapper.so \
  /path/to/GreedyGrammarInduction/nauty_wrapper.c \
  nauty.c nautil.c naugraph.c schreier.c naurng.c nausparse.c gtools.c \
  -I.
```

Place the resulting native library next to the built .NET executable, for
example under `GreedyGrammarInduction/bin/Release/net10.0/`, or replace the
corresponding file under `GreedyGrammarInduction/runtimes/<rid>/native/`.

To verify that the .NET SDK is available, run:

```bash
dotnet --version
```

The command should print a `10.x` SDK version. Install the SDK, not only the
.NET runtime.

## Quick Start

Clone the repository and check out the release tag:

```bash
git clone https://github.com/JosephPotashnik/GreedyGrammarInduction-JMLR.git
cd GreedyGrammarInduction-JMLR
git checkout v1.0
```

If using a ZIP archive instead of Git, unpack it and change into the
unpacked repository root. The repository root is the directory containing
`GreedyGrammarInduction.sln`.

Build from the repository root:

```bash
dotnet build --configuration Release
```

Do not omit `--configuration Release` for benchmark runs: plain `dotnet build`
uses Debug by default.

The first build may restore NuGet packages from the public NuGet feed unless
they are already available in the local NuGet cache.

The runnable console project is one directory deeper, at
`GreedyGrammarInduction/GreedyGrammarInduction.csproj`. After building from the
repository root, run a console verification batch with either of these two
equivalent approaches.

Option 1: stay in the repository root and pass the project path explicitly:

```bash
dotnet run --project GreedyGrammarInduction/GreedyGrammarInduction.csproj --configuration Release -- -FileName: QuickTestSuiteFixedSeed.json
```

Option 2: change into the console project directory first:

```bash
cd GreedyGrammarInduction
dotnet run --configuration Release -- -FileName: QuickTestSuiteFixedSeed.json
```

Do not run `dotnet run --configuration Release` from the repository root without
`--project`; that directory contains the solution file, but not the runnable
console project, so the .NET CLI cannot choose a project to run.

The console runner will execute the specified configuration and write the
results to the console standard output. For Release runs, generated artifacts
are saved under `GreedyGrammarInduction/bin/Release/net10.0/`. This includes the
aggregate `RunReport_*.txt` file, the individual log/output files for each
benchmark case in the batch, and any generated sample files. The
`QuickTestSuiteFixedSeed.json` batch is the recommended first run. It includes
all fixed-seed grammars from `TestSuiteFixedSeed.json` except the final
inherently ambiguous `AMBNCNANBNCM.txt` case, which may take a few hours to
learn. A successful benchmark run reports that the requested weak- and
strong-equivalence verification checks passed.

For the full fixed-seed reproducibility run used for Table 1 of the manuscript,
including the final inherently ambiguous grammar, use `TestSuiteFixedSeed.json`.
From the repository root:

```bash
dotnet run --project GreedyGrammarInduction/GreedyGrammarInduction.csproj --configuration Release -- -FileName: TestSuiteFixedSeed.json
```

Or, after `cd GreedyGrammarInduction`:

```bash
dotnet run --configuration Release -- -FileName: TestSuiteFixedSeed.json
```

To run a different batch, pass a file name from
`GreedyGrammarInduction/InputData/ProgramsToRun/`. From the repository root:

```bash
dotnet run --project GreedyGrammarInduction/GreedyGrammarInduction.csproj --configuration Release -- -FileName: TestSuite.json
```

Or, after `cd GreedyGrammarInduction`:

```bash
dotnet run --configuration Release -- -FileName: TestSuite.json
```

## Console Configuration

Batch runs are JSON files under
`GreedyGrammarInduction/InputData/ProgramsToRun/`. Each file contains a
`ProgramsToRun` array. A typical entry has:

```json
{
  "ProgramsToRun": [
    {
      "InputParams": {
        "SourceOfTruth": "Grammar",
        "LexiconSource": "File",
        "GrammarFileName": "EnglishFragment.txt",
        "LexiconFileName": "Lexicon.json",
        "MaxSentenceLength": 10,
        "GrammarSamplingParams": {
          "AllowedMissingProbabilityMass": 0.001,
          "DistributionType": "PowerLaw",
          "RandomSeed": 12345,
          "OutputSamplesToFile": "Samples/EnglishFragmentSamples.txt"
        }
      },
      "SearchSpaceParams": {
        "MaxNumberOfNonTerminals": 10,
        "MaxNumberOfRules": 20,
        "MaxDepthBetweenSubSolutions": 1,
        "MaxDepthAfterMinimalGlobal": 1,
        "MinSizeOfOptimalSubSolution": 2,
        "ParetoRibbonThickness": 0.001,
        "SearchSpaceHeuristicsParams": {
          "ContinueSearchAfterOptimalNode": true,
          "SkipKnownOptimalStructuralNodesAcrossRoots": false
        }
      },
      "PostLearnerVerification": {
        "ResultsContainGrammarWeaklyEquivalentTo": "EnglishFragment.txt",
        "ResultsContainGrammarStronglyEquivalentTo": "EnglishFragmentK1Target.txt"
      }
    }
  ]
}
```

### Input Files

Grammar files live under
`GreedyGrammarInduction/InputData/ContextFreeGrammars/`. They contain structural
production rules:

```text
START -> NP VP
NP -> D N
VP -> V NP
```

Lexicon files live under `GreedyGrammarInduction/InputData/Lexicons/` and map
POS categories to possible surface tokens:

```json
{
  "POSwithPossibleWords": {
    "D": ["the", "a"],
    "N": ["cat", "dog"],
    "V": ["sees", "runs"]
  }
}
```

Target grammars for strong-equivalence checks live under
`GreedyGrammarInduction/InputData/TargetGrammars/`. Sample-driven configurations
read counted samples from `GreedyGrammarInduction/InputData/Samples/`.

## Project Structure

```text
GreedyGrammarInduction/
|-- GreedyGrammarInduction/                 # Console application (Main entry point)
|   |-- Program.cs
|   `-- Services/
|       |-- SentenceGenerationService.cs
|       |-- GrammarLearnerService.cs
|       `-- SentenceDataProcessor.cs
|-- GreedyGrammarInductionLearner/          # Core bounded-search algorithm
|   |-- HypothesesSearchSpace.cs            # Greedy lattice search and Pareto tracking
|   |-- EvidenceTreesShape.cs               # Finite-horizon density evaluation
|   `-- OptimalSolutionsTracker.cs          # Tracks non-dominated global optima
|-- EarleyParserForGreedyGrammarInduction/  # Structural verification module
|   |-- EarleyParser.cs                     # Abduces preterminal mappings
|   |-- Grammar.cs                          # Grammar representation & bound computation
|   `-- ContextFreeGrammar.cs               # Strict binary normal form implementation
|-- EarleyRecognizer/                       # Fast boolean satisfiability testing
|   `-- EarleyRecognizer.cs
`-- InputData/                              # Reproducibility verification suite
    |-- ContextFreeGrammars/                # Grammars for weak-equivalence targets
    |-- Lexicons/                           # External word-to-preterminal mappings
    |-- TargetGrammars/                     # Lattice-shaped targets for strong equivalence
    `-- ProgramsToRun/                      # Configuration files for benchmarking
```

## Key Concepts

### Grammar Shape Vector

For a grammar `G`, `shape[i]` is the number of distinct strings of length `i`
derivable from `START`.

### Evidence Vector

For observed sentences `S`, `evidence[i]` is the number of observed sentence
types of length `i`.

### Fitness Criterion

A grammar fits the evidence when its shape vector matches the evidence vector
up to the active exposure length, so it generates exactly the observed
distribution of string lengths at that stage.

### Wildcard-Guided Generation

For each canonical adjacency batch, the learner computes which one-rule
extensions can participate in a wildcard-matching derivation of the active next
unparsed evidence sentence. Only guided survivors are enqueued when the guide is
supported; unsupported batches conservatively keep the full canonical list.

### POS Abduction And Strong Equivalence

`GetMaxFit` uses CKY POS abduction to find lexical/preterminal assignments for
guided survivors. Accepted optima are deduplicated under strong equivalence by a
lazy canonical graph path: candidates are first bucketed by refinement signature
and exact nauty canonicalization is used only on collisions.

## Main Configuration Parameters

`InputParams`:

- `SourceOfTruth`: `Grammar` or `Samples`.
- `LexiconSource`: `File` or `IdentityFromTokens`.
- `GrammarFileName`: grammar file under `InputData/ContextFreeGrammars`.
- `SamplesFileName`: counted sample file under `InputData/Samples`.
- `LexiconFileName`: lexicon file under `InputData/Lexicons`.
- `MaxSentenceLength`: maximum generated or evaluated sentence length.
- `GrammarSamplingParams`: sampling settings used when `SourceOfTruth` is
  `Grammar`.

`SearchSpaceParams`:

- `MaxNumberOfNonTerminals`: upper bound on learned nonterminals.
- `MaxNumberOfRules`: upper bound on learned production rules.
- `MaxDepthBetweenSubSolutions`: BFS depth between discovered sub-solutions.
- `MaxDepthAfterMinimalGlobal`: depth explored after the first global optimum.
- `MinSizeOfOptimalSubSolution`: minimum rules required in a solution.
- `ParetoRibbonThickness`: Pareto-front ribbon for retaining weakly equivalent
  candidates before strong-equivalence verification.
- `SearchSpaceHeuristicsParams`: optional performance heuristics.

`PostLearnerVerification`:

- `ResultsContainGrammarWeaklyEquivalentTo`: weak-equivalence target grammar.
- `ResultsContainGrammarStronglyEquivalentTo`: strong-equivalence target grammar.
- `ComparisonMode`: `PartsOfSpeech` or `SurfaceTokens`.
- `TargetLexiconFileName`: optional verification lexicon.

## Documentation

- [ARCHITECTURE.md](docs/ARCHITECTURE.md) - project organization, data flow,
  and search pipeline


## Citation

If you use this code or benchmark suite, please cite:

```text
Joseph Potashnik. Greedy Grammar Induction with Indirect Negative Evidence.
arXiv:2312.15321, 2026.
```

## License

This repository is licensed under the MIT License. See [LICENSE.txt](LICENSE.txt) for details.
