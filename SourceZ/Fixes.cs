using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsoRimVGEPackZ
{
    // Landing spots: VGE searches the enemy gravjumper's landing spot and "The Gravship" scenario's edge spawn over the
    // whole map; on a floored map its rows include the floors above. Those two searches see the ground's size instead
    // (every "map.Size" in them), so the spot is on the ground; the search itself (its random tries, scores) is VGE's.
    internal static class GroundSize
    {
        private static readonly MethodInfo getSize = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Size));

        internal static void Apply()
        {
            foreach (string[] m in new[]
            {
                new[] { "VanillaGravshipExpanded2.WorldComponent_GravshipCombat", "FindBestGravjumperLandingSpot" },
                new[] { "VanillaGravshipExpanded2.ScenPart_TheGravship", "FindEdgeSpawnSpot" },
            })
            {
                PackZ.Patch("landing spots on the ground", PackZ.Find(m[0], m[1]), transpiler: nameof(Transpiler), owner: typeof(GroundSize));
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo with = AccessTools.Method(typeof(GroundSize), nameof(Of));
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == getSize)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = with;
                    n++;
                }
                yield return ci;
            }
            if (n == 0)
            {
                Log.Warning(PackZ.Tag + "landing spots: no map size read found (VGE changed?): left as VGE does it.");
            }
        }

        // The map's size, its rows cut to the ground's on a map with floors.
        public static IntVec3 Of(Map map)
        {
            IntVec3 s = map.Size;
            if (!ProjectZ.Api.HasFloors(map))
            {
                return s;
            }
            CellRect g = ProjectZ.Api.GroundRect(map);
            return new IntVec3(s.x, s.y, g.maxZ + 1);
        }
    }

    // Artillery and salvagers: a cell picked near another one stays on that cell's floor.
    internal static class Floors
    {
        [ThreadStatic] private static Map map;
        [ThreadStatic] private static IntVec3 centre;

        internal static void Apply()
        {
            MethodInfo spawn = PackZ.Find("VanillaGravshipExpanded.ArtilleryUtility", "SpawnArtilleryProjectile");
            PackZ.Patch("artillery misses on the target's floor", spawn, prefix: nameof(Artillery_Prefix), transpiler: nameof(Add_Transpiler), finalizer: nameof(Clear_Finalizer), owner: typeof(Floors));
            MethodInfo tick = PackZ.Find("VanillaGravshipExpanded2.Spawner_SalvagerDropshipBombardment", "Tick");
            PackZ.Patch("salvager rockets on the target's floor", tick, prefix: nameof(Thing_Prefix), transpiler: nameof(Add_Transpiler), finalizer: nameof(Clear_Finalizer), owner: typeof(Floors));
            MethodInfo find = PackZ.Find("VanillaGravshipExpanded2.Spawner_SalvagerDropshipBombardment", "TryFindLandingSpotNear");
            MethodInfo can = PackZ.Find("VanillaGravshipExpanded2.Spawner_SalvagerDropshipBombardment", "CanLandAt");
            if (find != null && can != null)
            {
                PackZ.Patch("salvager dropship lands on its centre's floor", find, prefix: nameof(Landing_Prefix), finalizer: nameof(Landing_Finalizer), owner: typeof(Floors));
                PackZ.Patch("salvager dropship lands on its centre's floor", can, postfix: nameof(CanLand_Postfix), owner: typeof(Floors));
            }
        }

        public static void Artillery_Prefix(PlanetTile targetTile)
        {
            map = Find.Maps.Find(m => m.Tile == targetTile);
        }

        public static void Thing_Prefix(Thing __instance)
        {
            map = __instance.Map;
        }

        public static void Clear_Finalizer()
        {
            map = null;
        }

        public static IEnumerable<CodeInstruction> Add_Transpiler(IEnumerable<CodeInstruction> code)
        {
            return PackZ.SwapAdd(code, AccessTools.Method(typeof(Floors), nameof(OnFloor)), "artillery / salvager rockets");
        }

        // a + offset, kept on a's floor of the map being worked on (inside its floor's rows; plain sum elsewhere).
        public static IntVec3 OnFloor(IntVec3 a, IntVec3 offset)
        {
            IntVec3 sum = a + offset;
            Map m = map;
            if (m == null || !ProjectZ.Api.HasFloors(m) || !a.InBounds(m))
            {
                return sum;
            }
            int k = ProjectZ.Api.FloorOf(a, m);
            if (k < 0)
            {
                return sum;
            }
            IntVec3 g = ProjectZ.Api.GroundCellOf(a, m) + new IntVec3(offset.x, 0, offset.z);
            CellRect ground = ProjectZ.Api.GroundRect(m);
            g = new IntVec3(Math.Max(ground.minX, Math.Min(ground.maxX, g.x)), 0, Math.Max(ground.minZ, Math.Min(ground.maxZ, g.z)));
            IntVec3 c = ProjectZ.Api.CellOnFloor(g, k, m);
            return c.IsValid ? c : sum;
        }

        public static void Landing_Prefix(IntVec3 center, Map map)
        {
            centre = center;
            Floors.map = map;
        }

        public static void Landing_Finalizer()
        {
            map = null;
            centre = IntVec3.Invalid;
        }

        // A landing spot whose footprint leaves the centre's floor (or crosses void rows) is no spot.
        public static void CanLand_Postfix(IntVec3 cell, Map map, ThingDef dropshipDef, Rot4 rot, ref bool __result)
        {
            if (!__result || Floors.map != map || !centre.IsValid || !ProjectZ.Api.HasFloors(map))
            {
                return;
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(cell, rot, dropshipDef.size))
            {
                if (!ProjectZ.Api.SameFloor(c, centre, map) || ProjectZ.Api.FloorOf(c, map) < 0)
                {
                    __result = false;
                    return;
                }
            }
        }
    }

    // Ship cells (Api 4): VGE keeps its own gravship data per cell relative to the engine (vac barrier roof colours), and sums
    // ship offsets with map cells itself. A deck cell's offset holds its floor in y, which a plain sum drops onto the ground
    // cell. In VGE's four methods every "cell + cell" is done by ProjectZ's ship cell rule instead.
    internal static class ShipCells
    {
        [ThreadStatic] private static Map map;

        internal static void Apply()
        {
            Do("VanillaGravshipExpanded.Gravship_CopyCellContents", "Postfix", "vac barrier roof colours on decks");
            Do("VanillaGravshipExpanded.GravshipPlacementUtility_SpawnRoofs", "Postfix", "vac barrier roof colours on decks");
            Do("VanillaGravshipExpanded.GravshipPlacementUtility_PlaceGravshipInMap_Patch", "Prefix", "pawns moved out of the landing on decks");
            Do("VanillaGravshipExpanded.WorldComponent_GravshipController_LandingEnded_Patch", "RegenScaffondingSections", "subscaffold redrawn on decks");
        }

        private static void Do(string type, string method, string label)
        {
            MethodInfo m = PackZ.Find(type, method);
            if (m == null)
            {
                return;
            }
            if (SumsCells(m))
            {
                PackZ.Patch(label, m, prefix: nameof(Map_Prefix), transpiler: nameof(Transpiler), finalizer: nameof(Clear_Finalizer), owner: typeof(ShipCells));
            }
            else
            {
                PackZ.Patch(label, m, prefix: nameof(Map_Prefix), finalizer: nameof(Clear_Finalizer), owner: typeof(ShipCells));   // the sums are in its lambdas
            }
            // lambdas of that method (compiler-made methods in nested classes of its type) that sum cells
            Type t = m.DeclaringType;
            foreach (Type nested in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                foreach (MethodInfo l in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (l.Name.Contains("<" + method + ">") && SumsCells(l))
                    {
                        PackZ.Patch(label, l, transpiler: nameof(Transpiler), owner: typeof(ShipCells));
                    }
                }
            }
        }

        private static bool SumsCells(MethodInfo m)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => p.Value is MethodInfo mi && mi == PackZ.IntVec3Add);
            }
            catch
            {
                return false;
            }
        }

        public static void Map_Prefix(object[] __args)
        {
            map = null;
            foreach (object a in __args)
            {
                if (a is Map m)
                {
                    map = m;
                    return;
                }
            }
        }

        public static void Clear_Finalizer()
        {
            map = null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            return PackZ.SwapAdd(code, AccessTools.Method(typeof(ShipCells), nameof(Add)), "ship cells");
        }

        // A ship offset (its floor in y) plus a map cell (the origin, root or landing spot), or the other way round: the map
        // cell of that offset by ProjectZ's rule. Two plain cells (no floor in either): their plain sum.
        public static IntVec3 Add(IntVec3 a, IntVec3 b)
        {
            Map m = map;
            if (m == null || (a.y == 0 && b.y == 0) || !ProjectZ.Api.HasFloors(m))
            {
                return a + b;
            }
            IntVec3 local = a.y != 0 ? a : b;
            IntVec3 cell = a.y != 0 ? b : a;
            IntVec3 root = cell.InBounds(m) ? ProjectZ.Api.GroundCellOf(cell, m) : cell;
            return ProjectZ.Api.ShipToMap(root, local, m);
        }
    }

    // Fog (Api 4): after VGE fogs the whole map again for a landed enemy structure, ProjectZ's fog rule runs again.
    internal static class Fog
    {
        internal static void Apply()
        {
            PackZ.Patch("fog after an enemy landing", PackZ.Find("VanillaGravshipExpanded2.LandingStructure_EnemyStructure", "Refog"), postfix: nameof(Refog_Postfix), owner: typeof(Fog));
        }

        public static void Refog_Postfix(Map map)
        {
            if (map != null && ProjectZ.Api.HasFloors(map))
            {
                ProjectZ.Api.RequestFogRefresh(map);
            }
        }
    }

    // Deck foundations (Api 4): VGE subarmor, VGE subscaffold and the pack's damaged deck count as ship decks on upper floors
    // (built like the deck, directly on the floor support; ProjectZ.Api.RegisterDeckTerrain). The deck turns into the damaged
    // deck under VGE artillery (VGE's own DamagedTerrainReplacementExtension, added to the deck by Patches/Decks.xml).
    internal static class Decks
    {
        internal static void Apply()
        {
            foreach (string name in new[] { "VGE_GravshipSubarmor", "VGE_GravshipSubscaffold", "VGEPack_DamagedShipDeck" })
            {
                TerrainDef t = DefDatabase<TerrainDef>.GetNamedSilentFail(name);
                if (t != null)
                {
                    ProjectZ.Api.RegisterDeckTerrain(t);
                    PackZ.applied.Add("deck foundation " + name);
                }
            }
        }
    }
}

