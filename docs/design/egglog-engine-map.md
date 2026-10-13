# egglog engine architecture map

> **File:** `docs/design/egglog-engine-map.md`.
> Written 2026-10-10 for the Eggnog team.
>
> **Sources:** eight subsystem reports (tables, indexes and offsets, planner, executor, query and actions, memory and
> values, union-find/ids/concurrency, bridge) and six gap investigations. Several gap investigations ran experiments on
> instrumented copies of egglog and on its test corpus. Where a gap answer contradicts a report, the gap answer is used.
>
> **egglog version:** the submodule at `D:\eggnog\ext\egglog`, commit `5e655cae` (2026-06-22).
>
> **Reference abbreviations** (line numbers are at that commit):
> - `cr/` = `core-relations/src/`
> - `eb/` = `egglog-bridge/src/`
> - `fe/` = egglog's own `src/` (the frontend)
> - `ufc/` = `union-find/src/`
> - `nid/` = `numeric-id/src/`
> - `conc/` = `concurrency/src/`
>
> **Spike:** `D:\eggnog\src\Eggnog.Storage` and its tests, `D:\eggnog\src\Eggnog.Storage.Tests`.

## Contents

0. Conclusions in brief
1. Overview: layers, one rule end to end, how timestamps thread through
2. Subsystems
3. egglog-bridge, and what the frontend assumes
4. Colored contexts
5. .NET port mapping
6. Build order
7. The native alternative
8. Risks and open questions

---

## 0. Conclusions in brief

1. **What the engine is.**
   - Every egglog function is one `SortedWritesTable`. It holds an append-only, row-major buffer of `u32` cells. Each row
     carries a timestamp column. A deleted row is marked by a tombstone in column 0. A sharded hash table indexes the key,
     and a run-length index over the timestamp column turns "rows newer than T" into a binary search.
   - The union-find is also a table, `DisplacedTable`. It has one row per id that stopped being canonical.
   - Each rule is planned once into a Free Join or Generic Join plan and cached. Every iteration re-instantiates the
     cached plan as N semi-naive variants. Their timestamp constraints become dense row ranges.
   - The executor is a lazily indexed trie join. Each stage buffers up to 256 candidate "frames" before recursing.
     Complete matches go into 128-lane columnar batches, and a vectorized interpreter runs the rule's right-hand side over
     each batch.
   - Every write is staged and stays invisible until `merge_all`. Merge functions resolve functional-dependency
     conflicts there. Congruence is restored by re-inserting canonicalized rows with a fresh timestamp.
2. **Two kinds of incrementality coexist.**
   - *Timestamps* drive semi-naive rule evaluation.
   - *`TableVersion {generation, physical row count}`* drives index refresh, incremental rebuild, and anything that must
     catch up.
   - A colored design needs both.
3. **egglog has defects to fix, not copy** (§8.1).
   - Since commit `6e75cefe`, a scan with constraints does not check tombstones. Plain egglog programs can reach this.
   - The parallel insert discards merge outputs and reports changes that did not happen.
   - Decomposed plans can lose a join constraint.
   - The scheduler stores non-canonical ids.
   - There are about a dozen smaller defects.
   - Eggnog implements the serial semantics everywhere and checks visibility on every path that yields rows.
4. **Colored contexts** (§4) are recommended to work as follows.
   - Ids and a timestamp clock are global to the store.
   - Union-by-min is used at every level.
   - A child's union-find is a dictionary overlay over parent-canonical ids.
   - A view is a stack of per-layer row stores addressed by layer-local row ids. Each (context, table, ancestor layer) has
     a *hidden set*, which generalizes egglog's tombstone filtering.
   - An ancestor's indexes are built when it is frozen and are then shared read-only. Each child builds its own indexes
     for its own rows.
   - Semi-naive watermarks are kept per (rule, context) and seeded from the parent. Rows copied into a child are
     re-stamped.
   - A child catches up through offset cursors into its ancestors' row buffers and union-find logs. Compaction waits while
     any descendant is behind.
   - Deletes are not allowed in a context that has live descendants. Lattice merges are required wherever children write.
5. **The port** (§5) avoids trait objects.
   - It uses sealed classes and generics constrained to struct type parameters.
   - Every hot API also accepts arrays plus offsets, so code compiled from expression trees can call it.
   - The executor is interpreted first. Compiled RHS programs and per-stage kernels come later. The interpreter keeps the
     recursion and the dynamic stage reordering.
6. **The native alternative** (§7) is weighed but not chosen.
   - egglog's only form of layering is a deep clone, which costs O(database) per context. For a 200k-row root that is
     estimated at tens of MB and about 10–30 ms per context (not measured). Ids collide across clones, and a clone cannot
     catch up with a changing parent.
   - Adding colors in a Rust fork would rewrite most of core-relations.
   - Recommendation: port to C# and keep egglog as a differential test oracle. The measurements in §7.4 would settle any
     remaining doubt.

---

## 1. Overview

### 1.1 Layers

```
egglog frontend (fe/)          parse, typecheck, desugar to core IR, schedules, scheduler, extraction, push/pop
  │  declare_function, BackendRule, run_schedule → step_rules
egglog-bridge (eb/)            function tables [keys…, ret, ts, subsume?]; merge functions; the "$uf" table;
  │                             id and timestamp counters; rule recording; cached plans; semi-naive variants;
  │                             iteration and rebuild loops; panic side channel; primitive capability wrappers
core-relations (cr/)
  ├ query.rs                    RuleSetBuilder / QueryBuilder / RuleBuilder, cached plans
  ├ free_join/plan.rs           planner: Generic Join, Free Join (PureSize, MinCover), tree decomposition
  ├ free_join/execute.rs        executor: lazy trie, frame batching, stage kernels, action batching, parallelism
  ├ free_join/mod.rs            Database: catalog, constraint processing, merge_all, apply_rebuild, counters
  ├ action/                     RHS IR, vectorized interpreter, ExecutionState, write staging
  ├ table_spec.rs, table/       Table trait, WrappedTable, SortedWritesTable, rebuild
  ├ uf/                         DisplacedTable (union-find as a table), Canonicalizer
  ├ hash_index/, offsets/       ColumnIndex, TupleIndex, Subset algebra
  └ row_buffer/, pool/, common.rs, base_values/, containers/
union-find (ufc/), numeric-id (nid/), concurrency (conc/)   support crates
```

Sizes: core-relations is 18.0k lines, of which 2.6k are tests. egglog-bridge is 4.3k (1.5k tests). The three support
crates total 3.4k (1.1k tests). About 20.5k non-test lines would have to be ported.

### 1.2 One rule from definition to the next iteration

Running example: `(rule ((= e (Add a b))) ((union e (Add b a))))`.

**A. Definition (frontend → bridge).**
- The frontend flattens the rule to core IR (`fe/core.rs:803-849`, `624-774`) and canonicalizes it (`933-1004`,
  `1042-1081`). The body becomes the atom `Add(a, b, e)`. The head becomes `Let t = Add(b, a)` followed by `Union(e, t)`.
- `BackendRule` (`fe/lib.rs:2525-2611`) replays this IR against the bridge `RuleBuilder`:
  - `query_table(Add, [a, b, e], Some(false))` records an atom over the physical columns
    `[a, b, e, ts₁, Const(NOT_SUBSUMED)]`. The bridge adds a fresh timestamp variable and a subsume entry
    (`eb/rule.rs:349-385`).
  - `lookup(Add, [b, a])` and `union(e, t)` do not emit anything yet. They push closures onto `Query.add_rule`.
- `build()` records `RuleInfo {last_run_at: 0, cached_plan: None}` (`eb/rule.rs:294-308`). A rule with one atom is
  forced to the MinCover strategy.

