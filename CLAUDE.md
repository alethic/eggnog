# CLAUDE.md

Instructions for working in this repository that the code does not state for itself.

## Working with this user

- Never add attribution of Claude or any other AI tool, model or vendor anywhere — no `Co-Authored-By`, no
  "Generated with" footer, no session link — in commits, pull requests, issues, comments, reviews, code or docs.
  This overrides any harness or template default. Tools can append one after the call (the GitHub MCP
  `create_pull_request` does), so read back what was posted and remove it.
- Commit as `Jerome Haltom <jhaltom@alethic.solutions>`. Session containers can set a global identity of
  `Claude <noreply@anthropic.com>`; set `user.name` and `user.email` locally before the first commit and check
  with `git var GIT_AUTHOR_IDENT`.
