# 027 — Low-level performance and memory pass

A profiling-driven pass over the main A* loop: where the time and the memory go,
which low-level C# constructs (`Unsafe`, `MemoryMarshal`, bit packing) pay for
themselves, and which do not. Every change leaves the search itself untouched:
expansions, stored states and the bound `f` are identical in every benchmark,
and the regression suite is green (1046 pass; the same 9 `KnownBug` cells fail
as before).

The machine is an ARM64 Snapdragon X Elite laptop, which matters twice. SIMD
here means NEON through `Vector128`, not AVX. And measurements are noisy (see
"How it was measured").

## Where the time goes

`dotnet-trace` (sampled thread time), society, serial, 1.5 M expansions:

| | before |
|---|---|
| `StateStore.FindOrAdd` | 14 % |
| `Room.RestoreMobile` (key + snapshot) | 17 % |
| `ComputeFall` + `PrepareRound` + `Mask`/`Unmask` | 30 % |
| `Encode` + `WriteStateKey` + `Decode` | 10 % |

The GC is not a cost. Pauses are about 0.3 % of the run (331 ms in 99 s at
10 M expansions). Memory is the cost: the nightly batch of 2026-11-18 ended 7
of its 10 unsolved levels on the RAM-sized node cap, not the 30-minute limit.

## What changed

- **`RestoreMobile(key)` re-stamps only movers.** The grid depends on positions
  alone, so a model whose position matches the key keeps its cells. That is
  the same argument the snapshot overload already used. Consecutive pops are
  near relatives, so most models stay put.
- **`IsOnPad` without the resist frame.** "Is any pad below fixed" needs no
  de-duplicating frame. With the whole shape inside the room, only the shape's
  lower edge can see another model (`Shape.BottomX/BottomY`, precomputed).
  Outside the room every mark is still probed, since out-of-range reads as the
  border.
- **`Mask`/`Unmask` without bounds checks.** Per-model cell offsets are
  precomputed. A shape whose bounding box lies inside the room is stamped with
  `Unsafe.Add` from the grid's `GetArrayDataReference`, with no per-cell test;
  anything else takes the old checked path.
- **State hash.** FNV-1a (a multiply per byte) is replaced by 8-byte words with a
  MurmurHash3 finaliser. Micro-benchmark: 23 -> 8.4 ns on society's key,
  409 -> 30 ns on gems.
- **Store lookup.** Chain links and keys are read through unchecked refs. Keys
  of 8-16 bytes compare as two overlapping `ulong` loads instead of
  `SequenceEqual`.
- **Bit-packed state key** (`StateKeyCodec`). A position is one number: 0 for
  gone, else 1 + the anchor's index in a box one shape wider than the room on
  every side. Fish and plug flags take 7 bits above it. Slots are written back
  to back.
  - Key size: society 24 -> 16 B, library 22 -> 16 B, gems 182 -> 115 B.
  - Identical items are sorted on those numbers. That is also what made gems's
    encode 4.4x faster (88-member class).
  - Decode divides by multiplying (Lemire's exact 32-bit fastdiv).
  - The invariant checks still throw, out of line.
- **`BucketQueue` on pooled 4 KB blocks.** A `List<int>` bucket doubled by
  copying: at tens of millions of entries it briefly held old and new arrays,
  and handed the old one to the GC. Blocks are now recycled through a pool as
  buckets drain, and nothing is ever copied. FIFO order per bucket is unchanged.

## Measured

**Memory** (society, 10 M expansions, serial; in-process harness):

| | before | after |
|---|---|---|
| allocated in total | 1,119 MB | 870 MB (-22 %) |
| live heap at the end | 670 MB | 526 MB (-21 %) |
| peak working set | 738 MB = 54.9 B/state | 584 MB = 43.5 B/state (-21 %) |

`bench --suite` B/state: start 39 -> 34, society 48 -> 38, library 43 -> 37,
cannons 65 -> 52. So a RAM-capped run holds about 20-25 % more states.

**Speed**, original against final. Interleaved A/B, process wall time
(startup included), 8 rounds serial and 6 rounds on 12 threads:

| level | serial, min | serial, median | 12 threads, min |
|---|---|---|---|
| wreck | 1.18x | 1.18x | 1.06x |
| society | 1.09x | 1.09x | 1.08x |
| library | 1.13x | 1.13x | 0.96x |
| cannons | 1.06x | 1.06x | 1.22x |

Step by step (serial `bench --suite`, each against the one before):

- restore + `IsOnPad`: +4-10 %;
- `Mask`/`Unmask`: +6-17 %;
- hash: +3-11 %;
- bit-packed key: **-0-7 %** (interleaved A/B: wreck +2 %, library 0 %,
  cannons -5 %, society -7.5 %);
- store lookup: noise.

The packed key is a deliberate trade: up to 7 % speed for 11-18 % memory, on a
solver whose long runs stop on memory.

On 12 threads the gain is small and noisy. The parallel search is bounded by
its serial merge (phase 2, docs/019), and these changes mostly speed up phase 1.

## Tried and dropped

- **Branch-free bit packing** (one unaligned 64-bit store per field, no
  per-byte loop). Micro-benchmark: encode 120 -> 134 ns. The loop was not
  mispredicting; reverted.
- **Moving the codec's throws out of line**: no change on its own. It is kept
  for tidiness.
- **Look-ahead "touch" in phase 2.** Loading the bucket and chain head of the
  child 8 ahead stands in for a prefetch, since .NET has no prefetch intrinsic
  on ARM64. No measurable gain on 12 threads; reverted.

## How it was measured, and why it matters

- **`bench --suite` alone cannot resolve under ~10 % here.** The same binary pair
  swung from 1.13-1.18x to 0.82-0.97x between two consecutive minute-long runs.
  The causes are a laptop on the Balanced power plan, frequency scaling, and an
  unrelated process holding one core. Decisions above were made on
  **interleaved A/B** runs (A, B, A, B, ... per level, min and median), never on
  two suite runs taken minutes apart.
- **Micro-benchmarks used BenchmarkDotNet** (0.14) in a scratch project, not in
  the repo. Its `MemoryDiagnoser` confirmed every codec and hash path allocates
  nothing. It settled questions the suite could not, notably what the packed
  key costs per call and that the branch-free variant was slower.

## Open

- **BenchmarkDotNet in the repo.** A `benchmarks/` project over the hot
  primitives (codec, hash, store, `Mask`, `ComputeFall` on a fixed room) would
  pin their cost the way `micro-baseline.csv` pins node counts. Internal types
  need `InternalsVisibleTo` for it.
- **An A/B command.** `bench --ab <exeA> <exeB> --rounds N` in the CLI, so the
  interleaved method is not a scratch script.
- **Phase 2 is the parallel ceiling.** Sharding the store by hash would
  parallelise it, at the cost of the exact serial order and the identical-counts
  test (docs/019).
- **Stuck-detector garbage**, about 24 B per stored state on society: layout keys
  and verdicts evicted by `TrimIfFull`. Harmless for the GC as measured, but it
  is now the largest allocation source left.
- **`StateStore` bucket growth** still needs old + new bucket arrays at once,
  about 1 GB extra for an instant at 2^28 states.