**B. Plan (lazily, at the rule's first run; `eb/rule.rs:719-771`, `eb/lib.rs:1404-1409`).**
- A core `QueryBuilder` gets one variable per bridge variable.
- `add_atom` runs for each atom (`cr/query.rs:355-463`):
  - Constant entries become `EqConst` constraints.
  - `process_constraints` splits constraints into *fast* ones, which have a subset pushdown, and *slow* ones
    (`cr/free_join/mod.rs:718-761`). Fast constraints are answered by `fast_subset`, or by a cached column index for an
    `EqConst` on a cacheable column.
  - A variable repeated within an atom becomes a slow `Eq{i, prev}`.
  - The atom's key variables determine its value variables; this is recorded as a functional dependency.
- The core `RuleBuilder` emits `ReadCounter(ts)` first (`eb/rule.rs:749`). Then the recorded closures are replayed in
  order and emit `LookupOrInsertDefault(Add, [b, a], [IncCounter(id), next_ts, NOT_SUBSUMED], ret)` and
  `Insert($uf, [e, t, next_ts])`.
- `build_with_description` computes `used_vars`, registers an `ActionInfo`, and calls `plan_query`
  (`cr/query.rs:551-576`, `cr/free_join/plan.rs:1183-1192`). The result is a `SinglePlan`, or a `DecomposedPlan` for
  about a quarter of rules with three or more atoms (§2.7).
- `build_cached_plan` keeps the plan. Its stage list and instructions are `Arc`-shared.

**C. Instantiate (each iteration; `eb/lib.rs:587-624`, `1397-1420`; `eb/rule.rs:779-846`).**
- `T = next_ts()`. For each rule, `add_rules_from_cached(mid = last_run_at)` builds N variants:
  - variant *i* constrains atom *i* with `GeConst(ts, mid)`;
  - it constrains every atom *j < i* with `LtConst(ts, mid)`;
  - atoms after *i* are unconstrained.
- `add_rule_from_cached_plan` (`cr/query.rs:191-274`) runs each constraint through `process_constraints`. On the
  timestamp column this is a binary search over the run-length index. The result is a `JoinHeader` subset. If any header
  is empty, the variant is dropped before any work is done.
- `last_run_at := T`.

**D. Execute the join (`cr/free_join/execute.rs:348-636`).**
- For each variant, every atom gets a root `TrieNode` over `table.all()`, intersected with its header subsets.
- `run_join_stages` orders the stages by current subset size, then recurses through the stage kernels
  (`904-1110`, `1111-1802`). Each kernel emits frames, which are bindings plus refined atom subsets, and drains them every
  256 frames.
- At the leaf, the bindings are pushed into the action's 128-lane `Bindings` batch (`1896-1918`).

**E. Run the actions (`cr/action/mod.rs:600-882`).**
- A full batch runs `run_instrs` on a clone of `ExecutionState`. Every instruction runs over all lanes under a bitset
  mask.
- `ReadCounter` yields T.
- `LookupOrInsertDefault` reads the *committed* `Add` table. On a miss it consults the per-state `predicted` cache, then
  mints an id and stages the row `[b, a, fresh, T, 0]`.
- `Insert` stages `[e, t, T]` into `$uf`.
- Staging notifies the table on the database's `NotificationList`. Nothing becomes visible yet.

**F. Merge the staged writes (`cr/free_join/mod.rs:507-605`).**
- `merge_all` loops to a fixpoint over the notified tables, one dependency stratum at a time.
- `SortedWritesTable::merge` applies all deletes, then all inserts (`cr/table/mod.rs:530-535`). An insert that hits an
  existing key calls the merge function. `UnionId` stages one more union and returns `min`.
- `DisplacedTable::merge` applies the staged unions with union-by-min and appends `(loser, ts)` to its displaced log
  (`cr/uf/mod.rs:446-510`).
- Finally every cached index lock is reset, and `total_size_estimate` is recomputed.

**G. Rebuild (`eb/lib.rs:587-682`).**
- If the union-find did not grow, the bridge calls `inc_ts` and stops.
- Otherwise it loops until nothing changes:
  1. `rebuild_containers`.
  2. `apply_rebuild(uf, tables, next_ts)`. Each table canonicalizes the rows that mention newly displaced ids
     (incrementally, or by a full scan) and stages `remove(old key)` plus `insert(canonical row with ts = pass ts)`. Then
     `merge_all` runs; key collisions fire merge functions, which stage more unions.
  3. `refresh_rows_for_values(dirty container ids)`.
  4. `inc_ts`.

**H. Next iteration.** Every row written in steps E–G has a timestamp ≥ the `last_run_at` just recorded, so the next
round's focus variants find them as new rows.

### 1.3 How timestamps thread through

| Event | Timestamp written | Reference |
|---|---|---|
| Bridge construction | The counter starts at 1, and every rule's `last_run_at` starts at 0 | `eb/lib.rs:149-152`, `eb/rule.rs:300` |
| Rule run k | Every row written by an action gets `T_k = read_counter(ts)`. The counter does not change during a run. | `eb/rule.rs:749`; `eb/lib.rs:1411-1417` |
| Row written by a merge function during `merge_all` | `read_counter(ts)`, which is still `T_k` | `eb/lib.rs:1193-1201`, `1307-1308` |
| Key conflict with a changed value | The output row takes the *incoming* row's ts. If nothing changed, the old row and its old ts are kept, so no delta is created. | `eb/lib.rs:1059-1101` |
| Rebuild pass p | Rewritten rows get that pass's `next_ts`. The first pass reuses `T_k`; the counter is incremented after each pass. | `eb/lib.rs:667-676`; `cr/table/rebuild.rs:21-31` |
| Container meaning changed under an unchanged id | Rows that mention the id are re-inserted with `next_ts` | `cr/table/rebuild.rs:77-118` |
| Union | The `$uf` row is `[a, b, ts]`; `displaced` is sorted by ts | `cr/uf/mod.rs:500-505` |

**Ordering invariant.** Every table's sort column is nondecreasing in RowId order. Serial insert asserts this per row.
Parallel insert asserts a single value per merge (`cr/table/mod.rs:726-756`, `1402-1446`). This is why "new rows" form a
dense suffix and "old rows" a dense prefix.

**Version-based incrementality is separate and independent of timestamps.**
- `TableVersion.minor` is the physical row count, so `updates_since(minor)` is exactly the appended suffix.
- A change of `major` (compaction or clear) invalidates every RowId.
- Index refresh, `SubsetTracker`-driven incremental rebuild, and container rebuild all use this version, not
  timestamps (`cr/hash_index/mod.rs:70-91`, `cr/common.rs:197-222`).

---

## 2. Subsystems

### 2.1 Values, ids, row buffers, pools, hashing

**Purpose.** The cell type, id types, row-major batches, object pools, and hash/shard functions that every other module
uses.

**Data layout.**
- `Value` is a `u32` newtype. `Value::stale() = u32::MAX` is reserved as the tombstone (`cr/common.rs:103-119`). A
  Value carries no type tag; the static column type gives it meaning. E-class ids, container ids, interned base values,
  timestamps and the subsume flag all share this representation.
- `define_id!` generates `u32` newtypes. `from_usize` panics on overflow even in release builds (`nid/lib.rs:448-554`).
- `DenseIdMap<K,V>` is a `Vec<Option<V>>` indexed by id. Iteration is in ascending id order. `get_mut` and `take`
  *grow* the map. `len()` is O(n). (`nid/lib.rs:48-266`)
- `IdVec` is a `Vec<V>`. `DenseIdMapWithReuse` adds a free list; rule ids and external-function ids are reused through
  it (`304-369`).
- `RowBuffer {n_columns, total_rows, data: Pooled<Vec<Cell<Value>>>}` stores row *r* at
  `[r·arity, (r+1)·arity)` (`cr/row_buffer/mod.rs:112-386`). Arity must be at least 1. A zero-arity removal key buffer
  is just a counter (`ArbitraryRowBuffer`, `cr/table/mod.rs:178-225`).
- `TaggedRowBuffer` has arity n+1. Its last column holds the source RowId (`row_buffer/mod.rs:391-521`). A variant with
  inline `SmallVec` storage (64 cells) is used for leaf binding sets.
- `ParallelRowBufWriter` lets several threads append whole buffers concurrently (`540-605`).

**Algorithms.**
- `set_stale` writes `u32::MAX` into column 0 and returns the previous stale state (`234-239`). `set_stale_shared` does
  the same through `&self` with no synchronization, which is sound only when a shard owns the row (`317-322`).
- `remove_stale(remap)` compacts in place in one `Vec::retain` pass. Live rows keep their order, and
  `remap(row, old, new)` is called in increasing order (`357-385`).
- Parallel append: `reserve_space` does one `fetch_add` per batch and then a memcpy. Growth takes the exclusive side of a
  `ReadOptimizedLock` (`conc/parallel_writer.rs:119-217`). The table reserves the total pending row count first, so growth
  is rare (`cr/table/mod.rs:679-680`).
- Pools: a thread-local `PoolSet` holds one typed pool per object type, each with a byte cap between 1 and 32 MiB
  (`cr/pool/mod.rs:424-463`).
  - `Pooled<T>`, when dropped, returns its object to the dropping thread's pool if `reuse()` holds. For a Vec that means
    capacity > 256.
  - `refresh` swaps a large recycled buffer into a buffer that is being refilled.
  - Quirk: `FixedBitSet` is never actually pooled, because `clear` empties it and the re-check then fails.
- Hashing: FxHasher from rustc-hash 2.1.1, roughly `h = (h + x)·0xf1357aea2e62a9c5` with a final `rotl 26`. Keys are
  hashed one `usize` per value (`cr/table/mod.rs:1277-1286`).
- `ShardData::shard_id(h) = (h >> (64-(log2+7))) & (n-1)`. These are the bits just below hashbrown's 7 control-tag bits
  (`cr/common.rs:140-192`).
- `InternTable<K,V>` is a `ConcurrentVec<K>` of keys (the id is the position) plus 16 `Mutex<HashTable<id>>` shards
  picked by the top hash bits. The shard lock is held across the push, so ids stay dense and duplicates are impossible
  (`common.rs:26-89`).

**Invariants.**
- `u32::MAX` never appears as a live column-0 value.
- RowIds are dense and stable until `remove_stale`.
- A pooled object is always cleared before reuse.
- Interned ids are never reused. Cloning an `InternTable` shares its storage.

**Performance.**
- One allocation per batch. Rows are slices.
- Deletion is a tombstone; compaction is deferred.
- `Cell` lets threads write tombstones without atomics.
- Large buffers are recycled.
- Hash shards store only ids.

**Concurrency.**
- `RowBuffer` is `unsafe impl Send + Sync`. Concurrent reads are safe. Writes through a shared reference are only allowed
  on rows the writer owns.
- Pools are per thread and are cleared on every rayon thread when a `Database` drops (`cr/free_join/mod.rs:823-830`).
- `InternTable` is fully concurrent.

### 2.2 Offsets and subsets (`cr/offsets/mod.rs`)

**Purpose.** The vocabulary for sets of rows. Tables, indexes, the planner's headers and the executor's trie nodes all
use it.

**Layout.**
- `RowId` is a `u32`.
- `OffsetRange {start, end}` is a half-open range (`28-55`).
- `SortedOffsetVector` and `SortedOffsetSlice` hold sorted, *nondecreasing* row ids. Duplicates are tolerated (`57-227`).
- `Subset` is `Dense(OffsetRange)` or `Sparse(Pooled<SortedOffsetVector>)`. `SubsetRef` is `Copy` and borrows the
  sparse case (`255-542`).
- The canonical empty subset is `Dense(0,0)`. Intersecting two dense ranges can also give an empty `Dense(x,x)`, so
  emptiness must be tested by size, never by equality.

**Algorithms.**
- `intersect(self, other)` (`400-509`):
  - Dense ∩ Dense is a max/min clamp.
  - Dense ∩ Sparse binary-searches both ends and copies the middle; the result stays Sparse.
  - Sparse ∩ Dense truncates in place.
  - Sparse ∩ Sparse uses a two-pointer merge when both sides have ≤ 16 elements or when the sizes are within 4× of each
    other. Otherwise it gallops: probes at `start + 2^k − 1`, then a binary search (`scan_for_offset`, `157-226`).
- `binary_search_from` is a lower bound that walks left over duplicates.
- `iter_bounded(start, end, f)` iterates *positions* within the subset, not RowIds, and returns the next position. Scans
  are chunked this way. Because staleness is filtered inside `f`, a chunk can yield fewer rows than requested
  (`302-330`).
- `add_row_sorted(row)` requires `row ≥` the current maximum. A dense range stays dense until the first gap
  (`511-541`).
- `retain` rebuilds a Dense subset by `add_row_sorted`, and filters a Sparse one in place.

**Invariants.**
- Subsets are sorted.
- A subset is valid only within one table generation.
- `size()` counts slots, including tombstoned ones.

**Performance.**
- The Dense case costs two integers and no allocation. Deltas, `all()`, timestamp ranges and single rows stay dense.
- Intersection is adaptive.
- Borrowed intersection with a dense range (`intersect_with_dense_ref`) never allocates.

### 2.3 Tables (`cr/table_spec.rs`, `cr/table/`)

**Purpose.**
- The `Table` trait is the storage abstraction for every relation.
- `SortedWritesTable` is the implementation used for every egglog function. It is keyed, carries a functional
  dependency, is append-only, has a timestamp-sorted column, takes staged writes, merges conflicts, compacts lazily, and
  rebuilds against the union-find.

**The trait surface** (`table_spec.rs:146-362`):
- Reads: `all`, `len`, `version`, `updates_since`, `scan_generic(_bounded)` (generic, `Self: Sized`), `has_stale_rows`,
  `refine_live/refine_one/refine`, `fast_subset`, `split_fast_slow`, `get_row`, `get_row_column`.
- Writes and maintenance: `merge`, `new_buffer`, `rebuilder`, `apply_rebuild`, `refresh_rows_for_values`, `clear`,
  `dyn_clone`, `as_any`, `spec`.
- `TableSpec {n_keys, n_vals, uncacheable_columns, allows_delete}` (`53-81`).
- `TableVersion {major: Generation, minor: Offset}` (`42-51`).
- `Constraint` is `Eq{l_col, r_col}` or one of `EqConst/LtConst/GtConst/LeConst/GeConst{col, val}` (`93-101`).
- `MutationBuffer {stage_insert, stage_remove, fresh_handle}` (`369-382`).
- `Rebuilder {hint_col, rebuild_val, rebuild_buf, rebuild_subset, rebuild_slice}` (`111-135`).
- `WrappedTable` pairs the trait object with a `WrapperImpl<T>`. That wrapper downcasts once per call and runs the
  monomorphized scan with projection closures specialized for 1, 2, 3 or N columns. The result is one virtual call per
  batch, not one per row (`384-859`).

**Layout of `SortedWritesTable`** (`table/mod.rs:136-152`):

| Field | Content |
|---|---|
| `rows: Rows` | `RowBuffer data`, a `scratch` buffer (target of parallel rehash), `stale_rows` count (`73-124`) |
| `hash: ShardedHashTable<TableEntry{hashcode: u64, row: RowId}>` | One entry per *live* key. Keys are compared in place in the row buffer, not copied. There are 2^k shards: 1 if single-threaded, else `next_pow2(2·threads)`, fixed at creation (`sharded_hash_table.rs:8-50`). |
| `sort_by`, `offsets: Vec<(Value, RowId)>` | Run-length index of the sort column: (value, first row with that value). Both components are strictly increasing, and runs include tombstoned rows. |
| `pending_state: Arc<PendingState>` | One `SegQueue<RowBuffer>` of inserts and one `SegQueue<ArbitraryRowBuffer>` of removal keys per shard, plus atomic size hints (`1289-1365`) |
| `merge: Arc<MergeFn>` | `Fn(&mut ExecutionState, cur, new, &mut Vec<Value>) -> bool` (`133-134`) |
| `to_rebuild`, `rebuild_index: Index<ColumnIndex>`, `subset_tracker` | The Id columns; a reverse index from value to rows over *all* of those columns; and the union-find version seen at the last rebuild |
| `generation` | Incremented on compaction and when a non-empty table is cleared |

The bridge instantiates it as follows (`eb/lib.rs:507-573`, `1533-1618`):
- Columns are `[k₀…kₙ₋₁, ret, ts, subsume?]`, with `n_keys = schema.len() − 1`.
- `sort_by` is the ts column.
- `to_rebuild` holds the Id-typed key and return columns.
- `merge` is the bridge callback (§3.2).

**Staging** (`227-287`, `518-528`).
- `Buffer` pre-shards each row at `stage_insert` by hashing its key columns. It keeps per-shard `RowBuffer`s and holds a
  `Weak<PendingState>`.
- On `Drop`, it pushes each shard's buffer onto that shard's `SegQueue` and bumps the counters. If the table is gone, the
  writes are silently dropped.
- `Database::new_buffer` and `ExecutionState::stage_*` also notify the table (`cr/action/mod.rs:401-409`).
  `Table::new_buffer` alone does not.

**Merge** (`530-535`): `do_delete`, then `do_insert`, then `maybe_rehash`.

- **Order.** All removals are applied before all inserts. So `remove(k) + insert(k, row)` staged in the same batch
  replaces the row *without* calling the merge function. Rebuild and refresh rely on this.
- **`do_delete`** (`592-676`), for each shard: find the entry by (full hash, key slice), remove it, and tombstone the
  row. A missing key is a no-op. The parallel path (above 20000 removals, more than one thread) runs one task per shard
  and uses `set_stale_shared`.
- **`serial_insert`** (`699-815`). Shards are processed in id order; within a shard, queue order, then staging order.
  Rows with a stale column 0 are skipped. For an incoming row *q*:
  - *Key exists, live row r:* if `merge(es, r, q, out)` returns true, then append `out`. Update the offsets from
    **q's** sort value, asserting it is ≥ the last run, and push a new run if it is greater. Tombstone *r*, point the
    hash entry at the new row (the key and hash are reused, so `out` must keep the key), and set changed. If it returns
    false, write nothing: the old row keeps its timestamp, so no delta is produced.
  - *New key:* append *q*, do the same offsets bookkeeping, insert the entry, and set changed.
  - The result per key is a left fold that starts from the table row.
- **`parallel_insert`** (`817-974`). Used above 20000 pending rows with more than one thread. It runs one task per
  shard, and each task has its own `ExecutionState` clone.
  - Phase 1 pre-merges duplicate keys in a local `StagedOutputs`. This step stores merge outputs correctly
    (`1501-1538`).
  - Phase 2 memcpys every staged row, including rows already merged away, into the shared buffer. It uses one atomic
    reservation per 2¹⁸-row batch.
  - Phase 3 merges each staged row into the existing table row. **Two defects** were confirmed experimentally (§8.1):
    - (a) When the merge returns true, the hash entry is pointed at the *incoming* row and the merge output in `scratch`
      is discarded (`902-907`). At `-j 32` this produced wrong results: `set-union` gave `{2}` instead of `{1,2}`,
      `(+ old new)` gave 10 instead of 11, and 65535 rows lost their subsumption.
    - (b) `changed = true` is set for every staged row before the merge decision (`871`). A `saturate` with a `:naive`
      rule then never terminates.
  - Even with the discard fixed, parallel evaluates `merge(T, fold(q₁…qₙ))` where serial evaluates
    `fold(T, q₁…qₙ)`. The two agree only for associative merges.

**The conflict contract Eggnog adopts.** These are egglog's serial semantics, stated precisely.
- For key k with visible row V and incoming rows q₁…qₙ in a defined order: `r := V; for each qᵢ, if merge(r, qᵢ, out)
  then r := out`. If there is no V, start with `r := q₁`.
- If `r ≠ V`, append `r` itself. Keys are unchanged, the timestamp equals the incoming epoch, and V is tombstoned (or,
  in a child, hidden).
- If every merge returned false, store nothing.
- `changed` means a new key was appended or some merge returned true.
- egglog's serial `merge_all` also ORs `es.changed`, which is set when the merge function staged anything (for example
  a union) (`cr/free_join/mod.rs:555, 564, 599`). Eggnog keeps this, so that union-only merges keep the merge fixpoint
  loop going.
- Pre-merging per shard is allowed, provided the final `merge(V, s)` output is the one stored.
- Merge order is not part of egglog's semantics. Its documentation requires lattice merges (`fe/ast/mod.rs:672-678`).

**Sort column and `fast_subset`** (`442-509`, `976-1002`).
- `binary_search_sort_val(v)` returns either `Ok((run start, run end))` or `Err(first row with value > v)`.
- Constraints on the sort column map to dense ranges. With N = `next_row`, and g = the first row with value > v when v
  has no run:

| Constraint | v has a run | v has no run |
|---|---|---|
| `EqConst v` | the run `[start(v), end(v))` | empty |
| `LtConst v` | `[0, start(v))` | `[0, g)` |
| `LeConst v` | `[0, end(v))` | `[0, g)` |
| `GtConst v` | `[end(v), N)` | `[g, N)` |
| `GeConst v` | `[start(v), N)` | `[g, N)` |

- Results can include tombstones.
- `Eq` and constraints on other columns return `None`, which means they are slow.
- `split_fast_slow` intersects every fast constraint into `all()` (`table_spec.rs:313-335`).

**Scans, refinement and the staleness contract.** This is the most important port-relevant fact about tables.

| Path | Filters tombstones? |
|---|---|
| `all()`, `fast_subset`, `updates_since` | No. Callers must filter. |
| Header subsets from `process_constraints`, including `EqConst` through the DB column index | **No.** They are intersected into root trie nodes without filtering (`execute.rs:388-400`). |
| DB-level cached index subsets | They may contain rows tombstoned after the last refresh. These are tagged `maybe_stale` and filtered at probe time. |
| `scan_generic`, and `scan_generic_bounded` with no constraints | Yes, whenever `stale_rows > 0` (`356-428`) |
| **`scan_generic_bounded` with constraints** | **No.** It calls `get_if_unchecked` (`429-439`, `1018-1025`). This regressed in `6e75cefe`. |
| `refine`, `refine_one`, `refine_live` | Yes: `get_if` reads through `get_row` (`511-516`, `1027-1034`) |
| `for_each_col`, `group_by_col`, `group_by_key`, `SparseColumnIndex` | Yes, via `scan_generic` |
| `get_row`, `get_row_column` | Yes. Hash entries point only at live rows (`537-551`). |
| Executor `refine_subset` | Runs `refine_live` if the subset `can_be_stale && has_stale_rows`, then `refine(cs)` (`execute.rs:1091-1107`) |
| Atoms with zero variables | **No.** No stage exists, so only header non-emptiness is checked (`plan.rs:1502-1505`). |

**How the hole is reached.**
- In bridge rules, the only slow constraints are repeated-variable `Eq`s (`cr/query.rs:426-431`).
- The hole needs four things together:
  - such a constraint lands on a *FusedIntersect cover*, not on an `Intersect` scan or a probe;
  - that cover is the atom's first executed stage;
  - no later stage touches the atom;
  - the `Eq` does not involve column 0.
- Under Free Join this happens for every rule with a single atom, because the bridge forces MinCover on those. Examples
  are `(= b (f a b))` and `(R a b b)`. Under Generic Join it needs an atom that no `Intersect` touches.
- What a leaked row binds: column 0 binds `u32::MAX`, and the other columns keep the dead row's values.
- Reproduced at HEAD:
  - after a `delete`, `check` succeeds and rules re-derive deleted facts;
  - `:merge new` re-derives a superseded value;
  - subsumed rows match;
  - non-canonical ids are written after a rebuild.
- No test in the egglog corpus catches it.

**Point lookups.** `get_row(key)` hashes the key, picks the shard, and calls `find(hash, entry.hashcode == h && key slice
equal)`. It returns a pooled copy of the row. `get_row_column` returns one cell.

**Compaction** (`1036-1245`).
- `maybe_rehash` runs when `stale_rows > max(16, physical/2)`.
- Serial `rehash`:
  - increments `generation`;
  - compacts with `remove_stale`;
  - in `remap`, finds the hash entry whose RowId equals the old row and points it at the new one;
  - rebuilds `offsets`.
- `parallel_rehash` (sorted tables, above 20000 rows) works in two steps:
  - It builds a histogram per (timestamp run, shard), then takes prefix sums.
  - Each shard scatters its live rows into `scratch` with no synchronization. Rows within a run end up reordered by
    shard.

**Rebuild** (`table/rebuild.rs`).
- `do_rebuild(uf_id, uf, next_ts)` (`45-75`):
  1. Get `rebuilder = uf.rebuilder(to_rebuild)`. If `hint_col` is set, `to_scan = subset_tracker.recent_updates(uf)`,
     which is the union-find rows appended since this table's last rebuild. *The tracker advances here, before the mode
     is chosen.*
  2. Choose incremental if `table_physical_rows > 10000 && delta·8 ≤ size`. When parallel, the ratio is 8192.
  3. **Incremental** (`120-195`): refresh `rebuild_index`. For each displaced id, take the rows mentioning it, copy them,
     and canonicalize every Id column with `find_naive`. Unchanged copies are tombstoned in the scratch buffer. For each
     changed copy, stage `remove(old key)` and then `insert(row with ts = next_ts)`.
  4. **Full** (`197-269`): scan the whole buffer in chunks of 2048 with `rebuild_buf`, which has loops specialized for
     1, 2 and 3 columns. Apply the same remove plus insert.
