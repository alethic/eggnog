# Eggnog.Relations

The relational engine under [Eggnog](https://www.nuget.org/packages/Eggnog): a C# reimplementation of egglog's
`core-relations` (tables with functional dependencies and merges, hash indexes, union-find and rebuilding, Free Join
query planning and execution), its bridge and its extractor, plus colored contexts, which egglog does not have.

This package exists as Eggnog's dependency. Its types are internal: build theories, run rules and extract terms with
the `Eggnog` package, which is the supported API.
