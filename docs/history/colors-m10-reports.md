# Colored contexts M10.4: the L6 and L7 reports, and the child footprint

This report answers the done-when of colored-contexts spec §17 M10.4 ("the L6 and L7 reports are produced") and the
M9.4 item that M10.4 took over (§21.5: reduce a bare child and an empty layer toward §14.2's estimate). Where the
M10.4 code departs from the spec text is recorded in colored-contexts spec §21.10 and rule-layer spec §15.7, and the
spec text (§11, §14, §15, §16) now says what was built.

## How the figures were taken

Every figure below is a **count** or an **allocation**: rows copied, unions, parent-id losers, id-array lengths, rule
matches and allocated bytes. None of them depends on how busy the machine is, so none needs an idle machine. No time is
reported; the machine was shared with other builds and test runs while these were taken.

Two sources:

- **Dry runs** (`--job dry`) of the benchmark classes in `src/Eggnog.Benchmarks/Relations/Contexts/`: one cold invocation
  per case. Their *Allocated* column includes one-time allocations of the first invocation (JIT, static initializers,
  pools filling), so small figures (a few KB) are noisy.
- **The footprint mode** of the benchmark app, `Eggnog.Benchmarks.exe --context-footprint`
  (`Contexts/ContextFootprint.cs`): the same workloads, measured with `GC.GetAllocatedBytesForCurrentThread` after a
  warm-up and averaged over many repetitions. This is the steady state the memory diagnoser converges to, without the
  cold-start noise. The tables quote it unless they say otherwise.

Both were run on the Release build of the M10.4 sandbox, net10.0, server GC.

## L6: what a child costs, and memory against full clones

### Per-child cost as the root grows (B4, `PerChildCostBenchmarks`)

`ChildUnionsAndRebuild` is the M9.4 storage workload: a child makes 100 unions, rebuilds and is disposed.
`ChildUnionsRebuildAndRuleRun` is the full M10.4 workload on the bridge: the same 100 unions staged and flushed (the
flush rebuilds), then one run of `edge(x, y) ⇒ path(x, y)`, whose watermark the child inherits from the root's run
(§10.2), so it matches only the child's 200 rewritten `edge` rows; the setup checks that count at every size.

Allocated bytes per child (dry runs):

| Root rows | Frozen root | 100 unions + rebuild | 100 unions + flush + one rule run |
|---:|:---:|---:|---:|
| 20,000 | yes | 96.3 KB | 145.0 KB |
| 20,000 | no | 95.9 KB | 144.8 KB |
| 200,000 | yes | 96.0 KB | 144.7 KB |
| 200,000 | no | 96.1 KB | 144.8 KB |
| 2,000,000 | yes | 96.1 KB | 144.7 KB |
| 2,000,000 | no | 96.1 KB | 144.8 KB |

Bytes are flat to within 0.3% over a hundredfold growth of the root, for frozen and writable roots, with and without the
rule run (the 1.5× bound of the M9.4 done-when holds by a wide margin). The steady state of the full workload is about
148–149 KB at 20,000 and 200,000 rows. The examination, copy and match counters are identical at every size (the setups
check them: 300 rows examined and copied, 200 matches). M9.4's dry runs measured 98–101 KB for the storage workload; the
lighter children below account for the small drop.

**Needs an idle machine:** the time half of B4 (per-child time flat within 1.5×). The dry-run times above are single
cold invocations on a shared machine and are not evidence either way.

### Case splits against clones (B5, `CaseSplitMemoryBenchmarks`)

A split of *n* cases under a scope, each case making one union and asserting one fact, against egglog's push baseline,
one `Database.Clone()` of the root per case with the same writes merged into it. The context side opens each split under
a fresh writable child of a frozen root; the scope's own cost (about 2.7 KB) is subtracted below.

Bytes per case (steady state):

| Root rows | Cases | Contexts | Contexts, lifted | Clone per case | Clone ÷ contexts |
|---:|---:|---:|---:|---:|---:|
| 20,000 | 2 | 13.4 KB | 19.8 KB | 1.21 MB | 90× |
| 20,000 | 8 | 13.7 KB | 17.3 KB | 1.21 MB | 88× |
| 200,000 | 2 | 13.4 KB | 19.8 KB | 9.49 MB | 708× |
| 200,000 | 8 | 13.7 KB | 17.3 KB | 9.49 MB | 693× |

The dry runs agree (whole split, scope included): 33 KB (2 cases) and 114 KB (8 cases) for contexts at both root sizes,
against 2.36 MB / 9.43 MB (20,000 rows) and 18.5 MB / 74.2 MB (200,000 rows) for clones. A case costs the same whatever
the root's size; a clone costs about 45–55 bytes per root row (22,000 and 220,000 rows here). The lift adds 3.5–6.5 KB per case (member maps, the
candidate arena, the parent's flush), less per case as the split grows.

### One case, structure by structure (B5)

B5 asks for the bytes of each per-child structure. The footprint mode prints the parts of a bare child (the table above
and §14.2) and, for one case under a fresh writable scope over a frozen root of 20,000 rows, the bytes each step of its
work adds (steady state, the scope's own cost subtracted):

| Step | Structures it creates | Bytes |
|---|---|---:|
| The case | `CaseSet`, the child context, its database, catalogs and union-find overlay | 2,975 |
| One union and its rebuild | Overlay entries and journal, the union-find layer, the catch-up copies of the loser's rows (layers of every table they belong to), hidden sets, ancestor cursors | 9,848 |
| One asserted fact | The fact table's own layer | 3,317 |
| One rule run | Rule progress and watermarks, the run's indexes over the case's layers, child caches | 12,491 |
| A lift of the case into the scope | The member map, the `Lifter` and its arenas, the lift writes, and the scope's flush of the lifted union and facts | 24,537 |
| All of the above | | 53,168 |

### Copy volume per union (G3, `CopyVolumeBenchmarks`)

Each of 1,000 random unions runs in a child of its own over a frozen root where every id has one `f` row and one
`r(x, h(x))` row, the hubs `h` drawn with a heavy tail over a random permutation of the ids. The histogram counts the
ancestor rows each union made the child copy (losers only, decision 3):

| Root rows | 2–3 | 4–7 | 8–15 | 16–31 | 32–63 | Mean | Max |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 20,000 | 828 | 158 | 10 | 4 | 0 | 2.88 | 26 |
| 200,000 | 829 | 151 | 16 | 3 | 1 | 2.90 | 32 |

A union copies the rows naming its loser and nothing for its winner, so the volume follows the loser's degree, not the
root's size. With smaller-id-wins, a hub loses only to a smaller id, which random unions rarely pair it with; the tail
stays short. Workloads that union hubs into small ids systematically would see the tail; the histogram is the tool to
check one.

## L7: the cost of smaller-id-wins after a late parent mint (G2, `LateParentMintBenchmarks`)

The parent is a writable root of 10,000 ids. Each case builds `num(0) … num(T−1)` and the chain
`add(num(i), num(i+1))` from local ids and unions `num(T−1)` and the last `add` with `num(0)`; the lift then mints the
same terms in the root, one level per round. Every lifted term's root id is larger than the cases' local ids for it (the
cases reserved their blocks first), so it loses in every case.

| Cases | Terms *T* | Rounds | Terms lifted | Parent-id losers | Rows copied per round | Root ids minted | Ids reserved by cases | Root id array |
|---:|---:|---:|---:|---:|---|---:|---:|---:|
| 2 | 16 | 2 | 31 | 60 | 32, 32 | 10,031 | 96 | 0 → 10,114 |
| 8 | 16 | 2 | 31 | 240 | 128, 128 | 10,031 | 384 | 0 → 10,402 |
| 2 | 256 | 2 | 511 | 1,020 | 512, 512 | 10,511 | 2,592 | 0 → 12,850 |
| 8 | 256 | 2 | 511 | 4,080 | 2,048, 2,048 | 10,511 | 10,368 | 0 → 20,626 |

Read-out:

- **Parent-id losers** are `cases × (lifted terms − 1)`: every lifted term loses in every case, except the one whose class
  the case had already merged with another lifted id. Each loss is one own union in the case.
- **Copies** are one row per lifted term per case per round: the root's new row naming the loser is copied into the case,
  canonical, and hidden there. They scale with `cases × terms`, never with the root.
- **Id-array growth.** The root's union-find array grows to the largest id the root unions (§4.2): here a lifted term,
  minted above every block the cases reserved. With 8 cases of 256 terms the cases reserved 10,368 ids (first blocks of 16,
  doubling to 4,096), so the array reaches 20,626 entries for 10,511 root ids: about 2×, 8 bytes per id, 80 KB. With
  `n` single-mint cases the reservation is `16 n` ids (§4.2's 10⁵ cases → 1.6 M ids, 13 MB).
- **For G2 (§19 item 7):** ranking parent-visible ids first would remove the parent-id losers and these copies, at the
  price of rewriting the cases' own rows that name the local id (the same order of rows, but inside the case's own
  layer rather than copied from the parent). The measured cost of the current rule is linear in `cases × terms` per round
  and independent of the root, so the data gives no urgency to the change; the decision stays with the decisions owner.

**Needs an idle machine:** none of the L7 figures (all counters). The dry-run times in the benchmark output are not
evidence.

## The child footprint (§21.5: reduce a bare child and an empty layer)

M9.4 measured a bare child at 7.4–9 KB and an empty layer at 2.7–3.0 KB in dry runs. In steady state, before M10.4, a
bare child allocated 4,726 B and an empty layer 2,766 B; the difference to the dry-run figures is cold-start noise. Most
of both was `ConcurrentQueue` segments: a `PendingState` with one shard allocates two queues of 32 slots each, 1,792 B in
all, and both a child's union-find table and every layer have one.

| Steady state, bytes | Before M10.4 | After |
|---|---:|---:|
| Bare child (`ManyChildrenBenchmarks.ChildOnly`) | 4,726 | 2,454 |
| Empty layer (`ChildWithOneEmptyLayer` − `ChildOnly`) | 2,766 | 1,062 |
| Bridge child (`EGraphContext.CreateChild` + `Dispose`) | — | 2,693 |
| Child with one union and a rebuild (3 rows copied) | — | 12,173 |

| Part | Root (unchanged) | Child (after) |
|---|---:|---:|
| `PendingState`, one shard | 1,792 | 128 until first staged write |
| `NotificationList` (a database's catalog) | 664 | 96 until first notify |
| `IndexCatalog` | 48 (was 72) | 48 |

The empty layer is now within §14.2's 1–2 KB estimate, and a bare child is the O(tables + depth) small objects §14.2
describes. Dry runs (one cold invocation each) report 5.3 KB and 6.37 KB, against 4.57 KB and 10.58 KB before; the first
invocation after the change also pays the one-time static empty queues and the first lazily created segments, which is
why the bare child's cold figure did not drop.

What changed:

1. **Lazy pending queues** (`Tables/PendingState.cs`). A child's layer table (`SortedWritesTable.CreateLayer`) and its
   union-find table (`OverlayDisplacedTable`) create each queue on its first publication; until then the slot holds a
   shared, never-written empty queue, which every reader (`IsEmpty`, `TryDequeue`, enumeration, `Mark`, `DiscardSince`,
   `DeepCopy`) treats as empty. Creation is a compare-and-swap, so racing publishers agree on one queue.
2. **Lazy notification flags** (`Concurrency/NotificationList.cs`, `Tables/TableCatalog.cs`). A child database's catalog
   starts with an empty flag directory; the first notify takes the existing growth branch, which now creates segment 0.
3. **Lazy index-catalog gates** (`Indexes/IndexCatalog.cs`). The insert lock is created by the first insert.

**The root pays exactly what it did.** Root and bare tables keep eager queues and eager flags:

- `PendingState.EnqueueRows` and `EnqueueRemovals` test one volatile `int` (`flags != 0`) where they tested one volatile
  `bool` (`isSealed`): the same load, compare and not-taken branch. An eager state that is not sealed has no flag set; the
  flagged path (`PrepareRows`/`PrepareRemovals`, `NoInlining`) handles both the seal, as before, and a lazy queue.
- `NotificationList.Notify` is unchanged: an empty directory takes the branch a notify past the last segment already took.
- `IndexCatalog.GetOrInsert`, `TryFind` and `GetOrRefresh` are unchanged; only the `NoInlining` insert path takes the lazy
  lock. A root catalog allocates 24 B less until its first index and the same afterwards.
- Allocation probes: `ChildFootprintTests` bounds the child figures; the existing root probes
  (`ExecutorAllocationProbeTests`, `RootCostProbeTests`, `RootParallelAllocationTests`, `RootSizeProbeTests`,
  `LayeredAllocationProbeTests`) pass unchanged.

**Needs an idle machine:** the ≤2% root gates (§17's M10.4 row, §21.11) for the classes that publish staged writes
(`WritesBenchmarks`, `RuleSetRunBenchmarks`), to confirm on timings what the code reading above shows.

## M10.4 notes

The departures of the M10.4 code from the spec text, first listed here for the spec owner, are folded into
colored-contexts spec §21.10 (with the fixes made after review) and rule-layer spec §15.7, and the spec text now
describes what was built.
