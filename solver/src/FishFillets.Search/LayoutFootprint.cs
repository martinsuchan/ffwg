using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// The cells an event could not have happened the same way without: where
/// everything that moved was, where it ended up, what it passed through on the
/// way, and where the acting fish stood before and after - each with the cell
/// directly below it.
///
/// <para>This is the rule <see cref="LayoutSolver"/> stands on: <b>a fish that
/// stands clear of an event's footprint does not matter to it.</b> Wherever it
/// waits outside, the same key press from the same layout gives the same layout,
/// so the waiting fish's position can stay deferred (a field) rather than be
/// decided at every event. The rule is a claim about the physics, not something
/// derived from it, so the micro suite checks it in every reachable state
/// (<c>MicroChecks.Footprint</c>) through this very code.</para>
///
/// <para><b>Why each part.</b></para>
/// <list type="bullet">
/// <item>An item's cells before and after, and every cell it swept through: a
///   fish there would have blocked the push or been hit by the fall. A push
///   moves an item one cell sideways and gravity can only take it down after
///   that, so a move with both components is swept as that L - across at the
///   old height, then down.</item>
/// <item>An item that left the room is swept from where it stood to the border,
///   both in the push direction and straight down, since nothing records which
///   way it went.</item>
/// <item>The actor's cells at both ends: the other fish cannot overlap them.</item>
/// <item>The cell below covers the interactions that work by contact rather
///   than by overlap: a fish directly under something that moved was (or
///   became) its support, or is landed on, and an item pushed onto a fish's
///   back kills it.</item>
/// </list>
///
/// <para><b>Why only below.</b> The margin was all four neighbours. That
/// forbade a fish standing <i>beside</i> where an item lands, and on
/// <c>start</c> the small fish has to wait right beside where the big fish
/// drops a block: the optimum was out of reach (56 instead of 54). Shrunk to
/// the cell below, the property check still holds on every micro-level and
/// along the recorded solutions of wc, cannons, start, submarine, noground
/// and broom; with no margin at all it fails on five micro-levels. Placements
/// beside, above or in the before cells of a moved item were also measured on
/// four corpus levels to give no different outcome that can still be
/// finished.</para>
/// </summary>
internal sealed class LayoutFootprint
{
    private readonly Level _level;
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _raw;
    private readonly int[] _done;
    private int _generation;
    private int _rawCount;
    private readonly int[] _interactionDone;
    private readonly int[] _underDone;
    private int _interactionGeneration;
    private readonly List<int> _interaction = [];
    private readonly List<int> _under = [];

    public LayoutFootprint(Level level)
    {
        _level = level;
        _width = level.Width;
        _height = level.Height;
        _raw = new int[_width * _height];
        _done = new int[_width * _height];
        _interactionDone = new int[_width * _height];
        _underDone = new int[_width * _height];
    }

    /// <summary>
    /// The last <see cref="Compute"/>'s raw (undilated) cells that a moved item
    /// covers <b>after</b> the event or <b>sweeps</b> on its way there - every
    /// position of its shape except the one it started from. Each cell once.
    ///
    /// <para>This is where another fish standing can turn an event into a
    /// <i>different</i> one that can still be finished: stopping a fall, or a
    /// push, part-way. Measured over every clean generic outcome on wc,
    /// cannons, noground and broom, every different solvable outcome came from
    /// a placement intersecting these cells, and none from one only under,
    /// over or beside the moved items, in their before cells, or touching only
    /// the actor; <c>MicroChecks.Footprint</c> pins that on the micro-levels.
    /// <see cref="LayoutSolver"/> tries the idle fish there for an
    /// "interaction" child.</para>
    /// </summary>
    public IReadOnlyList<int> InteractionCells => _interaction;

    /// <summary>
    /// The last <see cref="Compute"/>'s cells directly under a moved item where
    /// it came to rest, not covered by any <see cref="InteractionCells"/>. A
    /// fish there gives no <i>different</i> outcome (measured, and pinned by
    /// <c>MicroChecks.Footprint</c>) but can be where the item lands on it and
    /// it survives - wc's bowl comes down onto the small fish's back while also
    /// resting on walls, the layout the same as without the fish. The generic
    /// child forbids that square; <see cref="LayoutSolver"/> tries it for a
    /// committed child with the same layout.
    /// </summary>
    public IReadOnlyList<int> UnderCells => _under;

