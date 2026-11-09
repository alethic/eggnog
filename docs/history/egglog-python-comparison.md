# egglog-python and Eggnog's API

A comparison of the public API of [egglog-python](https://github.com/egraphs-good/egglog-python) 14.0.0 (PyPI,
November 2026) with Eggnog's, and what Eggnog takes from it. egglog-python binds the Rust egglog engine through PyO3
and lowers Python declarations to egglog commands; Eggnog reimplements the engine in C# and has no text front end. The
semantics are egglog's in both; the shape of the API differs. The items Eggnog takes are in the roadmap's "Library
work".

## Side by side

The README's quick start, in egglog-python (run against 14.0.0; it extracts `Math.var("x") + Math(5)`):

```python
class Math(Expr):
    def __init__(self, n: i64Like) -> None: ...
    @classmethod
    def var(cls, name: StringLike) -> Math: ...
    def __add__(self, other: Math) -> Math: ...
    def __mul__(self, other: Math) -> Math: ...

@ruleset
def arith(a: Math, b: Math, x: i64, y: i64):
    yield rewrite(a + b).to(b + a)
    yield rewrite(a * Math(1)).to(a)
    yield rewrite(Math(x) + Math(y)).to(Math(x + y))

egraph = EGraph()
e = egraph.let("e", Math.var("x") * Math(1) + (Math(2) + Math(3)))
egraph.run(arith.saturate())
egraph.check(eq(e).to(Math(5) + Math.var("x")))
egraph.extract(e)
```

