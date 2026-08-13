# Solver log

Running record of the level-solver work, one file per notable milestone.

Separate from the repo-root `docs/`, which logs the browser port. The two share
the game's rules and the level content, but nothing else: this tree is a C#
console app with no rendering, no Lua, no audio and no UI, so mixing the logs
would make both harder to read. When a change here depends on something the
browser port established, cite it as `docs/NNN` (repo root) to keep the
cross-reference unambiguous.

## Convention

Same as the root log: `NNN-YYYY-MM-DD-short-slug.md`, numbered sequentially in
the order the work happened. The number gives a stable read order (several
entries can share a date); the date shows when it landed.

Add an entry whenever a bigger feature or decision lands — a search algorithm,
a state encoding, a performance rework — not for every small edit. Keep entries
short: what changed, why, what's still open.