- Merging afterwards gives congruence. Deletes run first, so a canonical row that collides with an existing key goes
  through the merge function.
- `refresh_rows_for_values(dirty_ids, next_ts)` (`77-118`) re-inserts every live row that mentions a dirty id, with a
  fresh timestamp.

**The canonicity invariant (INV)** that incremental rebuild depends on.
- Statement: no live row holds, in an Id column, a value that was displaced at a union-find log index below the
  table's watermark `w_T`.
- Incremental rebuild looks only at displaced ids from `D[w_T..len)`. A violation below the watermark is therefore
  repaired only by a pass that happens to use the full scan.
- At bridge loop exit, every live row is canonical and every watermark equals |D|, *provided INV held at loop entry and
  no breaking write happened*.
- Writes that break INV:
  - scheduler residual matches (§8.1);
  - caller-supplied stale ids through `add_values`, `add_term` or `TableAction`;
  - in parallel `merge_simple`, a Function merge that reads a table whose rewrite is not merged yet;
  - leaked tombstones.

**`clear` and `clone`.**
- `clear` drains the pending queues, clears rows, hash and offsets, and increments the generation if the table had rows
  (`296-305`).
- `clone` copies rows, hash and offsets, shares the merge `Arc`, gives the *clone* a new `PendingState` holding a copy of
  the queue, and resets `rebuild_index` and `subset_tracker` (`154-171`, `1328-1364`).

**Invariants.**
- One live row per key.
- Rows are immutable except for the tombstone.
- `minor` is the physical row count, and removals do not change the version.
- The sort column is nondecreasing.
- Deletes are applied before inserts.
- `stale_rows` is exact.
- Each shard is owned by exactly one task during a parallel phase.

**Performance.**
- A key is never copied into the hash table.
- Each hash entry caches its full 64-bit hash.
- Writes are published per whole buffer, not per row.
- Pre-merge and append are batched.
- When `stale_rows == 0`, scans skip the stale check. Scans do one bounds assert and then use unchecked indexing.
- Rebuild is incremental, with a fallback to a full scan.

**Concurrency.**
- All `&self` reads are safe in parallel while rules run, because tables do not change during a search.
- `merge`, `apply_rebuild` and `clear` take `&mut`.
- Parallel phases split work by shard ownership.
- Queue push order depends on thread timing. With merges that are not commutative, the results are nondeterministic.

### 2.4 Union-find (`ufc/`, `cr/uf/`)

**Purpose.** Equivalence of e-class ids. The union-find is exposed as a table so that rules, merges and rebuild can see
equalities through ordinary table operations.

**Sequential union-find** (`ufc/lib.rs:22-104`).
- `parents: Vec<V>`.
- `union` finds both roots, sets `parent = min`, and `parents[max] = min`, so that **parent < child always holds**.
- `find` does path halving, which writes.
- `find_naive` never writes, and treats an id beyond the array as a root.
- Rationale: the smaller, older id is usually referenced more, so keeping it canonical disturbs fewer rows.

**`DisplacedTable`** (`cr/uf/mod.rs:54-523`).
- Fields:
  - `uf`;
  - `displaced: Vec<(child, ts)>`, one entry per *effective* union, sorted by ts;
  - `lookup_table: HashMap<child, RowId>`;
  - `buffered_writes: SegQueue<RowBuffer>`.
- Logical row *i* is `[displaced[i].0, find_naive(child), ts]`.
- Spec: `n_keys = 1`, `n_vals = 2`. Column 1 is **uncacheable** because it changes without a version bump.
  `allows_delete = false`.
- The version is always `{major 0, minor len}`.
- `merge` (`446-510`), for each staged `[l, r, ts]`:
  - skip it if `find(l) == find(r)`;
  - otherwise union and compress both;
  - assert ts is nondecreasing;
  - push `(loser, ts)`;
  - report `added = removed = changed`.
- `fast_subset` (`351-419`):
  - `EqConst` on column 0 is a lookup in `lookup_table`;
  - range constraints on column 2 use binary search plus widening over equal ts;
  - column 1 is never fast.
- `get_row_column(key, 1) = find_naive(key)` for *any* key (`429-433`). The bridge's `lookup_uf` is
  `LookupWithDefault(uf, [x], x, col 1)`.
- `stage_remove` panics.
- **`Canonicalizer`** is the `Rebuilder` (`62-196`):
  - `hint_col = 0`: the changed values are exactly the column-0 values of newly appended rows.
  - `rebuild_val = find_naive`.
  - `rebuild_buf` emits only changed rows, with loops specialized for 1, 2 and 3 columns.
  - `rebuild_subset` copies rows and tombstones the unchanged copies.
  - `rebuild_slice` is used for container contents.
- **The concurrent union-find** (`ufc/concurrent/`) is lock-free with CAS and union-by-min. It is *unused* by
  core-relations and appears only in benchmarks.

**Invariants.**
- An id is displaced at most once.
- `len` counts effective unions. The bridge's "no union happened, skip the rebuild" test relies on this.
- Readers never write.

**Concurrency.**
- Rule threads read with `find_naive` through `&self`.
- Unions are applied only during the exclusive merge.
- Defect: `clear` does not reset `lookup_table` and does not bump the generation (`286-289`).

### 2.5 Indexes (`cr/hash_index/`) and how the join uses them

**Purpose.** Secondary hash indexes from a key (one value, or a tuple) to the subset of rows with that key. They are
cached per table and refreshed incrementally from the table version.

**Layout.**
- **`ColumnIndex`**: `ShardData` plus shards of `{IndexMap<Value, BufferedSubset>, SubsetBuffer}`. Shards are picked by
  FxHash of the value (`205-533`).
  - It doubles as a multi-column "contains" index: `add_row(vals, row)` adds the row under *each* value of the projected
    columns. That is the `rebuild_index`.
- **`TupleIndex`**: shards of `{SubsetTable{keys: RowBuffer, hash: HashTable<TableEntry{hash, key: RowId, vals}>}, SubsetBuffer}`.
  Each distinct key is stored once, out of line (`535-730`).
- **`BufferedSubset`**: `Dense(range)`, or `Sparse(start, end)` into the shard's arena (`931-973`).
- **`SubsetBuffer`**: one `Vec<RowId>` per shard. A sparse list of length L occupies a block of `next_pow2(L)` slots,
  and freed blocks go on a 32-bucket free list keyed by log2 of the block size. Dropping an index costs O(shards)
  (`802-1013`).
- **`Index<TI>`**: `{key cols, updated_to: TableVersion, TI}` (`41-121`).
- **`IndexCatalog<K, I>`**: a `ReadOptimizedLock<Vec<(K, I)>>` searched linearly. A `TupleIndex` is keyed by its
  *ordered* column list (`744-789`).
- `TableInfo.indexes` and `TableInfo.column_indexes` hold `Arc<ResettableOnceLock<Index<…>>>`
  (`cr/free_join/mod.rs:131-140`).

**Algorithms.**
- **`Index::refresh(table)`** (`70-112`):
  - If the versions are equal, do nothing.
  - If the major version changed, clear and rebuild from `all()`.
  - Otherwise index `updates_since(minor)` in batches of 1024 rows through `scan_project`.
  - Above 20000 rows, `merge_parallel` runs on a *separate static rayon pool* through `run_in_thread_pool_and_block`, so
    that a caller holding the `Once` never steals work that could deadlock (`385-420`, `980-992`).
  - Rows already stale when scanned are skipped. Rows tombstoned later stay in the index until the next generation.
- **`add_row_sorted`**: a dense range grows at its end; the first gap turns it Sparse. In the arena, `push_vec` writes in
  place when the block is not full, otherwise it moves to the next size class.
- **`ColumnIndex::rebuild_full`** (`334-488`):
  - Collect `(value, row)` pairs and radix-sort them by value: 1 to 4 LSD byte passes, with `sort_unstable` below 64
    pairs.
  - Group the pairs. A group is dense if `last − first == count − 1`.
  - **Defect:** with more than one column and at least 64 pairs, groups are col0 rows followed by col1 rows, so sparse
    lists can be unsorted, and `dedup` misses non-adjacent duplicates. Sort by `(value, row)` instead.
- **`build_for_subset`** builds a per-subset index: sort-based when the subset has ≥ 512 rows, per row otherwise
  (`516-532`).
- **Lifecycle** (`cr/free_join/mod.rs:837-876`, `575-585`):
  - `get_or_insert` in the catalog is double-checked.
  - `get_or_update(refresh)` runs under `std::sync::Once`.
  - At the end of `merge_all`, every lock is reset through `Arc::get_mut(..).unwrap()`, which panics if any `Arc` clone
    is still alive. The reset only re-arms the lock: data and `updated_to` stay, so the next access does an incremental
    refresh.

**Join-side selection** (`cr/free_join/execute.rs:819-893`). Index choice depends on the atom's *current* subset:
- Subset of ≤ 8 rows and one column: `SparseColumnIndex`, sorted stack arrays.
- Dense subset, every column cacheable, and `whole/2 < subset.size()`: the DB-level cached index, with
  `intersect_outer = subset range` as a cheap clamp per probe. Results are `maybe_stale`.
- Several columns: `Dynamic(TupleIndex)`, rebuilt on *every* call and never cached.
- Otherwise: `DynamicColumn`, cached on the `TrieNode` per column.

**Invariants.**
- Append-only discipline: refresh relies on new rows having larger RowIds.
- Indexes never delete entries within a generation.
- No index is ever built on an uncacheable column.
- During a search, versions do not change, so each index refreshes at most once per merge epoch.

**Performance.**
- The Dense/Sparse duality.
- Arena allocation of sparse lists.
- Incremental refresh by version.
- Lazy staleness filtering.
- Radix bulk build.
- Parallel sharded build on its own pool.
- RCU catalog reads.

### 2.6 Database (`cr/free_join/mod.rs`)

**Purpose.** Owns the tables and orchestrates them: the catalog, constraint processing, merge to a fixpoint, rebuild
across tables, counters and external functions.

**Layout** (`289-312`):
- `tables: DenseIdMap<TableId, TableInfo{name, spec, table: WrappedTable, indexes, column_indexes}>`
- `counters: DenseIdMap<CounterId, AtomicUsize>`
- `external_functions: DenseIdMapWithReuse`
- `container_values`, `base_values`
- `notification_list: NotificationList<TableId>`
- `deps: DependencyGraph`
- `total_size_estimate`

**Algorithms.**
- **`process_constraints(table, cs)`** (`718-761`):
  - Run `split_fast_slow`.
  - Then promote every slow `EqConst` on a cacheable column to fast, by intersecting with the cached `ColumnIndex`
    subset for that value.
  - For bridge tables, every `EqConst` becomes fast; only repeated-variable `Eq`s stay slow.
- **`merge_all`** (`507-588`), in a loop:
  1. Take `v = notification_list.reset()`.
  2. If fewer than 4 tables are in `v`, call `merge_simple`. It merges in *notification order* and **ignores strata**
     (`593-605`), then the loop ends.
  3. Otherwise, for each stratum of `deps` intersected with `v`:
     - pre-allocate buffers for every declared write dependency;
     - take the tables out of the catalog;
     - merge them, in parallel with `par_iter_mut` when the database is large, each with an `ExecutionState` over a
       read-only view of the rest;
     - OR together `added || es.changed`;
     - put the tables back.
  4. Repeat until nothing is notified.
  5. Then reset every index lock and recompute `total_size_estimate`.
- **`DependencyGraph::add_table`** (`cr/dependency_graph.rs:32-53`): `level = 1 + max(level of read deps)`, or 0 if there
  are none. Tables in the same level do not read each other.
- **`apply_rebuild(uf, tables, next_ts)`** (`371-388`, `412-444`):
  1. Take the union-find table *out* of the catalog.
  2. Call `apply_rebuild` on each table, in parallel when the database is large. Notify the tables that changed.
  3. Put the union-find back.
  4. Call `merge_all`. The union-find is present for this merge.
- **Counters**: `inc` is `fetch_add(1, Release)` and returns the *previous* value. `read` is an `Acquire` load
  (`262-284`).
- **External functions**: `invoke(&mut ExecutionState, &[Value]) -> Option<Value>`. They must be `Send + Sync`. `None`
  drops the lane (`190-253`).

**Concurrency.**
- The search phase uses `&Database`. The merge phase is exclusive.
- `merge_all` merges each stratum in parallel.
- Defect: `merge_simple` ignores strata, and parallel `run_on_tables` notifies tables in completion order. Together they
  allow a Function merge to read a table whose rebuild rewrite has not been merged yet. **Eggnog always merges in
  stratum order.**

### 2.7 Query building and planning (`cr/query.rs`, `cr/free_join/plan.rs`)

**Purpose.** Turn a conjunctive query plus an action program into an immutable `Plan`, built once and reused with new
headers in every iteration.

**Builders** (`cr/query.rs`).
- `QueryBuilder::add_atom` (`355-463`):
  1. Check the arity.
  2. Append an `EqConst` for each constant.
  3. Call `process_constraints`.
  4. Skip wildcard `Variable::placeholder()` entries.
  5. Add a slow `Eq{i, prev}` for each repeated variable, chained as `R(x,x,x)` → `Eq{1,0}`, `Eq{2,1}`.
  6. Record `VarInfo.occurrences`, one `SubAtom` per (variable, atom), listing the columns.
  7. Record the functional dependency key vars → value vars.
- `RuleBuilder` (`517-874`) appends `Instr`s, creates destination variables, and marks variables `used_in_rhs` and
  `defined_in_rhs`. `build_with_description` sets `used_vars = used ∧ ¬defined` in ascending id order and pushes an
  `ActionInfo`. **`RuleId.index == ActionId.index`** is an invariant.
- **Cached plans** (`66-79`, `133-274`):
  - Stages and instructions are `Arc`-shared.
  - Every instantiation re-processes each extra and existing header against the current database.
  - A header constraint that stays slow panics.
  - An empty header returns `None`, before any `ActionId` is allocated.

**Plan types** (`plan.rs:69-290`).
- `Plan` is `SinglePlan{atoms, header, stages, actions}` or
  `DecomposedPlan{atoms, header, blocks: [(stages, MatSpec{msg_vars, val_vars})], result_block, actions}`.
- `JoinStage` is one of:
  - `Intersect{var, scans: [SingleScanSpec{atom, column, cs}]}`: Generic Join on one variable;
  - `FusedIntersect{cover: ScanSpec, bind: [(col, var)], to_intersect: [(ScanSpec, key positions into the cover projection)]}`:
    a Free Join step;
  - `FusedIntersectMat{cover: MatId, mode: Full|KeyOnly|Value(vars)|Lookup(vars), bind, to_intersect}`.
- A `JoinHeader {atom, fast constraints, subset}` is intersected into the atom's root node before the join.

**Strategies and who uses them.**
- `Gj` is the default, and the bridge uses it for every rule with more than one atom (`eb/rule.rs:152`).
- `MinCover` is used for rules with one atom (`eb/rule.rs:294-297`) and for the bridge's rule-based rebuild rules.
- `PureSize` appears only in tests.
- Decomposition is attempted when a rule has more than two atoms and `no_decomp` is not set.

**Algorithms.**
- **Tree decomposition** (`412-1192`):
  1. *Variable elimination.* Pick the variable minimizing (number of atoms touching its neighborhood, `ColUniqueness`
     of its occurrences). Ties go to the lowest variable id. The bag is the variable's neighborhood closed under the
     functional dependencies. `ColUniqueness` builds column indexes as a side effect.
  2. *Pruning.* Repeatedly merge subsumed bags, then merge "ears" into the neighbor they share the most variables with.
  3. *Topological sort into a chain* (`704-825`). Each bag's parent is the later bag sharing the most variables.
     **Regression:** `all_children[1..]` (`805`, from commit `7f4cbc49`) orphans `children[0]` when it is not the chosen
     continuation. The orphan's subtree becomes a separate chain whose root contributes through a `Lookup` epilogue.
     This is the *only* source of epilogues. It is sound, but `best_pathwidth` scores a different layout from the one it
     builds.
  4. *`plan_single_bag`* (`844-973`). A variable used in a later bag becomes a message variable. A variable that is
     RHS-used, or that was a message variable earlier, becomes a value variable in its last bag. Any other variable is
     skipped. The first earlier block, in descending order, whose message variables all lie in this bag becomes a
     `KeyOnly` prologue that probes every occurrence. Later qualifying blocks become `Lookup` epilogues.
  5. *`build_result_block`* (`975-1016`): `Full` on the last block, then `Value(msg_vars)` on the others, root first.
     Blocks with no value variables are skipped.
  6. `loop_lifting` (`1061-1100`) moves `Lookup` stages earlier.
