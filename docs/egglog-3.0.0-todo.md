# egglog 3.0.0: what to evaluate

The submodule `ext/egglog` holds egglog 3.0.0 (tag `v3.0.0`, commit `8879605c`, released 2026-08-18). Before, it was
pinned at `5e655cae` (2026-06-22): egglog 2.0.0 plus unreleased work, 127 commits before 3.0.0.

The engine still follows `5e655cae`. It follows 3.0.0 only once the items below that change behaviour are evaluated.
Until then:

- read the version the engine follows with `git -C ext/egglog show 5e655cae:<path>`;
- the Rust files and lines that the code's comments cite are at `5e655cae`, and so are those that
  [`history/egglog-engine-map.md`](history/egglog-engine-map.md) and the other history documents cite;
- the benchmark comparisons build the Rust at `5e655cae`
  ([benchmark README](../src/Eggnog.Benchmarks/README.md)).

Each item names the upstream pull request (`#n` is `https://github.com/egraphs-good/egglog/pull/n`), its main files
and lines at `v3.0.0`, and the Eggnog code or document it bears on. Upstream paths are relative to `ext/egglog`;
`Relations/` is `src/Eggnog/Relations/`. The upstream summary is the "[3.0.0]" section of `ext/egglog/CHANGELOG.md`.
Items marked *behaviour* change what code the engine ports or the library mirrors does: its results, their order, or
its errors. The rest are API ideas, speed or plumbing. Tick an item when it is settled, and say where the outcome is
recorded (a commit, a row of [`engine/decisions.md`](engine/decisions.md), or a [roadmap](roadmap.md) item). Once the
behaviour items are settled, `CLAUDE.md` and the benchmark README say the engine follows 3.0.0.

## Already at the old pin

Several 3.0.0 changelog entries were already in `5e655cae`, in its "Unreleased" section or merged before it. They have
nothing new to evaluate:

