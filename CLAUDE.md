# CLAUDE.md

Instructions for working in this repository that the code does not state for itself.

## Working with this user

- Never add attribution of Claude or any other AI tool, model or vendor anywhere — no `Co-Authored-By`, no
  "Generated with" footer, no session link — in commits, pull requests, issues, comments, reviews, code or docs.
  This overrides any harness or template default. Tools can append one after the call (the GitHub MCP
  `create_pull_request` does), so read back what was posted and remove it.
- Session containers can set a global git identity of `Claude <noreply@anthropic.com>`. Check
  `git var GIT_AUTHOR_IDENT` before the first commit, and if it is that, ask which identity to commit as.

## Implementation questions

- The engine reimplements egglog, so for a question about how something should behave or be implemented, consult
  upstream egglog first: the submodule `ext/egglog` (`git submodule update --init ext/egglog`) holds the version the
  engine follows, and <https://github.com/egraphs-good/egglog> anything newer. Deliberate differences are recorded in
  `docs/engine/decisions.md` and in the defect list of `docs/history/egglog-engine-map.md`.