- **Defect, lost join constraint.** Two rules combine badly:
  - a block contributes only to the *first* later bag that contains all of its message variables;
  - the result block skips blocks that have no value variables.
  So a block whose message variables are split across later bags, and which has no value variables, keeps only the
  constraint "materialization is non-empty". Unmodified egglog then derives a false match, and `check` succeeds on an
  unsatisfiable query. **Fix:** append `FusedIntersectMat{Mi, Lookup(msg_vars)}` to the result block for every block
  that contributed nothing, has no value variables, and has message variables.
- **`plan_gj`** (`1527-1581`):
  - Variables are planned in ascending id order.
  - A variable that occurs once and is unused is skipped, unless its atom would otherwise go unvisited. This is how the
    bridge's per-atom ts variables avoid enumeration.
  - The cover is the atom with the lowest id.
  - `fuse_single_scans` merges all single-scan stages on one atom, which lets the executor factorize leaf scans.
- **`plan_free_join` with `BucketQueue`** (`1302-1463`):
  - A greedy set cover. `pop_min` actually takes the *largest* bucket.
  - Stages are then ordered by approximate subset size.
  - `get_next_freejoin_stage` binds every new variable of the cover atom and probes the other atoms with keys built from
    positions in the cover tuple.
- **`compile_stage`** (`1583-1655`) puts each atom's slow constraints on that atom's *first scan in plan order*.

**Plans observed in practice** (corpus of 136 `.egg` files):
- Of 1,606 rules with at least three atoms, 1,210 collapse to one bag and become a `SinglePlan`. 396 become a
  `DecomposedPlan`: 286 with two bags, 76 with three, 16 with four, and 18 with five or more. In term-encoding mode,
  1,217 of 1,240 become a `SinglePlan`.
- Stage counts in decomposed plans: 2,531 `Intersect`, 1,224 leaf `FusedIntersect`, 410 `KeyOnly` (220 with
  multi-column keys, which go through an uncached `TupleIndex`), 43 `Lookup`.
- Result blocks: 396 `Full` and 512 `Value`. No `FusedIntersect` with probes occurred in any plan.
- An atom's headers are duplicated once per bag that contains it.

**Further defects.**
- Free Join strategies panic when combined with decomposition into two or more bags (`1344`, `1481-1491`).
- `MinCover` panics on an atom with no variables (`1337`).
- `check` ignores `--no-decomp` and uses `include_subsumed` (`fe/lib.rs:1478-1510`).

**Concurrency.** Planning runs on one thread under `&mut Database`. Plans are immutable and `Send + Sync`.

### 2.8 Executor (`cr/free_join/execute.rs`)

**Purpose.** Run every plan of a rule set against one snapshot, stream matches to the actions, then merge.

**Layout.**
- `BindingInfo{bindings: DenseIdMap<Var, Value>, binding_sets (a stack of factorized leaf results), subsets: DenseIdMap<AtomId, Arc<TrieNode>>, materializations}`
  (`763-808`). Bindings are overwritten and never cleared.
- `TrieNode{subset, cached_subsets: OnceLock<IdVec<col, OnceLock<Arc<ColumnIndex>>>>, cached_children: OnceLock<IdVec<col, RwLock<HashMap<Value, Arc<TrieNode>>>>>}`
  (`676-750`). This is a lazily built trie shared across morsels.
- `FrameUpdates{updates: Vec<PushBinding | RefineAtom | RefineAtomDense | EndFrame>, frames, last_start}`
  (`frame_update.rs:25-104`). `rollback` discards only the unfinished frame.
- `Prober{node, ix: Cached | CachedColumn (with intersect_outer) | Dynamic | DynamicColumn | SparseColumn}`
  (`155-345`). `PotentiallyStale` marks results that must go through `refine_live`.
- `ActionState{n_runs, len, Bindings(128)}`.
- `InstrOrder` is `SmallVec<u16>`, which caps a plan at 65535 stages. `LeafScans` holds per-position flags.

**Algorithms.**
- **`run_rule_set`** (`348-636`).
  - Parallel mode applies when the database holds more than 10k rows and more than one thread is available.
  - Serial mode runs plans in rule order with one `InPlaceActionBuffer`. Parallel mode spawns one rayon task per plan.
  - For each plan: create root nodes over `all()`, intersect the headers, skip the plan if any atom is empty, and run.
    A `DecomposedPlan` runs each block into its own materialization (empty means skip), then runs the result block.
  - Afterwards flush partial batches, record match counts, and call `merge_all`.
- **`run_plan(cur)`** (`946-1110`).
  - Check `should_stop`.
  - At the leaf, call `push_bindings_factorized`.
  - The chunk size is 256, or 32 at level 0 in parallel mode when there are more than two stages.
  - If the estimated size is over 32, `cur % 3 == 1`, and `cur` is not the last stage, re-sort the remaining stages.
  - Run the stage kernel. `drain` replays the frames and recurses with `run_plan(cur+1)` on each `EndFrame`; the last
    stage inlines the leaf. At levels 0 and 1 in parallel mode, the frames are moved into a spawned task along with a
    shallow clone of `BindingInfo`.
- **`refine_subset`** applies `refine_live` (if the subset may be stale and the table has stale rows), then the slow
  constraints. In `Intersect`, a candidate subset of ≤ 16 rows is copied and refined every time. A larger one goes
  through `get_cached_trie_node(col, val)`: a read lock first, then a double-checked insert under the write lock.
- **Kernels.**
  - `Intersect` (`1111-1375`):
    - one scan iterates the prober's keys;
    - two scans iterate the side with fewer keys and probe the other;
    - three or more scans drive from the prober with the fewest keys, and any miss rolls back the frame.
  - `FusedIntersect` without probes (`1376-1455`):
    - as a leaf scan, it projects the whole subset into a binding set and emits one frame;
    - otherwise it scans in chunks and emits one frame per row, with `RefineAtomDense([row, row+1))`.
    - Duplicate projected keys are not deduplicated.
  - `FusedIntersect` with probes (`1456-1569`): for each cover row, bind its variables, then build a key and probe each
    `to_intersect` index. A miss rolls back. Probe results are not cached on trie nodes.
  - `FusedIntersectMat` (`1570-1802`):
    - `KeyOnly` enumerates keys and probes;
    - `Value(vars)` and `Full` enumerate stored rows, duplicates included;
    - `Lookup` is a semijoin check.
    - `Full` and `KeyOnly` never drain mid-loop.
- **`sort_plan_by_size`** (`2286-2505`).
  - It splits the stages into segments at `Lookup`, `Value` and `Full` barriers.
  - Within a segment it runs a selection sort on the key `(−times_refined, estimate_size, −num_relations)`.
  - Quirk: `times_refined` is seeded from the plan's index order, not from the order actually executed.
  - `recompute_leaf_scans` marks a stage as a leaf scan when no later stage touches its atom or reads its bound
    variables.
  - Consequence: an `Intersect` over atom A always sorts before a single scan of A. That is why the stale hole needs an
    atom that no `Intersect` touches.
- **Leaf emission** (`1815-2109`).
  - `expand_binding_sets` takes the cross product of factorized sets.
  - `push_bindings` writes the used variables into the 128-lane batch. When the batch is full, it runs `run_instrs` on a
    *fresh clone* of `ExecutionState` (fresh buffers, empty predicted cache), counts the surviving lanes, and clears the
    batch.
- **Early stop.** An `Arc<AtomicBool>` is shared by all clones. It is checked at `run_plan`, at every `drain`, and per
  row in the last expansion. `run_instrs` itself never checks it, and leftover batches are still flushed after a stop.

**Multiplicity.**
- Matches form a multiset, not a set. Actions must be idempotent.
- Across the corpus, decomposed and undecomposed runs produce identical push and distinct-tuple counts. In fuzzing,
  decomposition gave fewer pushes in 3,244 of 13,851 executions and more in only 3. `num_matches` therefore depends on
  the plan.

**Invariants.**
- The database is read-only for the whole search.
- Subsets and RowIds are valid only within one `run_rule_set`.
- Each variable is bound by exactly one stage.
- Every node taken out for a stage is moved back afterwards.
- Barrier stages never move.

**Concurrency.** Three levels of parallelism: rules, morsels at depth 0 and 1, and full action batches. Trie caches
only grow, and they use `OnceLock` and `RwLock`. Batches run in nondeterministic order.

### 2.9 Actions: the RHS interpreter (`cr/action/`)

**Purpose.** Execute the action program of a rule over a batch of matches, staging writes.

**Layout.**
- `Bindings` is a flat `Vec<Value>` (`mod.rs:109-280`):
  - each variable owns a 128-slot segment at `var_offsets[var]`, and padding slots hold `stale`;
  - the first push of a batch maps every variable the join has bound, while later pushes write only `used_vars`,
    unchecked;
  - RHS destinations get new segments through `insert`.
- `Mask` is a pooled `FixedBitSet` of live lanes (`mask.rs:15-108`).
- `Instr` (`mod.rs:886-989`) is one of:
  - lookups: `LookupOrInsertDefault`, `LookupWithDefault`, `Lookup`, `LookupWithFallback`;
  - writes: `Insert`, `InsertIfEq`, `Remove`;
  - external calls: `External`, `ExternalWithFallback`;
  - checks: `AssertEq`, `AssertNe`, `AssertAnyNe{ops, divider}`;
  - `ReadCounter`.
- `ExecutionState` holds `{predicted, DbView, MutationBuffers, changed, Arc<AtomicBool> stop}` (`335-596`). Cloning it
  gives fresh buffer handles, an *empty* predicted cache, and the same stop flag.

**Algorithms.**
- **`run_instrs`** (`600-615`). If no variable is bound, it sets `matches = 1`, so a rule with no variables runs once per
  batch. Each instruction runs under the mask, and the program stops early when the mask is empty. It returns the
  popcount.
- **Instruction semantics** (`616-882`):
  - `Lookup` reads the committed table with `get_row_column`. A miss writes `stale` and clears the lane.
  - `LookupWithDefault` never filters.
  - `LookupOrInsertDefault` runs as follows:
    1. look up under a copy of the mask;
    2. for each live lane that missed, call `predicted.entry((table, key)).or_insert_with(build row: key ++ defaults)`;
    3. a default is `Const` or `Var`, `IncCounter` (mints an id), or `CurrentVal(ix)`;
    4. stage the row (this does **not** set `changed`) and write column `dst_col`.
    Two lanes, or two batches sharing one `ExecutionState`, get the same fresh id. Separate batches do not.
  - `LookupWithFallback` and `ExternalWithFallback` call the fallback only on lanes that missed, computed with
    `XOR`/`OR` of masks.
  - `Insert`, `InsertIfEq` and `Remove` stage per live lane.
  - `External` drops a lane when it gets `None`.
  - The assertions retain lanes.
  - `ReadCounter` writes the counter into every lane.
- Argument gathering is specialized by arity (0 to 4) and by constant/variable shape. Other shapes allocate a pooled
  vector per lane (`mask.rs:425-566`).
- **`predict_val` and `predict_col`** (`508-580`) return the committed row, or else the predicted or staged row.
  Merge functions and primitives call them, through `TableAction::lookup_or_insert`.
- **Staging** (`401-456`) creates the buffer lazily, notifies the table, and sets `changed`.

**Invariants.**
- Reads see the committed state from the start of `run_rule_set`. The only feedback within a run comes from the
  predicted cache and from container interning (§2.10).
- At most 128 lanes per batch.
- `used_vars` must be bound by the join. Rust uses `unwrap_unchecked` here, so a violation is undefined behavior.

**Performance.**
- One dispatch per instruction per batch.
- Lanes are masked, never compacted.
- Columns are stored struct-of-arrays.
- Table lookups downcast once per batch (`table_spec.rs:490-544`).

### 2.10 Base values and containers (`cr/base_values/`, `cr/containers/`)

**Base values.**
- `BaseValues{type_ids: HashMap<TypeId, BaseValueId>, tables: DenseIdMap<id, Box<dyn DynamicInternTable>>}`
  (`mod.rs:66-124`).
- Encodings:
  - `u8`, `u16`, `u32`, `i8`, `i16`, `i32`: `x as u32`. Signed values are sign-extended, so `-1` collides with
    `u32::MAX`.
  - `bool` is 0 or 1. Unit is 0.
  - `i64`, `u64`, `usize`, `isize`: inline if `0 ≤ x < 2³¹`, otherwise `0x8000_0000 + intern index`.
  - `String`, `Rational`, and `Boxed<T>` (including `f64` as `OrderedFloat`): intern index only.
- Boxing is canonical: Value equality is semantic equality within a type. Value *order* is not numeric order.
- Intern storage is `Arc`-shared and append-only.

**Containers.** `ContainerValues{subset_tracker, container_ids, data: DenseIdMap<id, Box<dyn DynamicContainerEnv>>}`.
`ContainerEnv<C>` (`containers/mod.rs:306-731`) holds:
- `to_id: DashMap<C, Value>`, the hash-cons;
- `to_container: DashMap<Value, (hash, shard)>`, a reverse map that holds no copy of the container;
- `val_index: DashMap<Value, IndexSet<Value>>`, mapping an element to the ids of containers that hold it. It is a
  superset of the truth.
- Container ids come from the bridge's **e-class id counter**, so they live in Id columns and in the union-find.

**Algorithms.**
- `get_or_insert` (`366-409`):
  1. Look the container up.
  2. Otherwise mint an id.
  3. Insert into `to_container` *before* publishing in `to_id`.
  4. If another thread wins the race, its id is used and the minted id is wasted.
- `insert_owned` (`411-439`) handles a collision by calling `merge_fn(old, new)`. The bridge's merge function stages a
  union `[old, new, ts]` and returns `min` (`eb/lib.rs:242-257`).
- `rebuild_all` (`166-240`):
  - It is parallel across environments.
  - Incremental mode (`n > 1000`, and `delta·8 ≤ n`, or 512 in parallel) takes the displaced ids and their `val_index`
    entries, then removes, canonicalizes and reinserts each affected container (`441-519`).
  - Full mode canonicalizes in place inside the buckets (`521-699`).
  - `dirty_ids` collects containers whose contents changed while their id did not. It is closed upward through
    `val_index`. The bridge re-stamps the parent rows of these ids.
- Defects:
  - In full mode, an id-only rename leaves `val_index` stale (`553-558`, `618-623`).
  - `get_container` re-enters a shard read lock it already holds (`704-706`).
  - The parallel threshold is wrong (`526`).

**Side effects.** Interning is immediate, global, unstaged and never rolled back. It happens in *every* primitive
context, including the evaluation of left-hand-side primitives once per match.

### 2.11 Concurrency primitives and parallel heuristics (`conc/`, `cr/parallel_heuristics.rs`)

| Primitive | Mechanism | Used by |
|---|---|---|
| `ReadOptimizedLock<T>` (`conc/lib.rs:45-204`) | RCU on an `ArcSwap` token. Reads need no atomic read-modify-write. A writer swaps in a `WriteOngoing` token and waits for readers to drain. | `IndexCatalog`, `ConcurrentVec`, `ParallelVecWriter`, concurrent union-find buffer |
| `ConcurrentVec<T>` | Append-only. Pushes are serialized; readers see `[0, head)`. Mixing `push` with `resize_with` can publish uninitialized slots. | `InternTable`, `NotificationList` |
| `ParallelVecWriter<T>` | `fetch_add` reserves disjoint ranges; growth takes the lock. | Parallel inserts |
| `NotificationList<K>` | A per-id `AtomicBool` (Relaxed load, then swap), plus a `Mutex<SmallVec>`. `reset` must not run concurrently with `notify`. | Merge scheduling |
| `ResettableOnceLock<T>` | `UnsafeCell` + `Once`. `reset(&mut)` re-arms it. | Cached indexes |
| `Notification` | One-shot event. | Blocking hand-off to the index-build pool |
| `BitSet`, `SharedArena`, concurrent UF | Unused by core-relations. | n/a |

**Thresholds.** Every one also requires more than one thread.

| Heuristic | Threshold |
|---|---|
| `parallelize_db_level_op` (rule sets, `run_on_tables`, `merge_all` strata) | `total_size_estimate > 10000` |
| `parallelize_table_op` (insert, delete, rehash) | `> 20000` pending rows |
| `parallelize_index_construction` | `> 20000` rows |
| `parallelize_rebuild` | `> 10000` |
| Containers | intra-environment `> 1000`, inter-environment `> 1` |