| Topic | egglog-python | Eggnog |
|---|---|---|
| Declarations | A subclass of `Expr`; its methods are its constructors. The table's kind is inferred from the signature: a `Unit` result makes a relation, a value result a function, the sort's own result a constructor, and a body an eager primitive or a rewrite. | Explicit: `DefineSort<T>`, `DefineRelation`, `DefineFunction`, `DefineConstructor`, each returning a typed handle. |
| Terms | Python expressions: `a + b` builds `Add` through operator overloading; `int` and `str` convert (`i64Like`, `converter`). | `add.Of(a, b)`; constants convert implicitly to `Term<T>`; no operators. |
| Values | Wrapper classes (`i64`, `String`, `BigRat`, `Map[...]`), and `PyObject` for any Python object. | .NET types (`long`, `string`, `BigInteger`, `Set<T>`, `Vec<T>`); others through `ValueStore.Register<T>`. |
| Variables | `vars_("a b", Math)`, or a `@ruleset` generator's parameters. | `r.Vars<Expr, Expr>("a", "b")` in a rule builder. |
| Rules | `rule(*facts).then(*actions)`, `rewrite(x).to(y, *when)`, `birewrite`, `eq(a).to(b)`, `ne`, `set_`, `union(a).with_(b)`, `delete`. | `When`, `Where` (a lambda filter too), `Then`, `Rewrite`, `DefineBiRewrite`, `Is`, `IsNot`, `Fact.Exists`; host code through `Compute.Of`, `ThenCall` and `ThenCallFull`. |
| Merges | An expression: `merge=lambda old, new: old.max(new)`. | A `ValueLattice` or a `FunctionMerge`. |
| Schedules | `run(n)`, `run(rs)`, `.saturate()`, `* n`, `+` and `seq`, `run(rs, fact)`, `back_off`; a default ruleset. | `Schedule.Run`, `Sequence`, `Repeat`, `Saturate`, `Until`, `RunWith`, `Split`; `RunOptions` limits and cancellation. |
| Writes | `register(actions)`, `let(name, expr)`. | `Make` (a typed class), `Assume`, `Union`, `Delete`, `Subsume`, `Write` batches. |
| Reads | `check`, `check_fail`, `.value`, `lookup_function_value`, `function_values`, `match` on expressions. | `Check`, `Get`, `TryGet`, `AreEqual`, `Find`, `Rows`, `CountRows`; queries (`DefineQuery`) that deliver bindings. |
| Extraction | A Python expression, usable again; tree or greedy-DAG; `TreeCostModel` and `DagCostModel` over any ordered cost; `set_cost`, `extract_multiple`, `keep_best`. | An `ExtractedTerm` (head, arguments, class and cost per node, typed `TryMatch`, s-expression), which converts to no `Term`; `CostModel` of head, row and value costs summed as `ulong`; `ExtractVariants`; a reusable `Extractor`; no DAG extraction. |
| Push and pop | `with egraph:`, one stack. | A tree of scopes: cheap children, freeze, threads, tags, local rules, scoped reads. |
| Case splits | None. | `Schedule.Split` and lift. |
| Threads | Access to one `EGraph` is serialized; parallel work uses separate graphs. | A frozen scope is read from any number of threads, each with children of its own. |
| Text | `as_egglog_string` replays a session in egglog; the bindings parse egglog. | None (the roadmap's "Closed"). |

Only egglog-python also has: inline visualization (`display()`; Eggnog's `Serialize().ToJson()` gives the visualizer's
input), method bodies as default rewrites, `PyObject`, the Array API layer, low-level proof commands, and features of
egglog after `5e655cae` (greedy-DAG extraction, `keep_best`, `Maybe` and `catch`, `map_fold_kv`, `Rational`).

## What Eggnog takes

1. **One term type.** egglog-python's `extract` returns an expression of the kind the host wrote, which it registers
   again or destructures with the same calls as any expression (`get_callable_args(expr, Math.__add__)`, `match`).
   Eggnog's `ExtractedTerm` destructures (`TryMatch` against a constructor handle) but converts to no `Term`, and the
   reading API is its own: `Term` has almost none. What sets it apart is two facts about one extraction, each node's
   cost under the cost model and its class in the scope; neither belongs on the term. Wanted: `Extract` returns a ground
   `Term<TSort>` in a result that holds the cost and per-subterm cost and class lookups, the read side moves to `Term`,
   and `ExtractedTerm` goes.
2. **A transcript as egglog text.** `EGraph(save_egglog_string=True)` records the session as `.egg` source the egglog
   binary runs. Emitting egglog is not the closed front end, which parses it, and Eggnog already renders terms as
   egglog does. egglog-python's rule for what egglog cannot express carries over: once a session does something
   egglog cannot say (a C# lambda in `Where` or `Compute`, a callback, a split, scopes beyond one push and pop chain),
   the transcript is invalid and reading it throws. It turns a host's program into one the differential tests and the
   egglog binary can run, and an upstream report into a file.
3. **A page from egglog's commands to the API.** egglog-python's translation guide shows each command above its Python
   form. Eggnog's mappings are spread through `architecture.md`, written for maintainers; most of its users know egglog
   first. One table: `datatype`, `function :merge`, `rewrite :when`, `run-schedule`, `push` and `pop`, `check`,
   `extract`, and so on.
4. **One declaration per constructor.** In Python the signature is the declaration, the operator and the static type.
   Eggnog's sort struct, `DefineConstructor` call and handle are three things. A source generator over a declaration
   such as `[Constructor("Add")] static partial Term<Expr> Add(Term<Expr> a, Term<Expr> b);` gives one, and keeps
   trimming and NativeAOT whole. C# 14's extension operators let a host write `a + b` over `Term<Expr>`; the library
   builds with C# 13, so for now that is a documented pattern for hosts.
5. **Cost models beyond a `ulong` sum.** A `TreeCostModel` receives a node and its children's costs and returns any
   ordered cost; a `DagCostModel` with greedy-DAG extraction counts a shared subterm once. A callback over a node and its
   children's costs covers costs of several dimensions, or depth. DAG extraction is egglog's after `5e655cae`, and is
   taken up with [`../egglog-3.0.0-todo.md`](../egglog-3.0.0-todo.md).

## What Eggnog does not take

- **Inferring a table's kind from its signature.** egglog-python needs a seven-row table to say which signature lowers to
  what. Eggnog's explicit definitions stay.
- **Global contexts.** egglog-python's experimental `set_array_api_egraph` affects every thread at once; scopes are
  Eggnog's form of a context.
- **Global names for values.** `let` names a value across the graph; Eggnog's typed classes need none.