- relations as non-unionable constructors (#770); Eggnog keeps them as unit-valued functions
  ([`architecture.md`](architecture.md), section 2.13, item 4);
- the capability-specific primitive traits (#856; decisions row 12) and the `:naive` rule option;
- typed extension state (#884), the body-defined primitive APIs (#881, #889), f64 `exp`, `log` and `sqrt` and BigRat
  to i64 (#890, #891), `RunReport::can_stop` (#882), full source paths (#886), the nested-container semi-naive fix
  (#888), nullary call printing (#887), and `Read::table_size` (#892, #895);
- the panics made errors by #876 and #908.

## Engine semantics

- [ ] **Thread count per e-graph** (#910, *behaviour*). egglog replaced Rayon's global pool with a pool of its own,
  without work stealing, that each e-graph owns. `EGraph::new(n)` (0 for every processor, 1 for no pool),
  `with_num_threads` and `set_num_threads(&mut self)` set it, and `EGraph::default()` is now serial where it used
  every processor. A table's shard count follows the thread count when the table is created. Upstream:
  `concurrency/src/threadpool/mod.rs`, `egglog-bridge/src/lib.rs:143-274`, `core-relations/src/parallel.rs`. Eggnog:
  the engine's `Relations/Bridge/EGraph.cs` defaults to every processor (`ParallelHeuristics.Default`); the library's
  `EGraphOptions.DegreeOfParallelism` defaults to 1, as upstream now does. Parallel work runs on the .NET pool
  (`Parallel.For`, `Relations/Execution/WorkScope.cs`, `Relations/Concurrency/ParallelismBudget.cs`).
- [ ] **Join-index key order** (#948, *behaviour*). A trie node's index over its subset is now a sorted array, so
  `Prober::for_each` yields keys in ascending value order; at the pin, a subset under 512 rows yielded them in order of
  first appearance. The matches are the same, but they reach the actions in another order, so fresh ids and
  order-dependent merges can come out differently. Upstream: `core-relations/src/free_join/execute.rs:159-246`.
  Eggnog: `Relations/Execution/TrieNode.cs` (`GetCachedIndex`) and `Relations/Indexes/ColumnIndex.cs`
  (`BuildForSubset`) iterate in hash-slot order, which matches neither. Decisions rows 4 and 6 allow numbering and
  match counts to differ from egglog.
- [ ] **Stale rows in constrained scans** (#964, #986). `get_if_unchecked` now checks for stale rows, so a bounded
  scan with constraints never yields a deleted row, and the bridge's scans stop filtering them out themselves. This is
  upstream's fix for defect 1 of the engine map's section 8.1, made the way the port already does it. Upstream:
  `core-relations/src/table/mod.rs:1025-1032`, `egglog-bridge/src/lib.rs:526-574`. Eggnog:
  `Relations/Tables/SortedWritesTable.Scan.cs` and `Relations/Tables/LiveConstraintFilter.cs`; the comments in
  `Relations/Execution/JoinRun.Fused.cs`, `Relations/Tables/Constraint.cs` and `Relations/Tables/SortedWritesTable.cs`
  still describe it as egglog's defect. `tests/repro-stale-rows.egg` is upstream's regression program for it.
- [ ] **Multi-column index rebuild** (#914). `rebuild_full` now radix-sorts one block per column and merges the blocks,
  dropping duplicate pairs, and `add_row` and `merge_parallel` list a row once when a value repeats across its
  columns. This is upstream's fix for defect 8, with the same result as the port's. Upstream:
  `core-relations/src/hash_index/mod.rs:251-310`, `368-620`. Eggnog: `Relations/Indexes/ColumnIndex.Build.cs`,
  `Relations/Indexes/PairRadixSort.cs`.
- [ ] **An external context for rule runs** (#986). `run_rule_set`, `with_execution_state` and the bridge's
  `run_rules` take an optional `&dyn Any` that external functions read from their `ExecutionState`; the frontend
  passes its type information through it. What a run does is unchanged. Upstream:
  `core-relations/src/action/mod.rs:330-483`, `egglog-bridge/src/lib.rs:663-690`. Eggnog has no counterpart
  (decisions row 12): `Relations/Execution/Database.Execution.cs` (`RunRuleSet`), `Relations/Database/Database.cs`
  (`WithExecutionState`).
- [ ] **Smaller engine API changes** (#901, #927, #910). `with_execution_state_tracked` also says whether the closure
  staged a write (`core-relations/src/free_join/mod.rs:464-474`). `ExecutionState::for_each_matching_col` and
  `TableAction::for_each_output_value` read rows by column value (`core-relations/src/action/mod.rs:508-562`).
  `FunctionRow` is renamed `ScanEntry`, and `TableAction` gains `kind`, `input_arity` and row iteration
  (`egglog-bridge/src/lib.rs:500-575`, `1374-1513`). A `ValueRebuilder` trait splits off `Rebuilder`
  (`core-relations/src/table_spec.rs:106-157`). numeric-id drops its Rayon iterators. Eggnog:
  `Relations/Bridge/FunctionRow.cs`, `Relations/Bridge/TableAction.cs`, `Relations/Collections/DenseIdMap.cs`.

3.0.0 leaves the other defects of the engine map's section 8.1 in place; only their line numbers moved. Rows 1 and 8
are fixed as above, and #885 does not fix row 9.

## Extraction

- [ ] **Container term forms** (#927, *behaviour*). Extracted `set-of` children are sorted by term structure and
  deduplicated, `multiset-of` children are sorted, and maps extract as one flat `(map-of k0 v0 ...)` instead of nested
  `map-insert`. `TermDag::ast_cmp` gives the structural order. Upstream: `src/termdag.rs:26-62`, `165-222`;
  `src/sort/set.rs:43-49`, `247-258`; `src/sort/map.rs:76-79`, `243-279`. Eggnog:
  `Relations/Extraction/ContainerSort.cs` (its comments name nested `map-insert`) and
  `src/Eggnog.Tests/Relations/Extraction/TestMapSort.cs`; the library renders a `Set` in its element order
  (`ElementOrder`, used by `src/Eggnog/Rendering/SExpression.cs`).
- [ ] **Escaped string literals** (#920, *behaviour*). Printed strings escape `"` and `\`, so they parse back.
  Upstream: `egglog-ast/src/generic_ast_helpers.rs:758-772`. Eggnog: `Relations/Extraction/LiteralText.cs` prints
  them unescaped, as the pin did, and `src/Eggnog/Rendering/SExpression.cs` uses it.
- [ ] **Extractor errors** (#920). A root variant with no finite cost is skipped, and a value with no valid
  extraction returns `Error::ExtractError` instead of panicking. The port already does both
  (`Relations/Extraction/Extractor.Variants.cs`, `Relations/Extraction/Extractor.Functions.cs`); their comments cite
  the pin's lines. Upstream: `src/extract.rs:631-645`, `783-800`.
- [ ] **No proof-only extraction option** (#941). `compute_costs_from_rootsorts_allow_unextractable` is gone, and
  proofs have an extractor of their own. Eggnog keeps `AllowUnextractable` in
  `Relations/Extraction/ExtractionOptions.cs`, which only tests use. Upstream: `src/extract.rs:144-170`.
- [ ] **The cost hook takes an `Enode`** (#901). `CostModel::enode_cost` gets the node's children, class and
  subsumption instead of the bridge's row. Upstream: `src/extract.rs:280-300`, `src/exec_state.rs:527`. Eggnog:
  `Relations/Extraction/NodeCostFunction.cs` takes the function and the row, which carry the same.

## Scheduling

Eggnog follows egglog at 5e655cae in its schedulers (`Schedule.RunWith`, [`architecture.md`](architecture.md), section
2.6), except that the matches it offers hold canonical classes (defect 9) and each scope holds its own state
(defect 10).

- [ ] **Scheduler queries skip subsumed rows** (#885). A custom scheduler's query no longer offers subsumed rows as
  fresh matches; at 5e655cae it does, and so does Eggnog's (`RuleProgram.IncludeSubsumed`). Upstream:
  `src/scheduler.rs:398-400`. The scheduler's non-canonical residual ids (defect 9) stay; Eggnog canonicalizes them.
- [ ] **Variable-free scheduled rules** (#920). A scheduled rule whose head has no free variables now fires; before,
  its match size divided by zero. Upstream: `src/scheduler.rs:40-161`, `402-411`. Eggnog refuses such a rule before a
  run (`ScheduledRule.NoFreeVariables`).
- [ ] **Scheduler state after an error** (#920). `step_rules_with_scheduler` restores its rulesets and schedulers on
  every error path, and an unknown ruleset is an error. Upstream: `src/scheduler.rs:196-315`. At 5e655cae a failed
  step loses every ruleset and scheduler (defect 28 of the engine map's list); Eggnog keeps each scope's scheduler
  state in the scope, so a step whose rules fail leaves the scope usable with its state as the step left it, the
  matches a failed query found included, as 3.0.0's restore does, and a scheduler that throws faults the scope. The
  unknown ruleset is still to evaluate.

## Proofs

- [ ] **The proofs trigger.** Decisions row 17 and the roadmap's parked "Proofs" item wait until upstream's proof API
  settles and ships in a release. 3.0.0 ships it, with containers in the term encoding (#927), root-directed proof
  extraction (#941), faster checks of direct rule proofs (#940), proofs of eq-sort primitive facts (#930) and fewer
  merge rows (#928); upstream `main` keeps changing it after 3.0.0. Upstream: `src/proofs/proof_encoding.md`,
  `src/proofs/proof_container_rebuild.rs`, `src/proofs/proof_extractor.rs`, `src/proofs/proof_checker.rs`.
- [ ] **APIs that refuse proofs** (#901). `update`, `query` and the rule callbacks return
  `Error::ProofsIncompatibleApi` when proofs are on. Upstream: `src/lib.rs:2943-2950`.

## Errors and diagnostics

- [ ] **Duplicate rule names** (#920, *behaviour*). A second rule of the same name in a ruleset returns
  `Error::RuleAlreadyExists` instead of panicking. Eggnog's `Ruleset.Add` (`src/Eggnog/Rules/Ruleset.cs`) does not
  check, and a rule's name serves rendering, statistics and messages. Upstream: `src/lib.rs:1185-1192`.
- [x] **Partial primitives return no result** (#920, *behaviour*). `vec-set` and `vec-remove` out of range,
  `multiset-pick` on an empty multiset, BigInt shifts, a zero BigRat denominator and i64 `log2` now give no result
  instead of panicking. Upstream: `src/sort/vec.rs:196-197`, `src/sort/multiset.rs:190`, `src/sort/bigint.rs:26-27`,
  `src/sort/bigrat.rs:35`, `src/sort/i64.rs:42`. Settled in [decisions](engine/decisions.md) row 22: the library's
  `log2`, `BigInt` shifts and `bigrat` follow 3.0.0 (`Primitives.Log2`, `BigIntegerPrimitives`,
  `BigRationalPrimitives.FromBigIntegers`), and so do `vec-set` and `vec-remove` (`Primitives.VecSet`, `VecRemove`)
  and `multiset-pick` (`Primitives.MultiSetPick`), and `unstable-multiset-fill-index` and `-clear-index` of a
  primitive's function value (`src/sort/multiset.rs:518-520`, `583-585`; `Primitives.UnstableMultiSetFillIndex`,
  `UnstableMultiSetClearIndex`). 3.0.0 also gives `multiset-sum-multisets` no result where a count
  overflows (`src/sort/multiset.rs:767`, `checked_mul`), where the pin multiplies; the library's counts are `long`s, and its `MultiSetSumMultiSets` throws
  `OverflowException` there, not followed yet.
- [ ] **Panics made errors** (#920). New error variants include `RuleAlreadyExists`, `DesugarError`,
  `UnsupportedInputType`, `ExtractError` for a negative count or a deleted or subsumed value, `BadPresortArguments`,
  `AmbiguousPrimitive` and `UnresolvedPrimitive`. Most are frontend errors. Upstream: `src/lib.rs:2887-2950`,
  `src/typechecking.rs:1211-1255`. Eggnog's errors: [`architecture.md`](architecture.md), sections 2.5 and 2.12.

## Performance

- [ ] **Sorted subset indexes** (#948). A trie node indexes its subset with a sorted array and a binary-search probe
  instead of a hash index; upstream measured about 33% on `gemma`. Upstream:
  `core-relations/src/free_join/execute.rs:159-246`, `921-935`. Eggnog: `Relations/Indexes/ColumnIndex.cs`
  (`BuildForSubset`, sorting only from 512 rows) and `Relations/Execution/TrieNode.cs`.
- [ ] **Trie roots shared across plans** (#949). Plans of one rule-set run that start from the same table and
  constraints share a trie root, and child caches key on the edge constraints too; upstream measured 8-15% on
  `whisper`, `gemma` and `qwen3_moe`. Upstream: `core-relations/src/free_join/execute.rs:446-470`, `773-885`,
  `948-982`, `1067-1124`. Eggnog: `Relations/Execution/JoinRun.cs` builds roots per plan, and
  `Relations/Execution/TrieNode.cs` keys children by slot and value.
- [ ] **Rows patched in place in canonicalization** (#955). A changed row is edited in the output buffer, not built
  in scratch first, and the radix sort returns early on sorted input. Upstream: `core-relations/src/uf/mod.rs:79-165`,
  `core-relations/src/row_buffer/mod.rs:457-478`, `core-relations/src/hash_index/mod.rs:438-450`. Eggnog:
  `Relations/Tables/CanonicalizeLoops.cs`, `Relations/Rows/TaggedRowBuffer.cs`, `Relations/Indexes/PairRadixSort.cs`.
- [ ] **Refining without a copy** (#952). `refine_subset` copies a subset only when nothing filters it, and a sorted
  table refines in one pass. Upstream: `core-relations/src/free_join/execute.rs:1399-1415`,
  `core-relations/src/table/mod.rs:521-539`. Eggnog: `Relations/Execution/JoinRun.Index.cs` (`RefineVisible` copies
  first).
- [ ] **Resetting only touched indexes** (#957). `merge_all` resets the cached indexes of the tables it changed, not
  all of them, and keeps the size estimate current per merge. Defect 11 still stands upstream. Upstream:
  `core-relations/src/free_join/mod.rs:536-662`. Eggnog: `Relations/Database/Database.Merge.cs`
  (`ResetIndexesAndEstimateSize`).
- [ ] **Parallel cutoffs** (#910). New defaults, each overridable by an environment variable: index builds, rebuilds
  and table operations go parallel above 400,000 rows (20,000, 10,000 and 20,000 at the pin), a container above
  10,000 (1,000), containers above 8 (1), and a parallel action batch holds 8,192 rows (128). Upstream:
  `core-relations/src/parallel_heuristics.rs:8-15`. Eggnog: `Relations/Tables/ParallelHeuristics.cs` keeps the pin's
  values, and `Relations/Actions/Mask.cs` caps a batch at 128 lanes.
- [ ] **Which egglog the parity runs compare with.** The roadmap's "Parity with the Rust" item and the benchmark
  campaign time the Rust at `5e655cae`. At 3.0.0 the bridge examples are serial unless edited to take a thread count,
  and the Rust includes the speed-ups above, which the port does not have. The workloads upstream measured on
  (`tests/gemma.egg`, `whisper.egg`, `qwen3_moe.egg` and the other transformer programs) need a `.egg` reader
  (roadmap, "Closed").

## Public API ideas

- [ ] **A name-indexed data API** (#901). `EGraph::read` and `update` (one flush after the closure, whose reads do not
  see its writes), `query` (one map of variables per match), `function_entries` and `constructor_enodes`; in
  primitives, `Read::lookup`, `eclass_of` and `contains`, and `Write::set`, `add`, `remove`, `subsume` and `union`
  returning `Result`. Upstream: `src/exec_state.rs:351-630`, `src/lib.rs:1264-1309`, `2392-2489`. Eggnog's typed
  counterparts: `Scope.TryGet`, `CountRows` and `Rows` (`src/Eggnog/Graph/Scope.Reads.cs`), `Scope.Write` and
  `ScopeWriter`, and `EGraph.DefineQuery` ([`architecture.md`](architecture.md), section 2.7).
- [ ] **Row conversions and `ApiError`** (#901, #986). `IntoValue` and `FromValue` convert rows, for tuples up to
  arity 8, and a wrong name, arity or table kind returns `Error::ApiError`; column sorts are not checked. Upstream:
  `src/api/mod.rs:38-182`. Eggnog checks the arity and each column's type and throws `ArgumentException`
  (`TableHandle.CheckArguments` and `ToArguments`, `src/Eggnog/Tables/TableHandle.cs`).
- [ ] **Signatures for primitives** (#986). A function's signature is stored once (`Function::func_type`, `FuncType`),
  and primitives can read it (`Read::constructor_schema`, `function_schema`, `table_subtype`), walk a class's nodes
  through an index (`Read::enodes_for_eclass`) and remap a container (`Core::map_container`). Upstream:
  `src/lib.rs:328-345`, `src/exec_state.rs:207-218`, `394-487`. Eggnog: `TableHandle.Kind`, `Keys` and `Output`, and
  `EGraph.Tables`; it has no public walk of a class's nodes (the nearest is `Scope.Rows`).
- [ ] **Changing the thread count** (#910). `set_num_threads` changes a live e-graph's thread count. Eggnog fixes it
  when a graph or scope is created (`EGraphOptions.DegreeOfParallelism`, `src/Eggnog/Graph/ScopeOptions.cs`).
- [ ] **Reads in rule heads** (#912). The `:unsafe-seminaive` option keeps delta matching but lets a rule's head read
  the database, like `:naive`; it can depend on evaluation order, the term encoding rejects it, and it excludes
  `:naive`. Function lookups in rule heads, refused for every rule at the pin, are now allowed in `:naive` and
  `:unsafe-seminaive` rules. Upstream: `egglog-ast/src/generic_ast.rs:83-121`, `src/typechecking.rs:889-950`. Eggnog:
  rule heads never read tables but in a full callback (`ThenCallFull`, egglog's `rust_rule_full`, at the pin already)
  ([`architecture.md`](architecture.md), section 2.13, item 2), and `RuleBuilder.Naive()`
  (`src/Eggnog/Rules/RuleBuilder.cs`).

## Nothing to evaluate

Tooling and the text front end, which Eggnog does not have: release-mode tests with debug assertions (#917), nightly
benchmark reports (#935, #936, #939, #942), a build without default features (#961), `print_function`'s new signature,
CLI exit codes (#920), lazy rule names (#991), one typechecker pass fewer (#992), `check` stopping at its first match
(#990), the crate docs (`src/lib.md`) and `release-instructions.md`. Sort names with `-` in serialization (#920) are
followed already: `Scope.Serialize` ports 3.0.0's class ids ([decisions](engine/decisions.md), row 23).

## After 3.0.0

Upstream `main` (`0df778b0`, 2026-10-16) has more that is not released and not in the submodule. It waits for the next
release:

- extraction split into `TreeCostModel` and `DagCostModel` traits, with `MonoidCost`, `TreeCostModelFromDag`,
  `DEFAULT_COST_MODEL`, `TreeExtractor` and named result structs;
- a scheduler runner that applies each rule's matches before the next rule's `filter_matches`, with the bridge's
  `run_rules_no_rebuild`, `flush_updates_no_rebuild` and `rebuild_now`;
- rule-action errors that keep the effects already applied, without rollback (compare defect 22 and
  [`architecture.md`](architecture.md), section 2.12);
- a `SchedulerContext` for schedulers, and `EGraph::total_size` and `num_nodes`;
- subsuming a constructor row created earlier in the same rule, which goes on to run the rule's later actions
  (compare defect 19);
- packed join indexes shared across all plans of a run, and 128-bit stage masks for plans of 65 to 128 stages.

One more overlaps Eggnog's own open work. It is to be evaluated once a release contains it:

- [ ] **Processed headers reused across cached plans** (#1052, `238fadce`). A rule-set build caches each table's
  processed header constraints, keyed by the constraint list, so identical headers are processed once, and an empty
  result is cached as none. Upstream at `238fadce`: `core-relations/src/query.rs:97-119`, `156-181`
  (`reprocess_constraints`); related are `b92e449c` (index preparation cached with shared join plans) and `b9496100`
  (packed join root caches). It changes the same step as the idle-rule allocation commit ("Drop idle rule variants
  without allocating"; roadmap, "Idle local rules"): header instantiation in `RuleSetBuilder.AddRuleFromCachedPlan` and
  `PlanTemplate.InstantiateLayered` (`Relations/Query/RuleSetBuilder.cs`, `Relations/Planning/PlanTemplate.cs`).
  Upstream skips repeated processing. The allocation commit evaluates headers into scratch: for a single plan it
  allocates nothing for an empty header, as egglog does, and it departs from egglog only for a decomposed plan and for a
  later empty header ([decisions](engine/decisions.md), row 19). Compare #1052 with that commit, and port it if it gains
  as much with more fidelity. The cache commit, on the branch `idle-rule-cache`, keeps iteration rule lists on a scope
  and is decided separately (roadmap, "Idle local rules").