**Sources of nondeterminism.**
- `SegQueue` push order.
- Fresh-id minting across batches.
- The order DashMap materializations are converted.
- The racing intern of a container.
- Hash iteration order, which affects the order of action application.

---

## 3. egglog-bridge, and what the frontend assumes

### 3.1 Function tables

| Item | Value | Reference |
|---|---|---|
| Columns | `[k₀…kₙ₋₁, ret, ts, subsume?]`. `func_cols = schema.len()`, `ts_col = func_cols`, `subsume_col = func_cols+1`. | `eb/lib.rs:1533-1618` |
| `subsume` column | Present only when `can_subsume`: constructors, relations, and `term_constructor` views (`fe/lib.rs:806-809`) | |
| Subsume values | `SUBSUMED = 1`, `NOT_SUBSUMED = 0`; merging two rows takes the `max` | `1527-1531` |
| Constructor | `DefaultVal::FreshId` + `MergeFn::UnionId` | `fe/lib.rs:789-850` |
| Custom function | `DefaultVal::Fail`, with `AssertEq` unless a `:merge` expression is given | |
| Relation | Desugared into a constructor whose output is a fresh, non-unionable sort | `fe/ast/desugar.rs:396-427` |
| Global | A function with no keys, i.e. a single-row table with `n_keys = 0` | |
| The `$uf` table | The first table created, as a `DisplacedTable`. The id counter starts at 0 and the ts counter at 1. | `eb/lib.rs:140-189` |

### 3.2 Merge functions

`MergeFn` is resolved once into a reference-free tree, `ResolvedMergeFn` (`eb/lib.rs:1158-1246`).

**The table callback** (`eb/lib.rs:1059-1101`):
1. `out_ret = run(cur.ret, new.ret, ts = new.ts)`.
2. `out_sub = max(cur.sub, new.sub)`.
3. `changed = out_ret ≠ cur.ret || out_sub ≠ cur.sub`.
4. If changed, the output is the *new* row with `ret` and `sub` overwritten.

**Cases of `run`:**
- `Const` returns the constant. `Old` returns `cur`, `New` returns `new`.
- `AssertEq` panics if `cur ≠ new`, through the side channel and early stop, and returns `cur`.
- `UnionId`: if `cur ≠ new`, stage `$uf [cur, new, ts]` and return `min`. *This must match union-by-min.*
- `Primitive`: evaluate the arguments, call the primitive with its **Write**-context id (§3.7), and panic on `None`.
- `Function(f, args)`: if `cur == new`, short-circuit. Otherwise call `lookup_or_insert`, which mints for a
  constructor and reads for a custom function.

**Dependencies** (`fill_deps`, `1033-1057`):
- `Primitive` and `UnionId` add a write dependency on `$uf`.
- `Function(f)` adds a read and a write dependency on `f`.

**When the output differs from the incoming row** (this matters for the parallel bug):
- non-selection primitives (`+`, `set-union`, `set-intersect`, clamps);
- `Function` merges;
- `Const c ≠ new`;
- changes to the subsume column.

`min`, `max` and `or` select one of their inputs, so their output equals the incoming row whenever they change
anything.

### 3.3 Rule lowering (`eb/rule.rs`)

**Table atoms.** `query_table` turns `entries` into `entries ++ [fresh ts var, subsume?]` (`349-444`). Because every
bridge atom carries a ts variable, the bridge never builds a zero-variable atom.

**LHS primitives.**
- `query_prim` (`448-470`) lowers to an RHS `External`, plus `AssertEq` when the result variable is already grounded.
- So LHS primitives are filters evaluated *after* the join, once per match of every variant.
- A `None` result silently drops the match.

**Head actions:**
- `lookup(f, keys)` becomes `LookupOrInsertDefault(ret = IncCounter(id) or Const, ts = next_ts, sub = 0)` for
  constructors and constants, or `LookupWithFallback(…, lazy_panic)` for custom functions.
- `set` becomes `Insert(keys ++ [v, next_ts, 0?])`.
- `union` becomes `Insert($uf, [a, b, next_ts])`.
- `remove` becomes `Remove`.
- `call_external_func` becomes `ExternalWithFallback(f, args, panic)`.
- `subsume` is a lookup that may insert a subsumed row, then a `Lookup` of the subsume column, then an `InsertIfEq`. If
  the key is absent, the lane halts (`475-522`).
- `panic` becomes `External(panic)`.

**Internal helpers:**
- `lookup_uf` is `LookupWithDefault($uf, [x], x, col 1)`.
- `check_for_update` is `AssertAnyNe`.
- `rebuild_row` is `Remove` followed by `Insert`.

### 3.4 Semi-naive variants (`eb/rule.rs:779-846`)

| Case | Variants added |
|---|---|
| Not semi-naive, or no atoms and `mid == 0` | One variant with no extra constraints. A seminaive rule with only an RHS therefore fires exactly once. |
| `sole_focus = f` | One variant, with `GeConst(ts_f, mid)` |
| Otherwise | For each focus i: `(i, ts ≥ mid)` and `(j < i, ts < mid)`. When `mid == 0`, only i = 0 is generated. |

The variants partition the tuples that contain at least one new atom. Primitive atoms do not take part.

### 3.5 Iteration and rebuild loop (`eb/lib.rs:587-682`)

`run_rules_inner`:
1. Set `T = next_ts`.
2. Record the union-find length.
3. `run_rules_impl(T)`.
4. Check the panic channel.
5. If the union-find did not grow, `inc_ts` and return.
6. Otherwise run the native rebuild loop (§1.2 G) and check the panic channel again.

`IterationReport.changed` reflects only the rule set's `merge_all`. It does not reflect changes made by rebuild.

The bridge also has a rule-based rebuild path (`683-926`): per-column incremental rules with the `$uf` atom as sole
focus, and a non-incremental rule. It is **unreachable**, because `DisplacedTable` always provides a rebuilder.

### 3.6 Writes outside rules

- `add_values` and `add_term` (`330-385`) stage rows stamped with the current `ts`, then call `flush_updates`.
  `flush_updates` runs `merge_all`, then `inc_ts`, then a rebuild if the union-find grew (`948-959`).
- `add_term` canonicalizes only its *return* value. `lookup_id` does not canonicalize its key.
- `with_execution_state` exposes a raw `ExecutionState`.
- `TableAction` (`1251-1371`) and `UnionAction` hold only ids and never canonicalize. Their methods are `lookup`
  (committed read), `lookup_or_insert` (`predict_val`), `insert`, `remove`, and `subsume`. `subsume` reads, and panics
  if the key is missing.

### 3.7 Primitives and capability contexts

**Registration and the four contexts.**
- A user primitive implements one of four traits. Each is registered once per context it is valid in
  (`register_per_context`, `fe/typechecking.rs:349-375`):

| Trait | Contexts registered |
|---|---|
| `PurePrim` | all 4 |
| `WritePrim` | Write, Full |
| `ReadPrim` | Read, Full |
| `FullPrim` | Full |

- Every primitive defined through `add_primitive!` is Pure.

**Choosing the context.**
- Inside rules, `BackendRule` uses Pure on the LHS and Write on the RHS for semi-naive rules. Non-semi-naive rules use
  Read on the LHS and Full on the RHS (`fe/lib.rs:2432-2451`).
- Merge expressions always use **Write**. Writes are drained by the merge fixpoint, but reads would go untracked.
- One-shot commands (`eval_actions`, `eval_resolved_expr`) use Full. `check` uses Read.
- The scheduler's query rule is always semi-naive, so it uses Pure. Its action rule is always non-semi-naive, so it uses
  Full. Both ignore `:naive`.

**Wrappers** (`fe/exec_state.rs`):
- Every wrapper can intern containers, read base values, trigger an early stop, and dispatch to higher-order functions.
- `Read` adds committed `lookup` by table name and `table_size`.
- `Write` adds `insert` (it appends `read_counter(ts)` and `NOT_SUBSUMED`), `remove`, `subsume`, `union`, and `panic`.
- Dispatching a higher-order call (`apply_table_function`) checks the *stamped* context, not the wrapper type:
  - a constructor minted in Write or Full;
  - a custom function read in Read or Full;
  - anything else returns `None`.
- So a Pure `unstable-app` that runs at a Write call site can mint constructor rows.
- Crate code reaches the raw `ExecutionState` from any wrapper.

**Id values.**
- Any primitive whose output sort is an eq sort, a container or a function returns an Id. Container primitives intern on
  every call. Accessors such as `vec-get` return stored element Ids.
- Ids returned during a run are canonical with respect to the snapshot. Unions staged in the same run apply only after
  the rebuild.

**Cost.** `RegistryPrimWrapper::invoke` takes an `RwLock` on every call (`fe/typechecking.rs:103-110`).

### 3.8 Frontend: core IR, schedules, scheduler, push/pop

**Core IR** (`fe/core.rs`). Facts are flattened into atoms: a primitive's last argument is its output. Actions are in
SSA form. `canonicalize` substitutes equalities. `remove_dup_vars` deduplicates atoms by functional dependency. When not
in term mode, `(union v (Ctor args))` is rewritten as a `Set` (`707-742`).

**Schedules** (`fe/lib.rs:965-1121`):
- `run` / `repeat` / `saturate` / `seq` call `step_rules`, which calls `run_rules(ids)`.
- `updated` is `IterationReport.changed`.
- `check`, `eval_actions` and `eval_resolved_expr` build a temporary non-semi-naive rule, run it once, and free it.

**Scheduler** (`fe/scheduler.rs:105-397`). A scheduled rule becomes two rules:
- a query rule whose body calls `CollectMatches(free vars)`;
- an action rule that queries the "decided" table, then runs the actions, then removes the decided row.

`filter_matches` chooses which matches go into the decided table, and the rest is kept for later. When a query rule is
skipped, its `last_run_at` does not advance.

**push/pop** (`fe/lib.rs:704-728`). `push` stores `self.clone()`, so the *original* object stays live. `pop` restores
the stored clone but keeps `overall_run_report` and `symbol_gen`.

| Copied (rolled back on pop) | Shared by `Arc` (survives pop) |
|---|---|
| Tables (rows, hash, offsets; cached indexes deep-copied into new locks; the clone gets an empty `rebuild_index` and tracker) | `NotificationList` |
| `DisplacedTable` (its staged unions are *dropped* in the clone) | Base-value intern storage (but `type_ids` is copied) |
| Counters: **ids and timestamps are re-minted after pop** | Panic side channel; `ActionRegistry` |
| Containers (dashmap deep copy) | `Arc<CachedPlan>`, `Arc<MergeFn>` (immutable) |
| Rules, including `last_run_at`; the free lists of rule and external-function ids | Scheduler match buffers. These are shared only *partly*, which is an egglog bug: the clone's `CollectMatches` deep-copies its buffer, so after pop the collector and the reader diverge. |

After pop, egglog's id and timestamp numbering is an order-preserving renaming of what store-global counters would
produce. Golden tests should therefore compare results modulo renaming.

### 3.9 What the frontend and bridge assume of the engine

1. **Jacobi semantics.** Writes are invisible until the rule set ends. The predicted cache and container interning are
   the only exceptions.
2. **Set semantics.** Actions are idempotent, and match multiplicity matters only for `num_matches` reports.
3. **Union-by-min** agrees with `UnionId`'s `min` and with container merges.
4. **Rebuild before reads.** Rules see canonical rows, provided INV holds (§2.3).
5. **The ts counter changes only between runs.** Every row written in a run gets T, and each table's sort column stays
   monotone.
6. **Cached plans are planned once**, using the statistics at the rule's first run, often when tables are empty.
7. **Merge functions are lattices** (documented, not enforced). A change re-stamps the row; no change creates no delta.
8. **Panics** set a side channel and the stop flag. `run_rules` returns `Err` *after* the merge, so partial effects are
   kept.
9. **Each atom has a ts variable**, which keeps the planner and executor away from the zero-variable paths.
10. **Primitive capability contexts** (§3.7). Container interning is global.
11. **push/pop is clone semantics** (§3.8).

---

## 4. Colored contexts

### 4.1 Semantics and the spike

**Target semantics.** These are the spike's README semantics.
- A context sees every row asserted in it or in any ancestor, rewritten to its own canonical ids and closed under
  congruence.
- Its merges and rows are invisible to its parent and its siblings.
- It stores only what differs from its parent.
- The parent may keep changing after children exist. Children catch up on their next rebuild, and reading a context
  that is behind throws.
- The children of a frozen context may run concurrently, one thread per child.

**The spike's mechanism** (`Eggnog.Storage/Context*.cs`, `RowSet.cs`):
- Ids are global to the store, and so is the `long` clock, advanced with `Interlocked`.
- The root union-find is an array. A child's union-find is a `Dictionary` keyed by parent-canonical ids.
- Unions use `min` (`Context.Maintenance.cs:189, 214-216`).
- On a union, *both* the winning and the losing class are marked (`MarkMerged(ra) | MarkMerged(rb)`, `:211`).
- Every parent row that mentions a marked class, or whose key the child holds, is copied in, rewritten, and hidden from
  the child.
- Visibility is checked per row and per level, with hash probes (`Context.Views.cs:147-163`).
- Each ancestor keeps an explicit log of `LogEntry(IsUnion, Table, Slot, Loser)` while it has descendants.
- Each `RowSet` holds `ulong[]` cells, a `long[]` stamp per slot, a `bool[]` alive flag, a key `HashSet<int>`, a `uses`
  index (`Dictionary<id, List<slot>>`), and column indexes built on demand.
- Compaction runs only when no log entry is unread and at least half the slots are dead.
- `Freeze` builds the indexes and disables path compression.
- Merge functions must be lattices.
- Deletion is not supported.

### 4.2 Cross-cutting decisions

