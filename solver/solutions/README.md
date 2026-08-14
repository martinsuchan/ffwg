# Improved solutions

Solutions found by the solver that are **shorter than the one bundled in
`legacy/solution/`**. One file per level, in the game's own format:

```lua
saved_moves = 'uuuuuulllDDDRRR...'
```

Same flat symbol string the game records and replays (docs/021), so a file here
can be dropped straight into the browser port or checked with:

```
dotnet run --project ..\src\FishFillets.Cli -c Release -- verify <level> --moves <string>
```

`legacy/` is reference material and is never edited — that is why improvements
land here instead. `ffsolve improve <level> --out solutions\<level>.lua` writes
them.

Every file here has been replayed end to end and reaches *Solved*; the
`improve` command refuses to emit a solution that doesn't.

See [`../docs/003`](../docs/003-2026-08-14-window-optimizer.md) for how they were
found and how they compare to the hall of fame (`../docs/worldfame.lua`).
