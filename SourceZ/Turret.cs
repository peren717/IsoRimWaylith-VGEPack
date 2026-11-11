using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimVGEPackZ
{
    // VGE's turrets turn their gun themselves. Across floors the target's own position is the wrong direction in the shooter's
    // floor frame; ProjectZ's Api 4 AimPoint gives the right point (one place: Aim below). Without Api 4, as VGE does it.
    //  - Gravship turrets (Building_GravshipTurret.Tick: toward the target cell's centre, at VGE's own speed): the one
    //    "target cell -> ToVector3Shifted()" call in Tick is replaced with AimShifted; VGE's turning speed and firing stay.
    //  - The point defence (CompPointDefence.CompTickInterval: the gun top snaps to AngleFlat(target.DrawPos -
    //    turret.DrawPos) when it fires): the target's DrawPos there is replaced with AimDrawPos, the aim point when the
    //    target is on another floor (ProjectZ's Api.OnDifferentFloors), else the target's DrawPos as VGE reads it. A shell's
    //    aim point is its cell's (AimPoint); a thing that is not a projectile (a drop pod) is aimed at as it is drawn:
    //    ProjectZ's Api 12 InterceptorAimPoint (its interceptor rule: the floor of the cell it comes down on); below Api 12
    //    the cell it comes down on placed by AimPoint, with the drawn offset from it.
    // Each part counts as applied only when its one call was swapped; otherwise that method is left as VGE wrote it (one
    // warning).
    internal static class Turret
    {
        [ThreadStatic] private static Building_Turret current;
        private static readonly MethodInfo Shifted = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.ToVector3Shifted));
        private static readonly MethodInfo DrawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
        private static bool swapped;

        internal static void Apply()
        {
            Swap("turret aim", PackZ.Find("VanillaGravshipExpanded.Building_GravshipTurret", "Tick"), nameof(TickPrefix), nameof(AimTranspiler));
            Swap("point defence aim between floors", PackZ.Find("VanillaGravshipExpanded.CompPointDefence", "CompTickInterval"), nameof(CompPrefix), nameof(PdAimTranspiler));
        }

        // Patches one method; listed as applied only when its transpiler swapped the call (else unpatched again).
        private static void Swap(string label, MethodInfo target, string prefix, string transpiler)
        {
            if (target == null)
            {
                return;
            }
            swapped = false;
            try
            {
                PackZ.H.Patch(target, prefix: new HarmonyMethod(typeof(Turret), prefix), transpiler: new HarmonyMethod(typeof(Turret), transpiler),
                    finalizer: new HarmonyMethod(typeof(Turret), nameof(TickFinalizer)));
            }
            catch (Exception e)
            {
                PackZ.missed.Add(label + " (" + e.GetType().Name + ")");
                Log.Warning(PackZ.Tag + label + ": could not patch " + target.DeclaringType?.Name + "." + target.Name + ": " + e.Message);
                return;
            }
            if (!swapped)
            {
                PackZ.H.Unpatch(target, HarmonyPatchType.All, PackZ.H.Id);
                PackZ.missed.Add(label);
                return;
            }
            PackZ.applied.Add(label);
        }

        public static void TickPrefix(Building_Turret __instance) => current = __instance;

        public static void CompPrefix(ThingComp __instance) => current = __instance.parent as Building_Turret;

        public static void TickFinalizer() => current = null;

        // Fails soft: exactly one ToVector3Shifted call, else the original is left as it is (one warning).
        public static IEnumerable<CodeInstruction> AimTranspiler(IEnumerable<CodeInstruction> code)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(code);
            int n = 0, at = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(Shifted))
                {
                    n++;
                    at = i;
                }
            }
            if (n != 1)
            {
                Log.Warning(PackZ.Tag + "turret aim: " + n + " target cell centre calls in VGE's turret Tick (want 1, VGE changed?): its turrets aim as VGE does it.");
                return list;
            }
            list[at] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Turret), nameof(AimShifted))).MoveLabelsFrom(list[at]);
            swapped = true;
            return list;
        }

        // Fails soft: two DrawPos reads (the target's, then the turret's own through its parent field), else as VGE does it.
        public static IEnumerable<CodeInstruction> PdAimTranspiler(IEnumerable<CodeInstruction> code)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(code);
            List<int> at = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (DrawPos != null && list[i].Calls(DrawPos))
                {
                    at.Add(i);
                }
            }
            bool ok = at.Count == 2 && at[0] > 0 && list[at[0] - 1].opcode != OpCodes.Ldfld && at[1] > 0 && list[at[1] - 1].opcode == OpCodes.Ldfld;
            if (!ok)
            {
                Log.Warning(PackZ.Tag + "point defence aim: " + at.Count + " DrawPos reads in VGE's CompPointDefence.CompTickInterval (want the target's, then its own; VGE changed?): its gun turns as VGE does it.");
                return list;
            }
            list[at[0]] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Turret), nameof(AimDrawPos))).MoveLabelsFrom(list[at[0]]);
            swapped = true;
            return list;
        }

        public static Vector3 AimShifted(ref IntVec3 cell)
        {
            Building_Turret t = current;
            if (t == null || !t.Spawned || ProjectZ.Api.Version < 4)
            {
                return cell.ToVector3Shifted();
            }
            LocalTargetInfo target = t.CurrentTarget;
            return Aim(t, target.IsValid && target.Cell == cell ? target : new LocalTargetInfo(cell));
        }

        public static Vector3 AimDrawPos(Thing target)
        {
            if (target == null)
            {
                return Vector3.zero;
            }
            Building_Turret t = current;
            // (the target is often already destroyed here, by the shot: its last position still counts)
            if (t == null || !t.Spawned || ProjectZ.Api.Version < 4)
            {
                return target.DrawPos;
            }
            if (!AcrossFloors(t, target))
            {
                return target.DrawPos;
            }
            if (target is Projectile)
            {
                return Aim(t, target);
            }
            if (ProjectZ.Api.Version >= InterceptorApi)
            {
                return InterceptorAim(t, target);
            }
            IntVec3 cell = target.Position;
            return Aim(t, new LocalTargetInfo(cell)) + (target.DrawPos - cell.ToVector3Shifted());
        }

        private const int InterceptorApi = 12;   // InterceptorAimPoint

        // Names an Api 12 member: in a method of its own.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 InterceptorAim(Building_Turret t, Thing target) => ProjectZ.Api.InterceptorAimPoint(t, target);

        // Names Api 1 and 4 members: in methods of their own, so an older ProjectZ never fails the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AcrossFloors(Building_Turret t, Thing target) => ProjectZ.Api.OnDifferentFloors(t.Position, target.Position, t.Map);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 Aim(Building_Turret t, LocalTargetInfo target) => ProjectZ.Api.AimPoint(t, target);
    }
}
