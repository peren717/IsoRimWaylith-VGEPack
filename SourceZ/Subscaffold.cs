using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace IsoRimVGEPackZ
{
    // VGE leaves subscaffold out of the substructure limit: before the engine's valid set is filled it adds the subscaffold
    // cells it reaches with a flat flood fill from the engine to the limit (GravshipUtility_GetConnectedSubstructure_Patch.
    // GetConnectedSubstructure). On a map with floors that flood stays on the ground, so subscaffold on upper decks counted
    // against the limit. Here the subscaffold on upper floors of the ship (ProjectZ Api 5 ShipCells, every floor, no limit) is
    // added too: subscaffold is free on every floor, as on the ground.
    internal static class FreeSubscaffold
    {
        private static TerrainDef scaffold;
        private static readonly HashSet<IntVec3> tmp = new HashSet<IntVec3>();

        internal static void Apply()
        {
            scaffold = DefDatabase<TerrainDef>.GetNamedSilentFail("VGE_GravshipSubscaffold");
            if (scaffold == null)
            {
                return;
            }
            MethodInfo count = PackZ.Find("VanillaGravshipExpanded.GravshipUtility_GetConnectedSubstructure_Patch", "GetConnectedSubstructure");
            PackZ.Patch("subscaffold free on upper decks", count, postfix: nameof(CountPostfix), owner: typeof(FreeSubscaffold));
        }

        public static void CountPostfix(Building_GravEngine engine, ref int maxCells, bool requireInsideFootprint)
        {
            if (engine == null || !engine.Spawned || maxCells == int.MaxValue || ProjectZ.Api.Version < 5 || !ProjectZ.Api.HasFloors(engine.Map))
            {
                return;
            }
            maxCells += Upper(engine, requireInsideFootprint);
        }

        // Names an Api 5 member: in a method of its own, so an older ProjectZ never fails the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Upper(Building_GravEngine engine, bool requireInsideFootprint)
        {
            Map map = engine.Map;
            ProjectZ.Api.ShipCells(engine, tmp, requireInsideFootprint);
            int n = 0;
            foreach (IntVec3 c in tmp)
            {
                if (ProjectZ.Api.FloorOf(c, map) >= 1 && map.terrainGrid.FoundationAt(c) == scaffold)
                {
                    n++;
                }
            }
            tmp.Clear();
            return n;
        }
    }
}
