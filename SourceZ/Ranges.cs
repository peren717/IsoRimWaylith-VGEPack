using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
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
    // VGE's point defence measures its own way (CompPointDefence.FindTarget): PointDefence, below.
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
            foreach (MethodInfo m in Lambdas("VanillaGravshipExpanded.TurretLinkerUtility", "<StartLinking>", InHor).Concat(Lambdas("VanillaGravshipExpanded.Building_GravshipTurret", "<StartLinking>", InHor)))
            {
                PackZ.Patch("turret link range between floors", m, transpiler: nameof(Swap), owner: typeof(Ranges));
            }
            PointDefence.Apply();
        }

        // The compiler's lambda methods of a VGE method that call the given method (on the type or its nested closure classes).
        // A null method (the game's method not found) finds none, with a warning: never another method in its place.
        internal static IEnumerable<MethodInfo> Lambdas(string typeName, string marker, MethodInfo calls, bool quiet = false)
        {
            if (calls == null)
            {
                Log.Warning(PackZ.Tag + typeName + " " + marker + ": the game's method it calls was not found: left as VGE does it.");
                yield break;
            }
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
                    if (m.Name.Contains(marker) && Calls(m, calls))
                    {
                        found.Add(m);
                    }
                }
            }
            if (found.Count == 0 && !quiet)
            {
                Log.Warning(PackZ.Tag + typeName + " " + marker + ": no range check found (VGE changed?): left as VGE does it.");
            }
            foreach (MethodInfo m in found)
            {
                yield return m;
            }
        }

        internal static bool Calls(MethodInfo m, MethodInfo target)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => (p.Key == OpCodes.Call || p.Key == OpCodes.Callvirt) && Equals(p.Value, target));
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

    // VGE's point defence between floors. VGE's CompPointDefence.FindTarget takes the nearest hostile shell (or drop pod) by
    // IntVec3Utility.DistanceToSquared(target.DrawPos.ToIntVec3(), turret.Position) on raw map cells and fires when that is
    // within its interceptionRadius. On a map with floors each floor is its own band of rows, so a point defence on a deck
    // never finds a shell flying over another floor. Only that distance is answered here, by ProjectZ's own interceptor
    // measure (Api 12 InterceptorDistance, the one its shield bubbles use: overhead things, shells and drop pods, on the
    // ground circle; direct shots in 3D and never under the turret's floor), squared and rounded up to VGE's whole cells.
    // On one floor, without floors, or with ProjectZ older than Api 12: VGE's own distance. VGE's choice of target (the
    // nearest), its interception chance, fuel and effects stay VGE's; its gun's turn is Turret's.
    // All or nothing: FindTarget and its lambda must each read the target's DrawPos and call DistanceToSquared, and patch
    // cleanly; otherwise none of them stays patched (one warning), the part is listed as missed, and VGE measures as it does.
    internal static class PointDefence
    {
        private const string Label = "point defence range between floors";
        private const string TypeName = "VanillaGravshipExpanded.CompPointDefence";
        private const string Method = "FindTarget";
        private const int ApiNeeded = 12;   // InterceptorDistance

        [ThreadStatic] private static Thing turret;      // the point defence whose FindTarget runs
        [ThreadStatic] private static Thing seen;        // the thing whose DrawPos was read last (the one being measured)
        [ThreadStatic] private static Vector3 seenPos;   // that DrawPos
        private static int swaps;                        // calls swapped by the transpiler run that just ended

        private static readonly MethodInfo DrawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
        private static readonly MethodInfo DistSq = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceToSquared), new[] { typeof(IntVec3), typeof(IntVec3) });

        internal static void Apply()
        {
            if (ProjectZ.Api.Version < ApiNeeded)
            {
                Log.Message(PackZ.Tag + "IsoRimZ older than Api 12: VGE's point defence measures as VGE does it, also between floors.");
                return;
            }
            MethodInfo find = PackZ.Find(TypeName, Method);
            if (find == null)
            {
                return;
            }
            string why = null;
            List<MethodInfo> targets = new List<MethodInfo> { find };
            if (DrawPos == null || DistSq == null)
            {
                why = "the game's Thing.DrawPos or IntVec3Utility.DistanceToSquared not found";
            }
            else
            {
                targets.AddRange(Ranges.Lambdas(TypeName, "<" + Method + ">", DistSq, quiet: true));
                MethodInfo bad = targets.FirstOrDefault(m => !Ranges.Calls(m, DrawPos) || !Ranges.Calls(m, DistSq));
                if (targets.Count < 2)
                {
                    why = "no FindTarget lambda measuring a distance";
                }
                else if (bad != null)
                {
                    why = bad.Name + " has no DrawPos read and distance call";
                }
            }
            List<MethodInfo> patched = new List<MethodInfo>();
            if (why == null)
            {
                foreach (MethodInfo m in targets)
                {
                    swaps = 0;
                    try
                    {
                        PackZ.H.Patch(m, prefix: m == find ? new HarmonyMethod(typeof(PointDefence), nameof(FindPrefix)) : null,
                            transpiler: new HarmonyMethod(typeof(PointDefence), nameof(Transpiler)),
                            finalizer: m == find ? new HarmonyMethod(typeof(PointDefence), nameof(FindFinalizer)) : null);
                        patched.Add(m);
                    }
                    catch (Exception e)
                    {
                        why = "could not patch " + m.Name + ": " + e.Message;
                        break;
                    }
                    if (swaps == 0)
                    {
                        why = m.Name + ": nothing swapped";
                        break;
                    }
                }
            }
            if (why != null)
            {
                foreach (MethodInfo m in patched)
                {
                    PackZ.H.Unpatch(m, HarmonyPatchType.All, PackZ.H.Id);
                }
                PackZ.missed.Add(Label);
                Log.Warning(PackZ.Tag + "VGE's point defence (" + why + ", VGE changed?): it measures as VGE does it, also between floors.");
                return;
            }
            PackZ.applied.Add(Label);
        }

        public static void FindPrefix(ThingComp __instance) => turret = __instance.parent;

        public static void FindFinalizer()
        {
            turret = null;
            seen = null;
        }

        // Every target.DrawPos read and every DistanceToSquared call (checked to be there before patching).
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(code);
            foreach (CodeInstruction ci in list)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && Equals(ci.operand, DrawPos))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(PointDefence), nameof(Seen));
                    swaps++;
                }
                else if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && Equals(ci.operand, DistSq))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(PointDefence), nameof(Distance));
                    swaps++;
                }
            }
            return list;
        }

        // target.DrawPos, noting the target and the position for the distance that follows.
        public static Vector3 Seen(Thing t)
        {
            seen = t;
            seenPos = t.DrawPos;
            return seenPos;
        }

        // DistanceToSquared(a, b) between the noted target's cell and the turret's: ProjectZ's interceptor measure between
        // floors, VGE's own otherwise. (Patched only with Api 12 or later: Apply.)
        public static int Distance(IntVec3 a, IntVec3 b)
        {
            Thing pd = turret;
            Thing t = seen;
            seen = null;
            if (pd == null || t == null || pd.Map == null || (a != pd.Position && b != pd.Position))
            {
                return a.DistanceToSquared(b);
            }
            return Between(t, seenPos, pd.Position, pd.Map, out int dsq) ? dsq : a.DistanceToSquared(b);
        }

        // Names an Api 12 member: in a method of its own.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Between(Thing t, Vector3 pos, IntVec3 centre, Map map, out int dsq)
        {
            dsq = 0;
            if (!ProjectZ.Api.InterceptorDistance(t, pos, centre, map, out float d))
            {
                return false;
            }
            float sq = d * d;
            dsq = float.IsInfinity(sq) || sq >= int.MaxValue ? int.MaxValue : Mathf.CeilToInt(sq);
            return true;
        }
    }
}