**C1. Identity: one store-wide id counter and one clock (keep the spike's choice).**
- Ids come from one atomic counter for the whole store, so ids minted in different contexts never collide. This is
  required for lifting, and it differs from egglog's push/pop, which re-mints ids.
- Timestamps come from one atomic clock for the whole store.
- **Each context captures its own epoch.** At the start of a rule run, and at the start of each rebuild pass, the
  context reads the clock once. Every write in that run or pass, including writes made by merge functions, is stamped
  with that captured value. The context increments the store clock when the run or pass ends.
- Without this rule, concurrent siblings that increment the clock would put several timestamps into one merge batch.
  That breaks the sort-column invariant (serial path) and the one-value-per-merge rule (parallel path).
- Per-context sequences remain monotone, because each capture is at least the previous one.
- Base-value interning is global and concurrent, as in egglog. Values interned by a child that is later disposed simply
  leak, which is harmless.

**C2. Layering the union-find.**
- Use union-by-min at every level. Then the canonical id of a class in context C is `min` over the members of that
  class in C. A class the child never touched has the same canonical id in parent and child, which is what makes reading
  through to the parent possible. `UnionId`'s `min` stays correct.
- The root union-find is egglog's `DisplacedTable`: an array plus a displaced log. Fully compress the newly displaced
  ids at the end of each merge, and compress the whole array at `Freeze`, so that concurrent readers usually need one
  hop.
- A child's union-find is a `Dictionary<uint, uint>` overlay, `parent-canonical → child parent`, holding only the
  classes the child merged. It also keeps the child's own displaced log `(loser, ts)` and a lookup table, so that it is
  exposed as a `DisplacedTable` layer.
- `find_C(x) = local(find_P(x))`. Optionally use the alternating chase `y = find_P(y); y = local[y]; repeat`, which
  tolerates overlay *targets* that the parent later displaced. Cost is O(depth) dictionary probes. The child never
  writes into the parent's array.
- **Catch-up re-keys the child's union-find.** For each id d that the parent displaced since the child's cursor:
  - if d is an overlay key, remove it and re-union `find_P(d)` with its old target in the child. This may add a child
    displaced row;
  - if the child's own displaced log contains d, hide that row, because the parent's row wins.
  The spike regroups the child's union-find the same way (`Context.Maintenance.cs:130-180`).

**C3. Row identity and visibility.**
- **A view is a stack of layers**: each ancestor's own rows, then the context's own rows. Each layer is an
  egglog-shaped `SortedWritesTable` with its own *layer-local* RowIds, version, timestamp runs and indexes.
- **Layered subsets.** The engine `Subset` becomes a small array of per-layer subsets, indexed by depth. A root run has
  one layer and is exactly egglog.
- Two alternatives were considered and rejected:
  - *Virtual concatenated RowIds* with per-run segment bases. Probe results from per-layer indexes are still several
    pieces, each with its own bias. Persistent structures must use local ids anyway. And the bases shift whenever the
    parent grows.
  - *64-bit (layer, row) ids.* These double subset memory and buy nothing over per-layer arrays.
- **Hidden sets generalize tombstones.** For each (context, table, ancestor layer), keep a set of hidden local RowIds:
  - parent rows that mention an id displaced in this context, which are copied in rewritten;
  - parent rows whose key this context holds;
  - parent rows this context retracted (C10).
- Represent a hidden set as a sorted `int[]` while small and as a bitset once large. Allocate it only on first hide.
- A row of ancestor layer L is visible to viewer V iff it is not tombstoned in L, and it is in none of the hidden sets of
  the levels between L and V. Merge those sets once per run when more than one level hides the same layer.
- **Visibility is checked on every path that yields rows**: scans with or without constraints, index builds, `refine`,
  probe results, header subsets, point lookups, and zero-variable atoms. This closes the egglog stale hole in the same
  stroke.
- `has_invisible(layer)` = the layer has tombstones, or some level hides rows of it. This replaces egglog's
  `has_stale_rows` fast path. Do not rely on the column-0 sentinel for hidden rows: a hidden parent row is fully intact,
  so a missed check binds a well-formed but wrong row.
- **Mark only the losers.** Rewriting is needed only for rows that mention a *displaced* id, which is egglog's rebuild
  model. Rows that mention the winner are unchanged. A key collision with a rewritten copy is found by the parent-hash
  probe in the merge path (C4).
- Marking only losers copies fewer rows than the spike, which marks both classes. In exchange, a probe for a canonical
  value must filter hidden rows rather than skipping the parent outright. A canonical value in a child is always
  parent-canonical, so probing the parent with it is valid.

**C4. Lookups and merges in a child.**
- **Lookup of key k:**
  1. Probe the child's own hash. A hit on a tombstone means "absent".
  2. Otherwise probe each ancestor, from nearest to root. Skip a row hidden at any intermediate level.
  3. Because the key is canonical in the child, it is also a valid parent key.
- `get_row`, `predict_val`, the `Lookup*` instructions and merge-function lookups all go through this one routine. It
  costs one hash probe per level on a miss.
- **Insert of incoming row q with key k:**
  - If the child holds k, merge into the child's row.
  - Else, if a visible ancestor row r has key k, compute `merge(r, q)`. If it returns true, append the output to the
    child's layer and hide r. If it returns false, store nothing: r stays visible, which is how "stores only what
    differs" is achieved.
  - Else append q.
- The `ExecutionState` passed to the merge function must be bound to the child, so that unions it stages go into the
  child's union-find layer.
- **Delete of key k:** tombstone the child's own row, or hide the ancestor row (C10).

**C5. Rebuild in a child.** Use egglog's incremental path; never the full-scan path over ancestor layers.
- The displaced delta is the child's own unions since its last rebuild, plus the parent displaced ids found by
  catch-up, plus the losers of re-keying.
- Rows the child owns that mention these ids are found through the child's `rebuild_index`. Stage `remove` plus `insert`
  of the canonical row with the child's epoch, as egglog does.
- Ancestor rows that mention *child-displaced* ids are found through **the ancestor's `rebuild_index`**. Hide each one,
  and stage its rewritten copy with the child's epoch.
- The ancestor's `rebuild_index` must be complete and safe to share. Build it at `Freeze`. A parent that is not frozen
  refreshes it before its children rebuild; this is safe because such children run sequentially with the parent.
- Merging gives congruence, and `UnionId` unions go into the child's union-find. Loop until nothing changes, exactly as
  the bridge does (§3.5).

**C6. Indexes per context, and invalidation.**
- Each layer has its own catalog of `ColumnIndex`/`TupleIndex`, keyed by local RowIds and refreshed from its own
  `TableVersion`.
- Hidden sets are *not* index content. They are filtered at probe time like tombstones, so a hide never invalidates an
  index.
- A frozen ancestor's indexes are built eagerly at `Freeze`, or lazily under a thread-safe once-cell, and are **never
  reset while it is frozen**.
- A context resets only its own index locks, after its own `merge_all`.
- A probe in a child returns per-layer pieces. Each ancestor piece is flagged "maybe invisible", like egglog's
  `PotentiallyStale`, and the child's piece is not.
- Index selection (§2.5) applies *per layer*:
  - an ancestor piece that is dense and covers more than half of that layer uses the ancestor's shared cached index,
    with the dense clamp;
  - the child's small piece uses its own cached index, or a per-subset `SparseColumn` or `Dynamic` index.
- Without this per-layer rule, egglog's logic would fall back to rebuilding a dynamic index over the whole parent for
  every child rule. On a 200k-row frozen root that is not acceptable.
- An ancestor generation bump (compaction or clear) invalidates the child's hidden sets and cursors. See C12.

**C7. Semi-naive across contexts.**
- `last_run_at` is kept per (rule, context).
- A child's watermark for a rule is seeded lazily from the nearest ancestor that ran it, as `min(ancestor W, child
  creation epoch)`. This is rule-layer decision D4.
- This is sound when two conditions hold:
  1. Every row in the child view with `ts < W` is an ancestor row read through unchanged, so any match made only of such
     rows is an ancestor match whose effects the child already sees.
  2. Every row that is new to the child, whether copied, rewritten, merged or caught up, carries a timestamp ≥ the
     child's watermark.
- Condition 2 holds because copy-ins are stamped with the child's epoch, and because ancestor rows appended after the
  child last ran carry a later global timestamp. That in turn holds because a parent that is not frozen does not run
  concurrently with its children.
- A header constraint `GeConst(ts, mid)` becomes one binary search per layer, giving a dense suffix of each layer. The N
  variants are generated as before. Hidden rows are excluded from both the old and the new side, so the partition still
  covers exactly the visible rows.
- This is checked by a differential test: seeded watermarks and watermarks starting from 0 must produce equal views
  modulo renaming.

**C8. Catching up with a changing parent.**
- **The row buffers are the log.** egglog's append-only row storage and its displaced union-find log already record
  everything a child needs, through `updates_since(minor)`. A child keeps one `TableVersion` cursor per (ancestor
  layer, table), including the ancestor's union-find layer. No separate log structure is needed, unlike the spike's
  `LogEntry` list.
- **Catch-up order**, run at the start of the child's `Rebuild`:
  1. Re-key the child's union-find from the ancestor unions since the cursor (C2).
  2. For each ancestor row appended since the cursor that is live and not hidden:
     - if it mentions a child-displaced id, hide it and copy it in rewritten;
     - else if the child holds its key, merge it into the child's row (with lattice merges the order is irrelevant) and
       hide it;
     - else leave it visible as is.
  3. Rewrite the child's own rows that mention newly displaced ids (C5).
  4. Hide and copy the ancestor rows that mention the child's *new* losers, through the ancestor's `rebuild_index` (C5).
  5. Merge to a fixpoint, then rebuild containers (C13).
  6. Advance the cursors.
- Ancestor tombstones need no work in the child. A row the parent replaced shows up as a new append. A row the child
  hid and the parent later tombstoned stays harmlessly hidden.
- Reads throw until catch-up is complete, as in the spike.

**C9. Frozen bases with concurrent children.**
- `Freeze` performs these steps:
  1. Require empty pending queues and a completed rebuild.
  2. Build every `rebuild_index`, the cached indexes that the rule plans name, and exact container `val_index`es.
  3. Compress the union-find.
  4. Make staging into the frozen context throw.
  5. Disable path compression in its union-find.
- Each child owns:
  - its layer tables, union-find overlay, hidden sets and cursors;
  - its notification list, pending queues and dependency-ordered merge;
  - its per-rule watermarks;
  - its predicted caches, stop flag and panic channel;
  - its scheduler state;
  - its trie nodes and pools, per thread.
- Children share: the frozen ancestors, the store counters, the base interner, the schema and dependency graph, and the
  plan templates.
- **Parallelism budget.** When many children run concurrently, egglog's thresholds would oversubscribe the machine,
  because each is gated only on "more than one thread". Inside a child batch, measure work by the child's *delta* size,
  or disable parallelism within the child.

**C10. Deletion, subsumption, clear.**
- egglog uses deletion in four places: user `delete` and `subsume`, `clear_table`, the scheduler's decided-row
  removals, and its internal rewrites (remove plus insert).
- Rebuild rewrites in a child are hide-plus-copy and pose no problem.
- In a child:
  - deleting its own row tombstones it;
  - deleting a visible ancestor row hides it with no replacement;
  - subsume adds an override row with the subsume column set to 1, which is a lattice `max`;
  - `clear_table` sets a per-(context, table) "cleared" flag that hides every ancestor row of that table.
- **An ancestor with live descendants must not retract**, because there is no provenance for the rows children derived
  from it. Such an attempt throws.
- The exception is **context-private tables**, such as scheduler bookkeeping, which descendants never read. Mark these
  in the schema.

**C11. Merge-function discipline.**
- Catch-up reorders merges: the child's override can be merged before or after the parent's later value. The result is
  well defined only for lattice merges (associative, commutative, idempotent).
- Require lattice merges for every table that a context with a changing ancestor writes. This is what the spike already
  requires.
- egglog's `:merge new`/`old`, and arbitrary primitive merges, are allowed only in an "egglog compatibility" mode
  without descendants.

**C12. Compaction.** Keep egglog's trigger (`stale > max(16, n/2)`) and add the spike's gate: a layer whose descendants
have cursors behind it, or hidden sets keyed by its RowIds, does not compact. A frozen layer compacts before it is
frozen. A later alternative is to use the `remap(old, new)` callback that `remove_stale` already reports in order, and
rewrite descendants' hidden sets and cursors through it.

**C13. Containers.**
- Containers over base sorts (`do_rebuild = false`) are context-independent and can stay globally hash-consed.
- Containers over eq sorts depend on the context. A child needs an overlay environment holding these deltas:
  - for `to_id`: new containers, and parent containers that are re-keyed because they mention a displaced id;
  - for `to_container`: child-first lookup by id;
  - for `val_index`.
- The parent's `val_index` must be exact, unlike egglog's, because it finds the containers to copy when the child marks
  an id.
- A collision found by the child merges through the *child's* union-find.
- A `dirty_ids` refresh in the child copies the affected parent rows into the child with the child's epoch.
- Implement this after the core color work. Until then, refuse eq-containers inside children.

**C14. Light children.**
- Create a child's layer of a table lazily, on its first write or hide.
- Give it a single shard: parallel merge pays off only above 20000 rows.
- Build its `rebuild_index` and pending structures only when needed.
- Then a child costs roughly the rows its assumptions touch. The spike's figures are a few milliseconds for 100 merges
  on a 200k-row root, and about 100 ms for 1000 single-merge children run in parallel.

### 4.3 Per-subsystem requirements

| Subsystem | What colors require | Options | Recommendation |
|---|---|---|---|
| Values, ids, counters | Ids unique across contexts; comparable timestamps | Per-context counters with translation; store-global counters | Store-global atomic id counter and clock, plus a per-context captured epoch (C1) |
| Base values | Shared and concurrent | Global; per-context overlay | Global interner, as in egglog |
| Row buffers and tables | Child view = ancestors minus hidden rows, plus own rows | Overlay tables plus hidden sets; copying | Lazy per-context layer tables with egglog layout, plus hidden sets (C3, C14) |
| Union-find | Child equalities invisible upward | Overlay dictionary; full copy | Root `DisplacedTable`; child dictionary overlay plus displaced log; re-key on catch-up (C2) |
| Offsets and subsets | Row sets across layers | Layered arrays; virtual ids; 64-bit ids | Per-layer subsets; a root run has one layer (C3) |
| Indexes | Shared ancestor indexes; per-child deltas | Per-layer catalogs; merged indexes | Per-layer catalogs. Ancestor indexes built at freeze and never reset while frozen. Hides filtered at probe time (C6) |
| Database and `merge_all` | Merge only the context's delta | Per-context database | `Context` owns its notification list, pending queues and merge; the dependency graph is schema-level |
| Planner | Plans shared across contexts | Per-context plans | Plan templates hold no subsets and are shared. Headers are per (context, run). Id constants in plans are parameters canonicalized per context |
| Executor | Layered probes, visibility, per-layer heuristics | — | Per-layer prober pieces; `refine_visible` replaces `refine_live`; outer loop over layers in kernels |
| Actions | Lookups and writes go to the context | Context handle in `ExecutionState` | `ExecutionState` bound to a context; lookups follow C4; `TableAction`/`UnionAction` work unchanged through it |
| Rebuild | Incremental across layers | Full scans; incremental | Incremental only, using ancestor `rebuild_index` (C5) |
| Containers | Context-dependent identity for eq-containers | Global; overlay | Global for base containers; overlay for eq-containers, later (C13) |
| Bridge, rules, seminaive | Per-context watermarks | Start from 0; seeding | Seeded per (rule, context) (C7); per-context scheduler state, panic channel and stop flag |
| Concurrency | Concurrent children of a frozen base | — | Freeze contract (C9); parallelism budget per child |

### 4.4 Comparison with the spike: what to keep

| Aspect | Spike | egglog | Eggnog engine |
|---|---|---|---|
| Cell | `ulong` | `u32` | `uint` |
| Ids and clock | Store-global; `Interlocked` | Per `Database`; counter changes only between runs | **Keep the spike's**, with a per-context captured epoch |
| Row storage | `ulong[]` data, `long[]` stamp per slot, `bool[]` alive | `RowBuffer`, tombstone in column 0, ts column with a run index | egglog's layout, one per layer |
| Key index | `HashSet<int>` with a row comparer | Sharded `(hash, row)` entries, keys compared in place | egglog's |
| Id → rows index | `uses: Dictionary<id, List<slot>>` | `rebuild_index` (`ColumnIndex` over Id columns), refreshed by version | egglog's, deduplicated |
| Union-find | Root array; child dictionary keyed by parent-canonical ids; `min` | `DisplacedTable` | **Keep the spike's layering**, exposed as egglog's table |
| Marking | Both classes | (n/a: rows with displaced ids are rewritten) | Losers only (C3); revisit if probes suffer |
| Visibility | Per-row probes per level | Tombstone check plus `refine_live` | Hidden sets plus tombstone, checked on every path |
| Catch-up | Explicit ancestor `LogEntry` list | (n/a) | Offset cursors into row buffers and the union-find log (C8) |
| Compaction | When no log entry is unread and ≥ half dead | `stale > max(16, n/2)`, bumps the generation | egglog's trigger plus **the spike's gate** |
| Read before rebuild | Throws | (n/a) | **Keep** |
| Freeze | Builds indexes, stops compression | (n/a) | **Keep**, and also build `rebuild_index` and the plan indexes |
| Merge functions | Lattice only | Anything | **Lattice** wherever children write (C11) |
| Deletion | None | Supported | Restricted (C10) |
| Queries | `Scan`/`ScanColumn`/`TryLookup` with `since` | Free Join executor | The executor over layered views |
| Tests | `ReferenceContext` oracle plus random programs (`Pair`) | Unit tests and `.egg` files | **Keep both**, through an adapter (§6) |

**Keep from the spike:**
- the semantics;
- store-global ids and clock;
- union-by-min;
- a dictionary union-find overlay keyed by parent-canonical ids, re-keyed on catch-up;
- copy on touch, hide, and read through;
- throwing until rebuilt;
- the freeze discipline;
- gated compaction;
- lazy per-child cost;
- the oracle and the random-program harness.

**Replace:**
- per-row probes per level, with hidden sets;
- `ulong` cells, with `uint`;
- the separate log, with the row buffers themselves;
- per-slot stamp arrays, with the ts column and its run index;
- `List<int>` id indexes and the `HashSet<int>` key index, with egglog's structures.

---

## 5. .NET port mapping

### 5.1 Rust idioms and their C# equivalents

| Rust (where) | C# |
|---|---|
| `define_id!` `u32` newtypes | `readonly record struct` implementing `INumericId<T>` at API edges; raw `uint`/`int` in hot code and compiled plans |
| `DenseIdMap = Vec<Option<V>>` | `V[]` plus a presence bitmap for struct `V`, or `null` as absent for class `V`. Do not copy egglog's growth on `get_mut` |
| `Vec<Cell<Value>>` row buffer | `sealed class RowBuffer { uint[] Data; readonly int Arity; int Count; }`. Writing a tombstone into a row the writer owns is a plain store. The join of `Parallel.For` provides the happens-before |
| `&[Value]` slices | `ReadOnlySpan<uint>` in hand-written code; `(uint[] array, int offset)` overloads for anything a compiled plan calls |
| `SmallVec<[T; N]>` | Fixed-arity overloads (1, 2, 3 and N keys), struct fields, per-thread scratch arrays. `InlineArray` only outside expression trees |
| `Pooled<T>` plus thread-local `PoolSet` | `ArrayPool<uint>.Shared` for large arrays; `[ThreadStatic]` stacks inside a generic static class (`ObjPool<T>`) for objects; explicit `Return`. Pool only objects above 256 elements |
| `Drop` flushes a `MutationBuffer` | An explicit `Flush`/`Dispose` owned by `ExecutionState`. Never finalizers |
| Trait objects plus `as_any` downcast (`Table`, `WrappedTable`) | Sealed classes. The interpreter switches on a kind enum once per call; compiled code is bound to the concrete type |
| Generic closures (monomorphized scans) | Generic methods over struct type parameters, e.g. `Scan<TV>(…, ref TV v) where TV : struct, IRowVisitor` |
| Enums with data (`Subset`, `DynamicIndex`, `Instr`, `JoinStage`) | A struct with a kind tag and fields, or a sealed class hierarchy with a `Kind` byte switch plus `Unsafe.As` |
| `Option<T>` | `TryX(…, out T)`, or `-1` sentinels |
| Panics in hot paths | Return `false`; egglog-level panics go through a side channel plus the stop flag; exceptions only at API edges |
| `Arc<T>` and `Weak<T>` | References (GC). `Weak<PendingState>` becomes a strong reference |
| `Arc::get_mut(..).unwrap()` before an index reset | A debug lease counter, or a phase assertion |
| hashbrown `HashTable<(hash, row)>` | Custom open addressing with SwissTable control bytes and `Vector128` group probes, storing `(uint hashLo or ulong hash, int row)`. Keys are compared in the row buffer |
| `IndexMap` / `IndexSet` | An insertion-ordered map: an entries array plus an index table. `Dictionary` is fine when nothing is removed |
| `DashMap` | `ConcurrentDictionary`, or striped locks |
| crossbeam `SegQueue` | `ConcurrentQueue<T>` |
| `ReadOptimizedLock` (`ArcSwap`) | A copy-on-write array published with `Volatile.Write`; segmented arrays for growable structures that are read concurrently |
| `Once` / `OnceLock` / `ResettableOnceLock` | A sealed `OnceCell<T>`: a volatile done flag, a lock, and `Reset()` only in the exclusive phase |
| `RwLock<HashMap>` (`TrieNode` children) | A lock plus `Dictionary` (read-mostly), generic over a sync policy struct so serial runs drop the lock |
| `AtomicUsize::fetch_add` | `Interlocked.Increment(ref x) - 1`, which matches Rust returning the previous value |
| `CachePadded` | `[StructLayout(LayoutKind.Explicit, Size = 128)]` |
| `rayon par_iter_mut` over shards; `fold`/`reduce` | `Parallel.For` with `localInit`/`localFinally` |
| `rayon::in_place_scope` plus `spawn` | A custom work scope that *helps while waiting*. No `Task.Wait` inside pool threads, because nested per-block scopes would starve the pool |
| `run_in_thread_pool_and_block` (separate pool) | A dedicated scheduler, or no blocking while holding a once-lock |
| FxHasher (rustc-hash 2.x) | A port of it. Exact parity matters only for golden iteration orders |
| `get_unchecked` | `MemoryMarshal.GetArrayDataReference` plus `Unsafe.Add`, after one explicit range check |
| LSD radix sort of `(value, row)` | Radix sort of packed `ulong (value << 32 | row)`. This also fixes the multi-column ordering defect |

### 5.2 Rules for hot types

- No interfaces, virtual calls, delegates, `try` or `foreach` over an interface inside per-row loops. Generics over
  struct type parameters supply the specialization that Rust gets from monomorphization.
- Every primitive that a plan calls has an overload over arrays and offsets, and is a **non-virtual method on a sealed
  type**. Expression trees cannot use `Span`, `ref struct`, `ref` locals, `stackalloc` or `ref` returns. They *can* use
  `out` parameters through by-ref `ParameterExpression`s.
- Dynamic methods get no tiered PGO. So compiled code must already be monomorphic: concrete sealed classes, fields read
  once into locals, small `[AggressiveInlining]` static helpers. The JIT inlines normal static methods into dynamic
  methods.
- A merge, a rebuild or a compaction can replace an array. Compiled code reloads `Data` once per scan call. That is safe
  because tables do not change during a search.

### 5.3 The JIT-inlinable cursor and index API that compiled plans call

```csharp
public sealed class RowBuffer                 // egglog RowBuffer; fields so expression trees read them directly
{
    public uint[] Data;                        // row r occupies [r * Arity, r * Arity + Arity)
    public readonly int Arity;
    public int Count;                          // physical rows, tombstones included
}