    /// <summary>
    /// Writes the footprint of one event into <paramref name="destination"/>
    /// (cleared first), each cell once, as <c>y * width + x</c>.
    /// </summary>
    /// <param name="after">The room as the event left it.</param>
    /// <param name="items">The mobile non-fish models.</param>
    /// <param name="before">Where each of <paramref name="items"/> was before the key press, and whether it was already gone.</param>
    /// <param name="actor">The fish that pressed the key.</param>
    /// <param name="actorX">The actor's anchor before the key press.</param>
    /// <param name="actorY">The actor's anchor before the key press.</param>
    /// <param name="dx">The key's direction.</param>
    /// <param name="dy">The key's direction.</param>
    public void Compute(
        Room after, ReadOnlySpan<int> items, ReadOnlySpan<(int X, int Y, bool Gone)> before,
        int actor, int actorX, int actorY, int dx, int dy, List<int> destination)
    {
        destination.Clear();
        _generation++;
        _rawCount = 0;
        _interaction.Clear();
        _under.Clear();
        _interactionGeneration++;

        for (int i = 0; i < items.Length; i++)
        {
            (int bx, int by, bool wasGone) = before[i];
            if (wasGone)
            {
                continue;
            }

            int item = items[i];
            Shape shape = _level.Models[item].Shape;
            ref readonly ModelState state = ref after.State(item);

            if (state.IsOut || state.IsLost)
            {
                SweepOut(shape, bx, by, dx, dy, bx, by);
                SweepOut(shape, bx, by, 0, 1, bx, by);
                if (dx != 0)
                {
                    SweepOut(shape, bx + dx, by, 0, 1, bx, by);
                }

                continue;
            }

            int ax = state.X, ay = state.Y;
            if (ax == bx && ay == by)
            {
                continue;
            }

            // Across at the old height, then down (or up) at the new column.
            int stepX = Math.Sign(ax - bx);
            for (int x = bx; ; x += stepX)
            {
                AddShape(shape, x, by, interaction: x != bx);
                if (x == ax)
                {
                    break;
                }
            }

            int stepY = Math.Sign(ay - by);
            for (int y = by; ; y += stepY)
            {
                AddShape(shape, ax, y, interaction: ax != bx || y != by);
                if (y == ay)
                {
                    break;
                }
            }

            // Where the item comes to rest on whatever is below it.
            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                int cx = ax + shape.MarkX[m], cy = ay + shape.MarkY[m] + 1;
                if ((uint)cx < (uint)_width && (uint)cy < (uint)_height && _underDone[(cy * _width) + cx] != _interactionGeneration)
                {
                    _underDone[(cy * _width) + cx] = _interactionGeneration;
                    _under.Add((cy * _width) + cx);
                }
            }
        }

        // An under cell the item itself covers is not under it.
        int kept = 0;
        for (int i = 0; i < _under.Count; i++)
        {
            int cell = _under[i];
            if (_interactionDone[cell] != _interactionGeneration)
            {
                _under[kept++] = cell;
            }
        }

        _under.RemoveRange(kept, _under.Count - kept);

        Shape fish = _level.Models[actor].Shape;
        AddShape(fish, actorX, actorY);

        ref readonly ModelState actorState = ref after.State(actor);
        if (actorState.IsOut || actorState.IsLost)
        {
            AddShape(fish, actorX + dx, actorY + dy);
        }
        else
        {
            AddShape(fish, actorState.X, actorState.Y);
        }

        // The margin: each raw cell and the one below it. Stamps are per call,
        // so every cell is written once however many shapes covered it.
        _generation++;
        for (int r = 0; r < _rawCount; r++)
        {
            int cell = _raw[r];
            int x = cell % _width, y = cell / _width;
            Emit(x, y, destination);
            Emit(x, y + 1, destination);
        }
    }

    /// <summary>Where a model that has left the room sits (<c>Room.ChangeRemove</c>): off the grid, covering nothing.</summary>
    private const int OffGrid = -1000;

    /// <summary>
    /// The other half of the rule: whether, in the outcome <paramref name="after"/>
    /// holds, something stays up only because <paramref name="fish"/> stands at
    /// (x, y). No footprint marks that - an item kept up by the idle fish does
    /// not move - and it is real: on <c>submarine</c> the big fish is what keeps
    /// a pipe off the small one while the small one pushes elsewhere (micro-level
    /// <c>support-bound</c>). An outcome this is true for depends on where the
    /// fish stood, so the solver commits the fish there, and the property check
    /// never takes such an outcome as its reference.
    ///
    /// <para>Coarse first (anything mobile on the fish at all, the actor not
    /// counted), then exact: the engine asked whether the room would still be
    /// settled with the fish gone. "Gone" is the fish moved off the grid, where
    /// it covers no cell: nothing rests on it there, and the border it would
    /// see above itself counts as wall, so no load is put on it either.</para>
    /// </summary>
    public static bool NeedsIdleSupport(Room after, InertRouter router, int fish, int x, int y, int actor) =>
        router.IsHoldingSomethingUp(after, fish, x, y, actor) && after.WouldDisturbWithFishAt(fish, OffGrid, OffGrid);

    /// <summary>The shape translated step by step until every cell has left the room.</summary>
    private void SweepOut(Shape shape, int x, int y, int dx, int dy, int bx, int by)
    {
        if (dx == 0 && dy == 0)
        {
            AddShape(shape, x, y, interaction: x != bx || y != by);
            return;
        }

        for (int k = 0; k <= _width + _height + shape.W + shape.H; k++)
        {
            int cx = x + (k * dx), cy = y + (k * dy);
            if (!AddShape(shape, cx, cy, interaction: cx != bx || cy != by))
            {
                return;
            }
        }
    }

    /// <summary>Marks the shape's in-room cells at this anchor; false if none was in the room.</summary>
    private bool AddShape(Shape shape, int x, int y, bool interaction = false)
    {
        bool any = false;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
            {
                continue;
            }

            any = true;
            int cell = (cy * _width) + cx;
            if (interaction && _interactionDone[cell] != _interactionGeneration)
            {
                _interactionDone[cell] = _interactionGeneration;
                _interaction.Add(cell);
            }

            if (_done[cell] != _generation)
            {
                _done[cell] = _generation;
                _raw[_rawCount++] = cell;
            }
        }

        return any;
    }

    private void Emit(int x, int y, List<int> destination)
    {
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            return;
        }

        int cell = (y * _width) + x;
        if (_done[cell] != _generation)
        {
            _done[cell] = _generation;
            destination.Add(cell);
        }
    }
}
