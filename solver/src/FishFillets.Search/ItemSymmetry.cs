using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Which mobile items are interchangeable: physically identical, so that two
/// states differing only in which of them stands where are the same state
/// (docs/022).
///
/// <para>Every model has its own slot in the state key, so a room whose two
/// identical gems have traded places is stored twice and searched twice. On
/// most levels identical items never pass one another and it does not happen;
/// on gems 79% of the states plain A* stored were such duplicates, party1 14%.
/// Sorting the positions within each class of identical items makes the key
/// canonical and merges them.</para>
///
/// <para><b>Identical</b> means everything the physics reads from a model's
/// definition: kind, shape (the same marks in the same order), weight, power,
/// declared facing; and no goal, no plug, not a fish. Frozen models are not in
/// the mobile set at all. That the physics then really does not care which of
/// them is which - for instance that the order a round processes models in
/// never decides an outcome between two identical ones - is checked directly,
/// state by state, by the micro-level and fuzzer check <c>SymmetrySound</c>.</para>
/// </summary>
internal static class ItemSymmetry
{
    /// <summary>
    /// Classes of interchangeable slots, as indices into <paramref name="mobile"/>;
    /// only classes with at least two members.
    /// </summary>
    public static int[][] Classes(Level level, int[] mobile)
    {
        var classes = new Dictionary<string, List<int>>();
        for (int n = 0; n < mobile.Length; n++)
        {
            ModelDef def = level.Models[mobile[n]];
            if (def.IsAlive || def.Goal != GoalKind.No || def.OutDir != Dir.No)
            {
                continue;
            }

            string key = $"{def.Kind}|{def.Weight}|{def.Power}|{def.IsLeft}|" +
                string.Join(",", Enumerable.Range(0, def.Shape.MarkX.Length).Select(m => $"{def.Shape.MarkX[m]}:{def.Shape.MarkY[m]}"));
            if (!classes.TryGetValue(key, out List<int>? members))
            {
                classes[key] = members = [];
            }

            members.Add(n);
        }

        return [.. classes.Values.Where(c => c.Count > 1).Select(c => c.ToArray())];
    }
}
