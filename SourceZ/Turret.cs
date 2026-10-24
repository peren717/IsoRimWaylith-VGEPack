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
    // VGE's gravship turrets turn their gun themselves (Building_GravshipTurret.Tick: toward the target cell's centre, at
    // VGE's own speed). Across floors that centre is the wrong direction in the shooter's floor frame; ProjectZ's Api 4
    // AimPoint gives the right point. The one "target cell -> ToVector3Shifted()" call in Tick is replaced with AimShifted;
    // VGE's own turning speed and firing stay. Without Api 4 the cell's centre is used as before.
    internal static class Turret
    {
        [ThreadStatic] private static Building_Turret current;
        private static readonly MethodInfo Shifted = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.ToVector3Shifted));

        internal static void Apply()
        {
            MethodInfo tick = PackZ.Find("VanillaGravshipExpanded.Building_GravshipTurret", "Tick");
            PackZ.Patch("turret aim", tick, prefix: nameof(TickPrefix), transpiler: nameof(AimTranspiler), finalizer: nameof(TickFinalizer), owner: typeof(Turret));
        }

        public static void TickPrefix(Building_Turret __instance) => current = __instance;

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
            return list;
        }

        public static Vector3 AimShifted(ref IntVec3 cell)
        {
            Building_Turret t = current;
            if (t == null || !t.Spawned || ProjectZ.Api.Version < 4)
            {
                return cell.ToVector3Shifted();
            }
            return Aim(t, cell);
        }

        // Names an Api 4 member: in a method of its own, so an older ProjectZ never fails the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 Aim(Building_Turret t, IntVec3 cell)
        {
            LocalTargetInfo target = t.CurrentTarget;
            return ProjectZ.Api.AimPoint(t, target.IsValid && target.Cell == cell ? target : new LocalTargetInfo(cell));
        }
    }
}
