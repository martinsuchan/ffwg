# 077 - Slim CLAUDE.md: nested per-tree files, history moved to docs

2026-11-15

## Why

The root `CLAUDE.md` had grown to 129.8 kB / 1,443 lines (recommended: under 200 lines),
and Claude Code loads it whole into **every** session. That was an estimated 32-40k tokens,
about half of what a fresh session loads on top of the harness's own cached prompt (the first
turn was 94-99k tokens). 122 kB of it was one section, 97 bullets summarizing docs/006-076,
duplicating this log, even though the file said `docs/` is the source of truth. It also never
mentioned `solver/`, where the most recent long session (1,184 turns, 4 compactions) did all
its work.

## What was done

- **Root `CLAUDE.md` → 3.6 kB / 61 lines:** repo layout (now including `solver/`), where
  status lives, commands for both trees, and conventions. It now has an explicit rule:
  milestone summaries never go into a CLAUDE.md.
- **Nested files, loaded on demand** (Claude Code loads a subdirectory's CLAUDE.md the first
  time it reads or edits a file in that tree, so they cost nothing elsewhere):
  - `web/CLAUDE.md` (8.2 kB): goal, source map, architecture decisions, and a list of
    invariants/gotchas distilled from the old bullets, each citing its docs/NNN.
  - `legacy/CLAUDE.md` (5.2 kB): the old "Legacy game" section, verbatim.
  - `solver/CLAUDE.md` (2.6 kB): new, condensed from `solver/README.md`.
- **No content lost:** the 97 bullets moved verbatim to `docs/HISTORY.md`. Some carry
  same-day follow-ups that only exist there.
- **`docs/README.md` has a one-line-per-entry index**, generated from each entry's title.
  `scripts/new-doc.ps1` now appends the new entry to the index automatically.
- `@path` imports were rejected as a way to split the file: Claude Code expands them at launch,
  so they save nothing.

## Result

A session that touches only the solver loads about 6 kB of instructions instead of 130 kB.
Web work loads about 12 kB (root + web), and about 17 kB once it also reads `legacy/`.

## Open for next time

- Keep the nested files to stable facts. If `web/CLAUDE.md` grows past ~200 lines, move
  topic-specific parts into `.claude/rules/*.md` with `paths:` frontmatter, which also load on
  demand.
- Per-project context that isn't in the repo is a user-level choice. Unused claude.ai
  connectors can be turned off for this project via `/mcp` (stored per project in
  `~/.claude.json`). For synced `anthropic-skills:*` skills, the docs don't confirm a
  project-only switch; try `/skills` (writes `skillOverrides` to
  `.claude/settings.local.json`) and check the result.
- The savings above are estimated, not measured. Confirm them with `/context` in a fresh
  session.