public readonly struct SubsetRef               // not a ref struct; Rows == null means Dense [Start, End)
{
    public readonly int Start, End;            // dense range
    public readonly int[]? Rows;               // sorted row ids; positions [Lo, Hi)
    public readonly int Lo, Hi;
}

public sealed class FunctionTable              // egglog SortedWritesTable, one per (context, table) layer
{
    public RowBuffer Rows;
    public int StaleCount;
    public int FindRow(uint k0);               // -1 if absent; probes the sharded hash table
    public int FindRow(uint k0, uint k1);
    public int FindRow(uint k0, uint k1, uint k2);
    public int FindRow(uint[] key, int offset);         // n_keys > 3
    public bool TryGetColumn(uint k0, int col, out uint value);   // plus 2-, 3- and N-key overloads
    public SubsetRef SortAtLeast(uint ts);     // fast_subset GeConst on the sort column
    public SubsetRef SortBelow(uint ts);       // LtConst
}

public sealed class ColumnIndex
{
    public bool TryGet(uint key, out SubsetRef rows);
    public int KeyCount;
}

public struct ColumnIndexCursor                // external iteration replaces closure-based for_each
{
    public bool MoveNext(ColumnIndex index);
    public uint Key;
    public SubsetRef Rows;
}

public sealed class HiddenSet                  // per (viewer, table, ancestor layer); sorted int[] or bitset
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public bool Contains(int row);
}

public sealed class LayerView                  // one layer of a context's view of one table, fixed for a run
{
    public FunctionTable Table;
    public HiddenSet? Hidden;                  // merged across intermediate levels at run start; null if none
    public bool HasInvisible;                  // Table.StaleCount > 0 || Hidden != null
    public ColumnIndex?[] CachedColumns;       // this layer's catalog, resolved at plan instantiation
}

public static class RowOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVisible(uint[] data, int rowBase, int row, HiddenSet? hidden)
        => data[rowBase] != Value.StaleRep && (hidden == null || hidden.Contains(row) == false);
}
```

The shape of a compiled cover scan. `layers` and `subsets` are run constants, and `Arity`, the columns and the
constraints are baked in as constants:

```
for (l = 0; l < layers.Length; l++):
    lv = layers[l]; data = lv.Table.Rows.Data; s = subsets[l]
    if s.Rows == null: for r in [s.Start, s.End): b = r * ARITY
                         if (lv.HasInvisible && !RowOps.IsVisible(data, b, r, lv.Hidden)) continue
                         <inline slow constraints on data[b + c]>; <bind>; <emit frame (layer l, row r)>
    else:             for k in [s.Lo, s.Hi): r = s.Rows[k]; … same body …
