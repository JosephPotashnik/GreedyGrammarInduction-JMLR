<!-- Copyright (c) 2026 Joseph Potashnik. Licensed under the MIT License. See LICENSE.txt for details. -->

# Implementation Architecture

This document describes the computational implementation and data structures of
the bounded search framework. The learner induces non-lexicalized context-free
grammar structure: structural CFG rules are learned, while the POS-to-word
lexicon is supplied externally. POS assignment rules are abducted as part of
fitting a structural grammar to the evidence.

## Contents

1. [Execution Pipeline](#execution-pipeline)
2. [Main Search](#main-search)
3. [Canonical Adjacency](#canonical-adjacency)
4. [Wildcard-Guided Adjacency](#wildcard-guided-adjacency)
5. [POS Abduction](#pos-abduction)
6. [Hypothesis Evaluation](#hypothesis-evaluation)
7. [Search Heuristics](#search-heuristics)
8. [Strong Equivalence And Canonical Keys](#strong-equivalence-and-canonical-keys)
9. [Pareto Pruning](#pareto-pruning)
10. [Banned POS Propagation](#banned-pos-propagation)
11. [Computational Hot Paths](#computational-hot-paths)

## Execution Pipeline

1. **Input:** The console application reads configurations from
   `GreedyGrammarInduction/InputData/ProgramsToRun/`. A run either loads a
   counted sample file or loads a source grammar and external POS-to-word
   lexicon for evidence generation.
2. **Evidence Preparation:** `SentenceGenerationService` generates counted
   evidence from a grammar, or `SentenceDataProcessor` reads counted evidence
   directly from `InputData/Samples/`.
3. **Search:** `GrammarLearnerService` constructs the `LatticeRuleSpace`,
   `EvidenceTreesShape`, and `HypothesesSearchSpace` objects, then starts the
   bounded structural search.
4. **Verification:** When `PostLearnerVerification` is configured, `Oracle`
   compares discovered global optima against weak-equivalence and optional
   strong-equivalence targets. Strong-equivalence checks use canonical graph
   representations produced through the nauty wrapper.
5. **Output:** The console log reports local/global optima, growth rates,
   parent relationships, verification summaries, and the final batch report.

## Main Search

The learner performs a greedy breadth-first search over structural CFGs. The
outer queue contains optimal grammars that will be used as roots for local
expansion. For each root, an inner BFS evaluates nearby rule additions in
parallel.

```text
GreedyGrammarInduction(E, limits):
    outer <- priority queue ordered by grammar size
    outer.enqueue(empty_grammar)
    tracker <- OptimalSolutionsTracker()

    while outer is not empty:
        if tracker state changed:
            PruneOuterQueueByCurrentState(outer, tracker)

        root <- outer.DequeueWithDepthFilter(tracker.shortestGlobalDepth)
        if no valid root remains:
            break

        context <- BuildSearchContext(root, tracker)
        current <- empty concurrent queue
        next <- empty concurrent queue

        rootAdjacents <- CanonicalAdjacencyGenerator.GetAdjacentsForBFS(root)
        TryBuildWildcardGuidedAllowedCandidates(rootAdjacents, root, context)
        enqueue allowed root adjacents into current

        repeat:
            parallel foreach item in current:
                ProcessSingleNode(item, root, context, tracker, next)
            swap(current, next)
        until current is empty

        enqueue valid incomplete optima to outer

    tracker.PrintConsolidatedOptimalSolutionsGraph()
    return tracker
```

The implementation entry point for this search is
`HypothesesSearchSpace.Search`. The root-local work is handled by
`ProcessRootNode`, and each frontier item is evaluated by `ProcessSingleNode`.

## Canonical Adjacency

`CanonicalAdjacencyGenerator` enumerates legal one-rule structural extensions in
a fixed total rule order. Each queue item stores the last added rule index. When
expanding that item, the generator may only add rules with strictly larger
indices.

This turns the local BFS from a rule-addition lattice into a canonical spanning
tree. For a grammar with rules ordered as:

```text
r_i1 < r_i2 < ... < r_ik
```

the only canonical path is:

```text
empty
  -> {r_i1}
  -> {r_i1, r_i2}
  -> ...
  -> {r_i1, ..., r_ik}
```

Consequences:

- no inner-BFS visited dictionary is needed
- each reachable structural grammar is generated through one canonical path
- adjacency generation is static and independent of learner instance state
- semantic filtering is applied after the canonical candidate list exists

## Wildcard-Guided Adjacency

Wildcard-guided adjacency is the boolean satisfiability gate before queue
insertion. It takes the whole canonical adjacency list and computes which
candidates can participate in at least one wildcard-matching derivation of the
active next-unparsed sentence.

For each previous POS mapping of the root, the implementation:

1. Builds a root pruning context.
2. Selects that mapping's next-unparsed evidence sentence.
3. Treats currently unproductive delta-dependent nonterminals as nonempty
   wildcard cells.
4. Runs a batched length-indexed dynamic program.
5. Carries two candidate-bitset tables:
   `NoUse` for derivations that do not use a candidate rule, and `Used` for
   derivations that do use a candidate rule.
6. Accepts candidate bit `t` when `START` has a full-mask derivation matching
   the whole target sentence and using candidate `t`.

```text
GuidedBitsetDP(root, delta, candidates, target):
    clear NoUse and Used tables

    for len in 1..target.Length:
        for each span [i, h) of length len:
            seed non-start unproductive symbols over this nonempty span
            seed matching terminal rules

            for each split i < s < h:
                for each existing root/delta binary rule A -> B C:
                    transfer NoUse/Used bits to mask lm OR rm OR ruleMask

                for each candidate binary rule ct:
                    transfer only candidate bit t, writing to Used

            close root, delta, and candidate unit rules to a fixed point

    return Used[START, fullDeltaMask, 0, target.Length]
```

If an exact guided bitset cannot be built for an implementation reason, the code
logs a fallback warning and enqueues the complete canonical list with all
previous mappings active.

## POS Abduction

The GetMaxFit stage is implemented by `MaxFitNextUnparsedSentences`. It bridges
structural rules and evidence by finding POS assignments that allow the current
structural grammar to parse the next sentence not already parsed by the root
mapping. The implementation uses CKY preterminal abduction and returns compact
POS-rule-index options.

```text
MaxFitNextUnparsedSentences(core, prevParsed, prevMappings, root, bans):
    sharedTables <- CKYPOSSharedTables.Build(core)
    rootForbiddenMask <- RootPOSConstraint(root)
    options <- []

    for each active previous mapping i:
        target <- first unparsed evidence sentence for mapping i
        derivationPointers <- CKYPOSAssigner.AssignBitsets(sharedTables, target)

        for each derivation pointer:
            reject if assignment conflicts with fixed root/POS choices
            reject if required RHS coverage is missing
            reject if it contains a banned POS subset

            posRules <- materialized POS rules from derivation pointer
            if new POS rules are found:
                options.add(posRules)

    remove POS-rule supersets
    return options
```

The hot path keeps shared CKY tables, root POS constraints, and strictly-RHS
requirements outside the per-mapping loop where possible. Full POS mappings are
materialized only when later evaluation needs a ban bitset or an accepted
optimal grammar.

## Hypothesis Evaluation

`ProcessSingleNode` evaluates a queued structural grammar in increasing cost
order. The evaluation stages are:

1. **POS Abduction:** `GetMaxFit` discovers minimal new POS assignments for the
   next unparsed evidence sentence.
2. **Grammar Extension:** `GrammarExtensions` builds a full grammar from core
   structural rules, the selected previous POS mapping, and the new POS option.
3. **Shape Computation:** `Grammar.GetGrammarShape` rejects unproductive or
   overgenerating grammars early against the finite-horizon evidence bound.
4. **Evidence Parsing:** `EvidenceTreesShape.ComputeEvidenceVector` computes
   evidence coverage and fit.
5. **Growth Rate Calculation:** `Grammar.CalculateLambda` computes the grammar
   growth-rate value used by Pareto pruning.
6. **Pareto Pruning:** `ParetoManager` rejects candidates dominated by the
   current Pareto snapshot.
7. **Strong Equivalence:** `OptimalSolutionsTracker` rejects candidates whose
   exact canonical colored-graph key matches an already accepted optimum.

## Search Heuristics

The core algorithm is parameterized by `SearchSpaceHeuristicsParams`:

- `ContinueSearchAfterOptimalNode`: controls whether an inner-BFS node that has
  already produced a local optimum is still expanded to adjacents. The default
  is `true`. Setting it to `false` is an aggressive speed heuristic.
- `SkipKnownOptimalStructuralNodesAcrossRoots`: controls whether a structural
  rule set already present in `OptimalSolutionsMap` is skipped when encountered
  again from a different root. The default is `false`.

The console configuration files whose names include `WithHeuristics` enable
these shortcuts explicitly for comparison runs.

## Strong Equivalence And Canonical Keys

Strong equivalence is checked by canonicalizing the colored graph built from the
core rules plus POS assignments. Exact canonicalization uses nauty and is
deferred through signature bucketing.

```text
InsertOptimalSolution(candidate):
    if signature bucket is absent:
        accept candidate without exact canonicalization
        register compact candidate recipe in a new bucket
        return

    candidate.exactKey <- NautyCanonicalKey(candidate.graph)

    for each entry in signature bucket:
        if entry.exactKey is not computed:
            entry.exactKey <- NautyCanonicalKey(entry.graph)

        if entry.exactKey == candidate.exactKey:
            reject candidate as strongly equivalent
            return

    accept candidate and register it in the bucket
```

The tracker stores `CanonicalGraphCandidate` objects with a cheap
`CanonicalGraphSignature`. The exact `CanonicalColoredGraphKey` is computed only
when a signature bucket collision requires it.

## Pareto Pruning

The tracker maintains the current Pareto front over:

- structural grammar size
- generative growth rate `lambda`

A candidate can be pruned when an already accepted global optimum is no larger
and has an equal or lower growth rate, subject to the configured
`ParetoRibbonThickness`.

Inner-BFS workers evaluate hypotheses against lock-free snapshots of the Pareto
front. The tracker version is checked at frontier/root boundaries so later work
uses refreshed pruning state without forcing every worker to lock on every
candidate.

## Banned POS Propagation

When evaluation detects that a POS assignment overgenerates or fails the
evidence check, the learner stores the assignment as a compact bitset in a
`BannedPOSChain`. Descendant hypotheses can skip any POS assignment that is a
superset of a known ban.

Bans are carried in `FrontierQueueItem`, merged into touched
`OptimalSolutionNode` instances, and reused when those optimal nodes later
become outer-queue roots.

## Computational Hot Paths

Let:

- `q` be the size of a canonical adjacency batch
- `B` be the active unparsed target length
- `V` be the number of nonterminals visible to the guide
- `k` be the current delta size
- `w = ceil(q / 64)` be the number of machine words in a candidate bitset

| Operation | Implementation Cost Or Behavior |
| --- | --- |
| Canonical adjacency generation | `O(q)` plus rule-legality checks |
| Wildcard guide table space | `O(V * 2^k * (B + 1)^2 * w)` machine words |
| Wildcard guide time | Word-parallel DP over candidate rule transfers |
| CKY shared table build | Built once per `GetMaxFit` call for the current core grammar |
| CKY POS abduction | Compact bitset iterations over active previous mappings |
| Shape DP | Length-indexed DP with early overgeneration exit |
| Pareto check | Snapshot scan over accepted global optima |
| Strong-equivalence signature | Fixed-round color refinement over the colored graph |
| Exact canonical key | nauty canonicalization only on signature bucket collisions |

The implementation limits expensive evaluations by staging the pipeline:

1. generate the canonical candidate list once
2. remove impossible children via wildcard-guided DP before queue insertion
3. run CKY POS abduction only for guided survivors and active mappings
4. pay shape, evidence, lambda, and Pareto costs only after valid POS abduction
5. defer exact nauty canonicalization until a signature bucket collision occurs
