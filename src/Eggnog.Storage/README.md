# Eggnog.Storage

The storage layer of an e-graph engine with **contexts**: one base of facts, and any number of views over it
that each add assumptions — merges of classes, rows — visible only to themselves and their descendants. It is
the "colored e-graph" idea, built for the case where a base (a schema's compiled theory) is shared by many
short-lived contexts (one per query, one per case of a split).

It is only storage: union-find, tables with functional dependencies and merges, congruence closure, and
incremental scans. Parsing, rules, scheduling and extraction belong to layers above it.

## Model

- A **`Store`** holds the tables and a tree of **`Context`**s rooted at `Store.Root`.
- A **table** is a relation (a set of rows) or a function (one output per key). An `Id` output is a
  *constructor*: two rows whose keys become equal have their outputs merged, which is congruence. A `Value`
  output is combined by the table's merge function, which must be a lattice operation (associative,
  commutative, idempotent).
- A **context** sees every row asserted in it or any ancestor, rewritten to its own canonical identifiers and
  closed under congruence. Its merges are invisible to its parent and siblings.
- Writes leave work pending; **`Rebuild()`** does it. Reads throw until the context, and everything an
  ancestor changed since, has been rebuilt. Ancestors may change after children exist; children catch up on
  their next rebuild.
- **`Freeze()`** makes a context read-only. Children of a frozen context can be used concurrently, one thread
  per child.

```csharp
var store = new Store();
var and = store.DefineTable(TableSchema.Constructor("And", ColumnKind.Id, ColumnKind.Id));

var schema = store.Root;
// ... compile the schema's facts into the root ...
schema.Freeze();

using var query = schema.CreateChild();          // the query's assumptions
using var whenOrder = query.CreateChild();       // one case of a split
whenOrder.Union(uuidComparison, stringComparison);
whenOrder.Rebuild();
// in whenOrder, everything built from the two comparisons is equal; in query and schema, nothing changed
```

## How a context stays cheap

A context stores only what differs from its parent:

- **Its union-find maps the parent's canonical identifiers to its own.** The root uses an array; a context
  uses a dictionary holding only the classes it merged.
- **A parent class this context merged with another is *marked*.** Every parent row mentioning a marked class
  is copied in, rewritten, and the parent's copy is hidden from this context. So is any parent row whose key
  this context holds a row for.
- **Everything else is read through** from the parent unchanged, because an unmarked class has the same
  canonical identifier in both.

So a context costs roughly the rows its assumptions touch. On a 200,000-row frozen root, a context with 100
merges builds in a few milliseconds, and a thousand single-merge contexts in parallel take about 100 ms.

To catch up with an ancestor, a context reads the log the ancestor keeps while it has descendants: classes it
merged away, rows it added or changed. An ancestor merge may make two of the context's parent identifiers
one, which **re-keys** its union-find; an ancestor's new row is copied in if this context marks one of its
classes or holds its key.

Each rebuild discards the log entries every descendant has taken in. Once none are left unread, nothing names
a row by its slot, and a table at least half full of dead slots — rows replaced because a merge changed their
key — is compacted. A descendant that is never rebuilt holds both back; disposing it releases them.

## Testing

`Eggnog.Storage.Tests` checks the store against an oracle, `ReferenceContext`, that replays every assertion of
a context and its ancestors into a naive e-graph and closes it from scratch. Random programs — nested contexts,
ancestors changing under children, frozen roots, disposal — run against both, and the views are compared modulo
the choice of canonical identifier, along with lookups, column scans, and incremental scans. 400 programs run by
default; set `EGGNOG_STORAGE_SEEDS` for a longer soak. A failing program prints itself.

## Not yet

- **No deletion.** Rows cannot be removed or subsumed.
- **Scans of a deep context check every ancestor row** against each level below it, with a couple of hash
  lookups per level. Cheap at the depths a split produces.
- **No lifting.** Moving a fact proven in every case of a split up to the split context is the engine's job, on
  top of this.