namespace IsoRimVGEPackZ
{
    // Repair of a damaged deck, as VGE repairs damaged substructure: VGE lets substructure be planned over its damaged
    // substructure (GenConstruct_CanPlaceBlueprintAt_Patch.ShouldSkip); the same for a ship deck over the pack's damaged deck.
    // The deck is then built straight over it (the foundation replaced), so nothing on it falls meanwhile.
    internal static class DeckRepair
    {
        private static TerrainDef damaged, deck;

        internal static void Apply()
        {
            damaged = DefDatabase<TerrainDef>.GetNamedSilentFail("VGEPack_DamagedShipDeck");
            deck = DefDatabase<TerrainDef>.GetNamedSilentFail("PZ_ShipDeck");
            if (damaged == null || deck == null)
            {
                return;
            }
            PackZ.Patch("damaged deck repaired by a new deck", PackZ.Find("VanillaGravshipExpanded.GenConstruct_CanPlaceBlueprintAt_Patch", "ShouldSkip"), postfix: nameof(ShouldSkip_Postfix), owner: typeof(DeckRepair));
        }

        public static void ShouldSkip_Postfix(IntVec3 cell, Map map, TerrainDef terrainDef, ref bool __result)
        {
            if (!__result && terrainDef == deck && map != null && map.terrainGrid.FoundationAt(cell) == damaged)
            {
                __result = true;
            }
        }
    }
}
