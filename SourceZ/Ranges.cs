using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace IsoRimVGEPackZ
{
    // VGE's own range checks between floors. VGE measures a few gravship ranges with IntVec3.InHorDistOf on raw map cells;
    // on a map with floors a cell upstairs lies in another band of rows, so a grav field extender on a deck reads as far out of
    // the engine's range, and a targeting terminal and a turret on different floors as out of link range. These checks become
    // ProjectZ's 3D distance between floors (Api 2 Distance3D); on one floor, or a map without floors, the game's own
    // InHorDistOf, unchanged:
    //  - PlaceWorker_InRangeOfGravSource (VGE's grav field extender): placement (AllowsPlacing) and its ghost lines (DrawGhost)
    //  - TurretLinkerUtility.StartLinking: a terminal (or the bombardment targeter) linking a turret, its range check
    //  - Building_GravshipTurret.StartLinking: a turret linking a terminal, its target validator
    // Each part fails soft: no InHorDistOf call found, a warning, and the method stays as VGE wrote it.
    internal static class Ranges
    {
        [ThreadStatic] private static Map placeMap;
        private static readonly MethodInfo InHor = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.InHorDistOf), new[] { typeof(IntVec3), typeof(float) });
        private static readonly MethodInfo WithinM = AccessTools.Method(typeof(Ranges), nameof(Within));

        internal static void Apply()
        {
            MethodInfo allows = PackZ.Find("VanillaGravshipExpanded.PlaceWorker_InRangeOfGravSource", "AllowsPlacing");
            PackZ.Patch("extender range between floors", allows, prefix: nameof(PlacePrefix), transpiler: nameof(Swap), finalizer: nameof(PlaceFinalizer), owner: typeof(Ranges));
            PackZ.Patch("extender range between floors", PackZ.Find("VanillaGravshipExpanded.PlaceWorker_InRangeOfGravSource", "DrawGhost"), transpiler: nameof(Swap), owner: typeof(Ranges));
            foreach (MethodInfo m in Lambdas("VanillaGravshipExpanded.TurretLinkerUtility", "<StartLinking>").Concat(Lambdas("VanillaGravshipExpanded.Building_GravshipTurret", "<StartLinking>")))
            {
                PackZ.Patch("turret link range between floors", m, transpiler: nameof(Swap), owner: typeof(Ranges));
            }
        }

        // The compiler's lambda methods of a VGE method that call InHorDistOf (on the type or its nested closure classes).
        private static IEnumerable<MethodInfo> Lambdas(string typeName, string marker)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null)
            {
                PackZ.missed.Add(typeName.Substring(typeName.LastIndexOf('.') + 1) + " " + marker);
                yield break;
            }
            List<MethodInfo> found = new List<MethodInfo>();
            foreach (Type owner in new[] { t }.Concat(t.GetNestedTypes(AccessTools.all)))
            {
                foreach (MethodInfo m in owner.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                {
                    if (m.Name.Contains(marker) && Calls(m))
                    {
                        found.Add(m);
                    }
                }
            }
            if (found.Count == 0)
            {
                Log.Warning(PackZ.Tag + typeName + " " + marker + ": no range check found (VGE changed?): left as VGE does it.");
            }
            foreach (MethodInfo m in found)
            {
                yield return m;
            }
        }

        private static bool Calls(MethodInfo m)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => (p.Key == OpCodes.Call || p.Key == OpCodes.Callvirt) && Equals(p.Value, InHor));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void PlacePrefix(Map map) => placeMap = map;

        public static void PlaceFinalizer() => placeMap = null;

        public static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> code, MethodBase __originalMethod)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(code);
            int n = 0;
            foreach (CodeInstruction ci in list)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && Equals(ci.operand, InHor))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = WithinM;
                    n++;
                }
            }
            if (n == 0)
            {
                Log.Warning(PackZ.Tag + __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name + ": no range check found (VGE changed?): left as VGE does it.");
            }
            return list;
        }

        // InHorDistOf(a, b, maxDist) on the map of the call (the placement's map, else the viewed one): 3D between floors.
        public static bool Within(ref IntVec3 a, IntVec3 b, float maxDist)
        {
            Map map = placeMap ?? Find.CurrentMap;
            if (map == null || ProjectZ.Api.Version < 2)
            {
                return a.InHorDistOf(b, maxDist);
            }
            return Across(a, b, maxDist, map);
        }

        // Names Api 2 members: in a method of its own.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Across(IntVec3 a, IntVec3 b, float maxDist, Map map)
        {
            if (!ProjectZ.Api.HasFloors(map) || ProjectZ.Api.SameFloor(a, b, map))
            {
                return a.InHorDistOf(b, maxDist);
            }
            return ProjectZ.Api.Distance3D(a, b, map) <= maxDist;
        }
    }
}
