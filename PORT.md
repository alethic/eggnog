# Port Progress

Eggnog is a C# port of the Rust [egg](https://github.com/egraphs-good/egg)
library (e-graphs good), an e-graph library optimized for equality saturation.

This file tracks the progress of porting egg to C#. The upstream source is
vendored as a git submodule at [`ext/egg`](ext/egg) and pinned to a specific
commit; see `git submodule status` for the exact revision being ported.

## Status legend

- ⬜ **Not started** — no C# code yet
- 🟨 **In progress** — partially ported
- ✅ **Done** — ported, with tests passing
- ⏭️ **Skipped** — intentionally not ported (see notes)

## Modules

Listed roughly in dependency order (leaves first). Source paths are relative
to [`ext/egg/src`](ext/egg/src).

| Source | C# target | Status | Notes |
| --- | --- | --- | --- |
| `lib.rs` (`Id`) | | ⬜ | The `Id` newtype over `u32` and the crate prelude/re-exports. |
| `util.rs` | | ⬜ | Misc utilities (e.g. `HashMap`/`HashSet` aliases, `JoinDisp`). |
| `unionfind.rs` | | ⬜ | Union-find data structure. |
| `sexp.rs` | | ⬜ | S-expression parser used by patterns/language parsing. |
| `language.rs` | | ⬜ | `Language`, `ENode`, `FromOp`, `RecExpr`, analysis traits. |
| `subst.rs` | | ⬜ | `Subst` and `Var`. |
| `eclass.rs` | | ⬜ | `EClass`. |
| `machine.rs` | | ⬜ | Virtual machine for pattern matching. |
| `pattern.rs` | | ⬜ | `Pattern`, `PatternAst`, `ENodeOrVar`, `SearchMatches`. |
| `multipattern.rs` | | ⬜ | Multi-patterns. |
| `rewrite.rs` | | ⬜ | `Rewrite`, `Searcher`, `Applier`, `Condition`. |
| `extract.rs` | | ⬜ | Cost models and `Extractor`. |
| `explain.rs` | | ⬜ | Proof/explanation machinery (large). |
| `egraph.rs` | | ⬜ | `EGraph`, the core data structure (largest module). |
| `run.rs` | | ⬜ | `Runner`, `Scheduler`, equality saturation loop. |
| `dot.rs` | | ⬜ | Graphviz `Dot` output (`std`-only feature). |
| `lp_extract.rs` | | ⬜ | ILP-based extraction (`lp` feature). Optional. |
| `macros.rs` | | ⬜ | `define_language!` etc. — needs a C# equivalent strategy. |
| `test.rs` | | ⬜ | Test helpers (`std`-only). |
| `tutorials/` | | ⏭️ | Rust doc tutorials; not required for the port. |

## Integration tests

Ported from [`ext/egg/tests`](ext/egg/tests). These are the acceptance bar for
the corresponding feature areas.

| Source | Status | Notes |
| --- | --- | --- |
| `simple.rs` | ⬜ | Minimal end-to-end example. Good first target. |
| `math.rs` | ⬜ | Symbolic math with a numeric/constant-folding analysis. |
| `prop.rs` | ⬜ | Propositional logic. |
| `lambda.rs` | ⬜ | Lambda calculus with substitution; exercises explanations. |
| `datalog.rs` | ⬜ | Datalog-style rules. |

## Open questions / decisions

- **`define_language!` macro** — Rust's declarative macro has no direct C#
  equivalent. Decide between source generators, a fluent/registration API, or
  hand-written language types.
- **Optional features** — `lp` (ILP extraction) and `serde` serialization are
  feature-gated in Rust; decide whether they are in scope for the initial port.