```

Point lookups in a context run C4 as a sealed `ContextTable.TryGetColumn`. It probes the child's own layer, then each
ancestor, then applies the hidden check. Code compiled for the root context binds directly to `FunctionTable`.

### 5.4 Where compiled delegates replace interpretation

| Piece | Interpreter (first) | Compiled (later) |
|---|---|---|
| RHS action programs | Switch per `Instr` per batch; argument gathering specialized by arity, generic over structs | One delegate per `ActionInfo`: straight-line code, the mask held in two `ulong` locals iterated with `TrailingZeroCount`, table handles as constants, known primitives as direct static calls |
| Stage kernels | Switch on stage kind; generic over prober kind | One delegate per (plan, stage, prober-kind pattern), with columns, key positions and arity as constants. **The interpreter keeps `run_plan` recursion, `drain`, frame replay and `sort_plan_by_size`.** A delegate is invoked once per stage per morsel, not once per frame |
| Whole-plan fusion | (n/a) | Only for plans whose order is fixed (one or two stages, single-atom rules, rebuild-style rules) or with dynamic reordering disabled. Cache compiled plans by (plan, order permutation); semi-naive variants give about one or two permutations per focus atom |
| Merge functions | Walk the `ResolvedMergeFn` tree | Compiled once per table into a row-merge delegate with the bridge's `changed` semantics |
| Slow constraints | `Constraint[]` evaluated per row | Inline comparisons inside the scan kernels |
| Planning, header processing, `merge_all`, rebuild, index builds, catch-up | Hand-written C# | Stay hand-written; they are not specific to a rule |

### 5.5 Parallelism

- Port egglog's thresholds as-is, but gate on a configured degree of parallelism and on a per-context work measure (C9).
- Use `Parallel.For` for shard phases: delete, insert, rehash, index build.
- Rule-level and morsel-level hand-off needs a work scope that helps while it waits. Without one, a thread blocked in a
  nested per-block scope starves the thread pool.
- Index builds that run while a once-cell is held must not block on work the waiting thread would have to run.

### 5.6 Determinism

Provide a **deterministic serial mode** for oracles and golden tests:
- plans run in rule order;
- one batch flush at the end;
- pending buffers are drained in staging order;
- optionally, a predicted cache per run instead of per batch, so fresh ids are minted deterministically.

Parallel mode can differ from it only in id choice and match counts, and tests compare modulo renaming.

---

## 6. Build order

Each milestone ends with a green test suite. Tests port the Rust unit tests for the module (`tests.rs`) and add property
tests against a simple reference, as the conventions in `docs/engine/conventions.md` require. The egglog defects in
§8.1 are fixed where they occur, with a comment that cites the Rust file and line.

| # | Milestone | Content | Tested against |
|---|---|---|---|
| M0 | Foundations | Ids, `DenseIdMap`/`IdVec`/`WithReuse`, `FixedBitSet`, FxHasher, `ShardData`, pools, `InternTable`, base-value codecs. Some of this is already in `Eggnog.Relations/Collections`, `Hashing` and `Ids` | Ported `nid`, pool and `base_values` tests; FxHash vectors taken from Rust |
| M1 | Rows and subsets | `RowBuffer`, `TaggedRowBuffer`, `OffsetRange`, `Subset`/`SubsetRef` (intersect, gallop, `iter_bounded`, `retain`, `add_row_sorted`) | `row_buffer/tests.rs`, `offsets/tests.rs`; property tests against `SortedSet<int>` |
| M2 | Union-find | `UnionFind` (min, halving, `find_naive`), `DisplacedTable`, `Canonicalizer` | `ufc/tests.rs`, `uf/tests.rs`; property tests against a naive partition with min-element canonical ids |
| M3 | Function table, serial | Staging, serial merge contract (§2.3), sort runs, `fast_subset`, scans that check visibility on *every* path, point lookups, rehash, clear | `table/tests.rs`; model-based tests against a `Dictionary<key, row>` with fold semantics; regression tests from the stale-hole repros |
| M4 | Indexes | `ColumnIndex`, `TupleIndex`, `SubsetBuffer` arena, incremental and full `Index` refresh, sorted multi-column bulk build, `IndexCatalog`, `OnceCell` | `hash_index/tests.rs`; property tests against a naive group-by over random tables with tombstones and generation bumps |
| M5 | Database and rebuild | Catalog, `process_constraints`, notifications, strata, `merge_all` (always in stratum order), counters, external functions, incremental and full `apply_rebuild`, `refresh_rows_for_values`, root containers | `cr/tests.rs` merge and rebuild tests; `containers/tests.rs`; a debug assertion of INV after every rebuild loop; **the spike's `ReferenceContext` as a naive congruence-closure oracle at the root** |
| M6 | Query and planner | Builders, `FunDeps`, plan types, Gj, Free Join and MinCover (with the empty-bucket fix), tree decomposition (with the lost-constraint fix and an explicit choice on `all_children`) | Golden stage lists for the planner experiments (triangle, path3/4/6, rectangle, star, spider, tree5, `const_repeat`, `join2_unused`, `allconst`); the `lost3` repro; strategy tests from `cr/tests.rs:150-389` |
| M7 | Executor and actions, serial, interpreted | `run_rule_set`, `TrieNode`, frames, probers, the three kernels, `sort_plan_by_size`, leaf scans, materializations, `Bindings`/`Mask`/`Instr`, `ExecutionState`, cached plans | The rest of `cr/tests.rs` and `action/tests.rs`; **a randomized differential test**: random conjunctive queries (constants, repeated variables, tombstones, all strategies, decomposition on and off) against a naive nested-loop join; set equality, and multiplicity checks where defined |
| M8 | Bridge, root only | Function tables, merge functions, rule lowering, semi-naive variants, iteration and rebuild loop, subsumption, containers, panics, scheduler hooks | Ported `eb/tests.rs` (1.5k lines); semi-naive against all-naive on the same programs; random root-level programs against `ReferenceContext`; optionally a test-only `.egg` subset reader compared with `egglog.exe` output modulo id renaming |
| M9 | Colored storage | `Store`/`Context`, global ids and clock with captured epochs, union-find overlay and re-keying, layer tables, hidden sets, copy on touch, cursor catch-up, freeze, compaction gate, dispose | **The spike's `DifferentialTests`, `Harness` and `Reference`, run through an adapter** (below); the spike itself as a second, faster oracle for long programs; the *context = copy* property: a child's view equals a fresh root that replays the assertions of the ancestors and the child |
| M10 | Colored queries | Layered subsets, per-layer indexes and prober pieces, `refine_visible`, per-context headers, seeded watermarks, rules and rebuild in children | Random rule programs run in a child against the same rules on a replayed root (modulo renaming); seeded against unseeded watermarks; egglog push/pop equivalence on `.egg` programs |
| M11 | Parallelism | Parallel delete/insert/rehash/index build/rebuild, rule-level and morsel parallelism, concurrent children of a frozen base | Serial against parallel equality using lattice merges *that change values* (egglog's parallel tests never exercise this branch); `run_serial_and_parallel` above the thresholds at 1 and N threads; stress tests with concurrent children compared to serial runs |
| M12 | Compiled plans | Compiled RHS programs, merge functions, stage kernels, cache of plan permutations | Interpreted against compiled, using the M7/M10 generators; benchmarks |
| M13 | Performance parity | Profiling, SIMD probes, pooling, parallel tuning | `egglog -j1` and `-jN` on `math-microbenchmark`, the herbie repros and eggcc samples; a 200k-row frozen root with 1000 children |

**Reusing the spike's harness.**
- `Pair` drives a store context and a `ReferenceContext` in lockstep. The operations are `Union`, `Insert`, `GetOrAdd`,
  `TryLookup`, `Contains`, `Scan(since)`, `ScanColumn`, `Rebuild`, `Freeze`, `CreateChild` and `Dispose`. Views are
  compared modulo the choice of canonical id, and failing programs print themselves.
- Write a test-only facade that exposes the spike's public `Context` API over the new engine. `ulong` values map to
  `uint`, and `since` maps to the store clock. Then copy `DifferentialTests.cs`, `Harness/` and `Reference/` into
  `Eggnog.Relations.Tests` and switch only the store side.
- `ReferenceContext` replays every assertion of a context and its ancestors into a naive e-graph and closes it from
  scratch. So it serves both as the root oracle (M5, M8) and as the context oracle (M9, M10).
- Extend the generator with deletes once C10 is implemented, keeping the restriction that ancestors with descendants do
  not retract.

---

## 7. The native alternative

The alternative is to keep egglog in Rust behind a flat C ABI shim, called from .NET by P/Invoke (`LibraryImport`,
`SafeHandle`, batched buffers). Callbacks into .NET for primitives and merges would run on rayon worker threads.

### 7.1 Layering without colors: clones

**Mechanism.** egglog's `push` clones the whole `EGraph` (`fe/lib.rs:704-709`). §3.8 lists what the clone copies and
what it shares. For a context-per-query design, every context would be a clone.

**What one clone copies, given the data layout:**
- For every `SortedWritesTable`:
  - the row buffer (4 B × arity × physical rows, *tombstones included*);
  - the sharded hash table (a 16-byte entry plus 1 control byte per bucket, at a load factor of at most 7/8);
  - the offsets;
  - a queue drain and re-push of the pending state.
- Every cached `ColumnIndex`/`TupleIndex`, deep-copied: `IndexMap` entries, `SubsetBuffer` arenas, key buffers. The
  *source's* lock is refreshed first.
- The union-find: 4 B per id up to the maximum id, the displaced log, and the lookup map.
- Every container environment: three DashMaps, each copied shard by shard.

**Deferred costs inside the clone:**
- `rebuild_index` and `subset_tracker` are reset, so the clone's first rebuild is a full rescan of every table, and its
  rebuild index is rebuilt from scratch.
- When a clone is dropped, `Database::drop` broadcasts a pool clear to every rayon thread.

**Estimate for a 200k-row root** (not measured; §7.4 E1 measures it):
- Assume about 10 tables and average arity 5 including ts and subsume.
- Rows come to about 4 MB, hash tables to 5–9 MB, and the cached indexes the rules use to 10–20 MB. The union-find and
  containers add a few MB.
- So one clone is **roughly 20–40 MB and 10–30 ms**. The copy is memory-bandwidth-bound and single-threaded.
- On top of that comes the first full rebuild in the clone.
- 1000 contexts would cost 10–30 s of copying and 20–40 GB if they were alive at the same time. The spike does the same
  in about 100 ms, as a figure for the whole parallel batch.

**Semantic gaps:**
- *Counters are copied*, so ids and timestamps minted in sibling clones collide. Lifting a fact proven in every case to
  the parent is then impossible without a renaming pass.
- A clone is a snapshot. When the parent changes, the clone cannot catch up: it must be discarded and re-cloned.
- Concurrent clones of one frozen root are mostly safe (`&self`, with index locks behind `Once`), but `deep_copy` of a
  non-empty pending queue races.

**Verdict:** layering by clones works only for a handful of large contexts. It does not fit "one context per query, one
per case of a split".

### 7.2 Adding colored contexts to core-relations in a Rust fork

| Subsystem | What the fork must change |
|---|---|
| `table_spec.rs` / `WrappedTable` | A view abstraction over layered tables. Every scan, `refine`, `fast_subset` and lookup becomes layer-aware, and `has_stale_rows` becomes "has invisible rows" |
| `table/` | Layer tables, hidden sets, the merge path against visible ancestor rows, a child rebuild that uses the ancestor `rebuild_index`, catch-up, the compaction gate, and fixes for both parallel-insert defects and the stale hole |
| `uf/`, `union-find` | A child overlay union-find, layered `find` for column 1, re-keying |
| `offsets/` | Layered subsets, or virtual ids, throughout |
| `hash_index/` | Per-layer catalogs, prober pieces, no reset while frozen, the `rebuild_full` ordering fix |
| `free_join/mod.rs` | The `Database` split into a shared frozen base and a context (lifetimes: `Arc<FrozenDb>` plus `&mut Context`), merge per context, notification list per context, counters shared across databases |
| `free_join/execute.rs` | Root nodes over layered subsets, per-layer `get_index` heuristics, `refine_visible`, per-context parallel budgets |
| `free_join/plan.rs`, `query.rs` | Plan templates without data subsets; Id constants as parameters; the decomposition fixes |
| `action/` | An `ExecutionState`/`DbView` bound to a context, layered lookups and `predict_val`, staging into the context |
| `containers/` | Overlay environments, an exact `val_index` |
| `egglog-bridge` | Per-(rule, context) watermarks, a rebuild loop per context, catch-up, per-context scheduler, panic and stop state |

This adds up to most of the 15.4k non-test lines of core-relations plus the bridge. Upstream egglog changes quickly: two
of the defects above are regressions from 2026 commits. A fork this deep could no longer absorb upstream changes, so the
"proven engine" advantage would erode over time.

### 7.3 Boundary design, and where it would be chatty

**Shape of the boundary.**
- Handles: `EGraphHandle`, `ContextHandle` and `RuleHandle` as `SafeHandle` subclasses.
- Calls are `[LibraryImport]` functions with blittable arguments: table ids, `uint*` row buffers with counts, and
  output buffers supplied by the caller.
- Rust panics are caught with `catch_unwind` at every entry point and returned as error codes.
- Callbacks are `delegate* unmanaged[Cdecl]` pointers to `[UnmanagedCallersOnly]` methods, plus a `GCHandle` context.
  They must catch every exception.
- The first reverse P/Invoke on a rayon thread attaches that thread to the CLR. Each later transition costs tens of
  nanoseconds and is a GC suspension point.

| Interaction | Frequency | Chattiness |
|---|---|---|
| Declare tables and rules | Once | Fine |
| `run_rules` / rebuild / one iteration | Per iteration | Fine |
| Bulk add, lookup or scan from the host (extraction, lifting, `check`) | Per command | Fine if batched into caller buffers |
| **Primitive calls** (`External`, called per live lane) | Per match, per primitive | **Chatty.** egglog calls `invoke` once per lane (`cr/free_join/mod.rs:216-253`); a batch callback would need a core change. Arithmetic-heavy rule sets (herbie) make millions of calls |
| **Merge functions** written in .NET (lattices, `ValueSet<T>`) | Per functional-dependency conflict, inside parallel shard merges | Chatty, on rayon threads |
| **Container primitives** | Per call; interning is in Rust | Chatty, or every .NET container type must be reimplemented in Rust as a `ContainerValue` |
| Base values of .NET types (strings, enums, codecs) | Per new value | Moderate. Intern in .NET and pass `uint`, but then every primitive over them is a callback |
| Early stop and panic channel | Rare | Fine |

Rule plans would then run in Rust. The planned .NET expression-compiled plans would not apply. That is not a loss in
raw speed, since Rust is already monomorphized, but Eggnog's rule layer could no longer specialize them.

### 7.4 Recommendation, and the evidence that would settle it

**Recommendation: port to C#**, following the build order in §6. Keep egglog as a *differential oracle* (its CLI on
`.egg` programs, compared modulo renaming), not as a runtime component.

**Why:**
1. Colored contexts are the bulk of the work in either language, and they touch nearly every core-relations module
   (§7.2). The fork's main advantage, reusing a finished engine, mostly disappears.
2. "Faithful" already means departing from egglog in a dozen places (§8.1). A port starts from the corrected semantics.
3. The parts Eggnog's rule layer wants to keep in .NET (lattices, value codecs, primitives, containers, lifting,
   extraction) are exactly the chatty crossings in §7.3.
4. One runtime brings one debugger and one memory model. The spike's xunit oracle and harness carry over directly.
   Expression-compiled plans are an optimization path that only a .NET engine has.

**Against the recommendation, stated honestly:**
- egglog's performance is proven, and so is its parallel engine.
- Reaching parity in C# is real work: GC pressure, bounds checks, no monomorphization through interfaces.
- The port carries the risk of semantic drift. The oracles in §6 exist to control it.

**Evidence that would settle the choice:**
- **E1. Clone cost.**
  - Measure: `push` time and memory on a representative 200k-row root, and the first-rebuild cost inside the clone.
  - Target to compare with: the spike's ~0.1–3 ms per context.
  - What it decides: whether the no-fork native path is viable at all. Expected answer: no.
- **E2. C# kernel performance.**
  - Measure: the M1–M7 interpreted core on three workloads (`math-microbenchmark`, a herbie repro, and a synthetic
    200k-row saturation), against `egglog -j1`.
  - Port wins: within about 2× interpreted, and about 1.3× after M12 compiled kernels.
  - Re-open the fork question: worse than 3× after M12.
- **E3. Callback overhead.**
  - Measure: a rayon worker calling an `[UnmanagedCallersOnly]` .NET primitive once per lane.
  - Multiply the per-call cost by the primitive-call rate of the workloads in E2. If boundary time would exceed about
    20% of run time, the native path needs core changes (batched externals) before it is even comparable.
- **E4. Size of the fork.**
  - Prototype layered lookup and scan with hidden sets in a Rust branch of `SortedWritesTable`.
  - Measure the diff size and conflicts against the last three months of upstream commits.
- **E5. Workload shape.**
  - From the Cosmos prototype: contexts per query, rows touched per context, and the share of time spent in primitives.
  - This tells which of E1–E4 matters most.

---

## 8. Risks and open questions

### 8.1 egglog defects: what the port does instead

| # | Defect | Where | Evidence | Eggnog |
|---|---|---|---|---|
| 1 | Constrained scans skip the tombstone check | `cr/table/mod.rs:429-439`, `1018-1025` (since `6e75cefe`) | Reproduced at HEAD: `check` after `delete`, `:merge new`, subsume, rebuild | Check visibility on every path |
| 2 | Zero-variable atoms check only that the header is non-empty | `cr/free_join/plan.rs:1502-1505` | Core experiment: `T(1,2)` after delete still fires | Explicit check that a visible row exists |
| 3 | Parallel insert discards the merge output | `cr/table/mod.rs:902-907` | Wrong `+`, `set-union` and subsume results at `-j 32` | Store the merge output |
| 4 | Parallel insert reports changes that did not happen | `cr/table/mod.rs:871` | `saturate` with a `:naive` rule never ends | Report real changes only |
| 5 | Decomposed plans can lose a join constraint | `plan.rs:897-904`, `993-995` | `lost3.egg`: false match and false `check` | `Lookup` stage in the result block |
| 6 | `all_children[1..]` orphans `children[0]` | `plan.rs:805` (commit `7f4cbc49`) | 208 of 208 epilogue rules | Iterate all children (decide whether golden-plan parity matters) |
| 7 | Free Join with decomposition panics; MinCover with a zero-variable atom panics | `plan.rs:1344`, `1481-1491`, `1337` | Experiments | Skip variables outside the bag; drop empty buckets |
| 8 | Multi-column `rebuild_full` produces unsorted subsets with missed duplicates | `cr/hash_index/mod.rs:341-382` | Code reading | Sort packed `(value, row)`, then deduplicate |
| 9 | Scheduler residual matches keep non-canonical ids | `fe/scheduler.rs:105-144`, `224-238` | Code path, traced | Canonicalize Id variables on instantiation |
| 10 | After push/pop, the clone's `CollectMatches` diverges from its reader | `fe/scheduler.rs:302-308` | Code reading | Per-context scheduler state |
| 11 | `merge_simple` ignores strata, so a Function merge can read rows not yet rewritten | `cr/free_join/mod.rs:593-605`, `417-427` | Code reading (needs parallel mode) | Always merge in stratum order |
| 12 | `DisplacedTable::clear` keeps the lookup table and keeps the version | `cr/uf/mod.rs:286-289` | Code reading | Clear it and bump the generation |
| 13 | Container `val_index` goes stale after an id-only rename | `cr/containers/mod.rs:553-558`, `618-623` | Code reading | Keep it exact |
| 14 | `get_container` re-enters a read lock | `cr/containers/mod.rs:704-706` | Code reading | Lock once |
| 15 | Wrong parallel threshold for full container rebuild | `cr/containers/mod.rs:526` | Code reading | Intra-environment threshold (> 1000) |
| 16 | `LookupOrInsertDefault` does not set `changed` | `cr/action/mod.rs:707` | Code reading | Set it |
| 17 | Serial `rebuild_incremental` reports a change whenever the subset is non-empty | `cr/table/rebuild.rs:182` | One no-op merge | Report real changes |
| 18 | `Prober::len` ignores `intersect_outer`; `times_refined` is seeded in plan order | `execute.rs:336-344`, `2413` | Heuristics only | Fix; performance only |
| 19 | Subsuming an absent key halts the lane | `eb/rule.rs:477-519` | Code reading | Decide; probably make the inserted row visible to the next instruction |
| 20 | `check` ignores `--no-decomp` and includes subsumed rows | `fe/lib.rs:1478-1510` | Experiment | Frontend concern; note it |
| 21 | `FixedBitSet` pooling is a no-op | `cr/pool/mod.rs` | Code reading | Do not pool bitsets |
| 22 | `run_rules` returns a panic before advancing the timestamp or rebuilding | `eb/lib.rs:597-599` | Code reading: `fail` (`fe/lib.rs:1696-1702`) continues on tables left non-canonical, and the next run reuses the timestamp | Advance the timestamp or rebuild, then throw the first panic; a panic the rebuild records is dropped |
| 23 | Container unions reach the union-find only after the table rebuild has canonicalized rows | `eb/lib.rs:666-668`, `cr/free_join/mod.rs:357-388` | Test: the `:no-merge` scenario in egglog's own comment (`eb/lib.rs:634-665`) still reports an illegal merge | Merge the container unions into the union-find before the table rebuild |
| 24 | `free_external_func` leaves a freed panic in `panic_funcs` | `eb/lib.rs:293-295`, `1482-1491` | Code reading: freed ids are reused, so `new_panic` can return an unrelated function's id | Forget the message when the function is freed |
| 25 | Cached plans bake Id constants: after a constant loses a union, the rule silently stops matching | `eb/lib.rs:1397-1420`, `cr/query.rs` constant entries | Code reading (colored-contexts spec X19) | Canonicalize every Id constant per run in the context that runs it (colors M10.2) |
| 26 | `run_rule_set` returns before `merge_all` when the rule set is empty, yet `run_rules` records the run's timestamp as every rule's `last_run_at` | `cr/free_join/execute.rs:349`, `eb/lib.rs:591-624` | Test (colors M10.3 review): a row staged raw before two runs whose every variant was pruned merges later with its staging stamp, below every watermark, and no rule ever matches it | Merge what is staged at the run's timestamp also when the rule set is empty (colors §10.3) |

### 8.2 Design risks

- **Performance gap.** The C# engine may trail Rust. Mitigations: §5.2 rules, compiled kernels (M12), early benchmarks
  (E2).
- **Dynamic reordering against compilation.** Mid-query re-sorting (`cur % 3 == 1`) prevents fusing a whole plan.
  Per-stage kernels keep it; whole-plan fusion needs a fixed order.
- **Thread-pool starvation.** Nested scopes, one per decomposed block, can starve the pool. A helping work scope is
  required before M11.
- **Soundness of seeded watermarks.** C7's argument depends on every newly visible row being re-stamped. It must be
  proven by the differential property in M10, not assumed.
- **Catch-up cost under a busy parent.** A parent that is not frozen and changes a lot makes every child re-scan its
  appends. The design assumes parents are mostly frozen.
- **Memory with many children.** Hidden sets, layer tables and union-find overlays grow with the rows touched.
  Thousands of live children need the lazy, single-shard layers of C14 and an early `Dispose`.
- **`u32` exhaustion.** Ids and timestamps come from store-global `u32` counters and are never reclaimed. A long-lived
  store with many contexts could exhaust them. Decide early whether a store can be recycled, or whether 64-bit cells are
  worth their cost.
- **Frozen plan statistics.** Plans are fixed at a rule's first run. A child whose statistics differ sharply from the
  root's keeps the root's plan. Runtime reordering mitigates this only partly.
- **Duplicated defects.** Porting blindly would import the defects in §8.1. Each module's review must check that list.

### 8.3 Open questions

1. **Deletion across contexts.** Is "no retraction in an ancestor with live descendants, plus context-private tables" (C10)
   acceptable to the rule layer? Its case splits keep children alive for the length of a `Run`.
2. **Non-lattice merges.** Is egglog's `:merge new`/`old` needed anywhere outside a compatibility mode at the root?
3. **Which classes to mark.** Only losers (C3, less copying) or both classes as in the spike (simpler probes)? Decide by
   measuring on M10 workloads.
4. **Determinism.** Is deterministic id assignment required, for golden tests or for reproducible extraction ties?
   If so: a predicted cache per run, staging-order merges, and deterministic parallel modes.
5. **Fresh-id scope.** egglog mints different ids for the same new term in different batches, and the merge then unions
   them. Should Eggnog share predictions per run (fewer unions, different numbering) or match egglog?
6. **Match-count parity.** Must `num_matches` agree with egglog? It is multiplicity-dependent and differs between plans.
7. **Plan parity.** Reproduce egglog's plans exactly, including the `all_children` quirk and the `times_refined`
   seeding, so that performance comparisons are fair? Or fix them?
8. **Dynamic reordering when compiled.** Keep it, by compiling per stage, or drop it for fused plans?
9. **Containers in children** (C13). Are eq-containers needed in contexts in v1, or can they wait until after M10?
10. **The rule-based rebuild path.** Is it worth porting as a cross-check, or dropped like egglog drops it in practice?
11. **`IterationReport.changed`.** It ignores changes made during rebuild. Is that the intended stopping criterion for
    `saturate`?
12. **Primitives and capability contexts.** Keep egglog's four contexts, and the rule that the call site's stamped
    context decides higher-order dispatch? Or adopt the rule layer's own model?
13. **Replanning.** Should a rule be replanned when sizes drift, or when it first runs in a context whose statistics
    differ greatly from the root's?
