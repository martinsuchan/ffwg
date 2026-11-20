# Solver-found solutions

Solutions the solver produced and that are worth keeping: **shorter than the one
the game shipped with**, or the solver's own independent answer for a
level it solved outright (`gems`, matching the hall of fame). One file per level,
named after the **real** level even when the search ran on a hand-simplified copy
— the move string is only kept here once it has been replayed against the real
one. In the game's own format:

```lua
saved_moves = 'uuuuuulllDDDRRR...'
```

Same flat symbol string the game records and replays (docs/021), so a file here
can be dropped straight into the browser port or checked with:

```
dotnet run --project ..\src\FishFillets.Cli -c Release -- verify <level> --moves <string>
```

`ffsolve improve <level> --out solutions\<level>.lua` writes them. Once the user
asks, a shorter one is also promoted into `legacy/solution/<level>.lua`, where the
web sandbox replays it. The game's original then moves to `legacy/solution/old/`.
`Corpus.TryReadOriginalSolution` still reads the original from there: the
results' `bundled` column and the tests pinned to the original move strings use
it.

Every file here has been replayed end to end and reaches *Solved*; the
`improve` command refuses to emit a solution that doesn't.

See [`../docs/003`](../docs/003-2026-08-14-window-optimizer.md) for how they were
found and how they compare to the hall of fame (`../docs/worldfame.lua`).
