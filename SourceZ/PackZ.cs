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
    // IsoRim Waylith - VGE Pack, IsoRimZ part: VGE (Vanilla Gravship Expanded, Chapters 1 and 2) on maps with IsoRimZ floors.
    // VGE keeps its own behaviour; only where its code assumes a flat map the pack puts things on the right floor, with
    // ProjectZ's public API (Api 2: floors; Api 4: ship cells, fog refresh, deck foundations):
    //  - Landing spots (GroundSize): the enemy gravjumper's landing spot search and "The Gravship" scenario's edge spawn use
    //    the ground's size instead of the whole map's (which includes the floors' rows): ships land on the ground.
    //  - Ship cells (ShipCells, Api 4): VGE's own per-cell gravship data (vac barrier roof colours captured at take-off and
    //    written back at landing, the subscaffold redraw and the pawns moved out of the way at landing) lands on the deck's
    //    floor instead of the ground cell under it.
    //  - Fog (Api 4): after an enemy structure lands VGE fogs the whole map and unfogs from the structure; ProjectZ then runs its
    //    fog rule again, so the upper floors the colony reaches stay revealed.
    //  - Artillery (Floors): incoming VGE artillery that misses lands on the target's floor; salvager rockets on the
    //    dropship target's floor; the salvager dropship lands on its centre's floor.
    //  - Turrets (Api 4): VGE gravship turrets turn toward ProjectZ.Api.AimPoint (the right direction to a target on another
    //    floor), at VGE's own speed.
    //  - Deck foundations (Api 4): VGE subarmor and subscaffold are built on upper floors like ship decks and count as decks;
    //    VGE artillery damages decks into damaged decks, which count as decks and are repaired by rebuilding the deck.
    //  - The mass lance (Api 1, RegisterLobbedShot): the astropede's VGE_MassLance needs a line of sight; its slug only flies
    //    overhead to pass over walls and cover. Declared direct: it stays on its floor, its misses on the target's floor, and
    //    it does not come down on a deck over its target (a lobbed shell would). VGE's artillery stays lobbed.
    // VGE is reached by name: a missing type or method leaves that part as VGE does it (logged once).
    [StaticConstructorOnStartup]
    public static class PackZ
    {
        // The mass lance is a direct shot (see above); every other verb: no say.
        private static bool? LanceRule(Verb verb, IntVec3 from, IntVec3 to)
        {
            return verb?.EquipmentSource != null && verb.EquipmentSource.def.defName == "VGE_MassLance" ? false : (bool?)null;
        }

        internal const string Tag = "[IsoRim VGE Pack, IsoRimZ] ";
        internal static readonly Harmony H = new Harmony("peren.isorimwaylith.vgepack.z");
        internal static readonly List<string> applied = new List<string>();
        internal static readonly List<string> missed = new List<string>();

        static PackZ()
        {
            try
            {
                if (ProjectZ.Api.Version < 2)
                {
                    Log.Message(Tag + "IsoRimZ has no floor API (Api 2): VGE is left as it is on floored maps.");
                    return;
                }
                Setup2.Run();
                ProjectZ.Api.RegisterLobbedShot(new Func<Verb, IntVec3, IntVec3, bool?>(LanceRule));
                applied.Add("mass lance direct");
                if (ProjectZ.Api.Version >= 4)
                {
                    Setup4.Run();
                }
                else
                {
                    Log.Message(Tag + "IsoRimZ older than Api 4: VGE ship data on decks, fog after landings and deck foundations are left as they are.");
                }
                Log.Message(Tag + "applied: " + string.Join(", ", applied) + (missed.Count > 0 ? "; not found (left as VGE does it): " + string.Join(", ", missed) : ""));
            }
            catch (Exception e)
            {
                Log.Error(Tag + "Setup failed: " + e);
            }
        }

        // A VGE method by "Type:Method" (null, noted, if missing).
        internal static MethodInfo Find(string typeName, string method)
        {
            if (typeName.StartsWith("VanillaGravshipExpanded2.") && !ModsConfig.IsActive("vanillaexpanded.gravship2"))
            {
                return null;   // (a Chapter 2 part, Chapter 2 not loaded: nothing to fix)
            }
            Type t = AccessTools.TypeByName(typeName);
            MethodInfo m = t != null ? AccessTools.Method(t, method) : null;
            if (m == null)
            {
                missed.Add(typeName.Substring(typeName.LastIndexOf('.') + 1) + "." + method);
            }
            return m;
        }

        internal static bool Patch(string label, MethodInfo target, string prefix = null, string postfix = null, string transpiler = null, string finalizer = null, Type owner = null)
        {
            if (target == null)
            {
                return false;
            }
            owner = owner ?? typeof(PackZ);
            try
            {
                H.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(owner, prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(owner, postfix) : null,
                    transpiler: transpiler != null ? new HarmonyMethod(owner, transpiler) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(owner, finalizer) : null);
                if (!applied.Contains(label))
                {
                    applied.Add(label);
                }
                return true;
            }
            catch (Exception e)
            {
                missed.Add(label + " (" + e.GetType().Name + ")");
                Log.Warning(Tag + label + ": could not patch " + target.DeclaringType?.Name + "." + target.Name + ": " + e.Message);
                return false;
            }
        }

        internal static readonly MethodInfo IntVec3Add = AccessTools.Method(typeof(IntVec3), "op_Addition", new[] { typeof(IntVec3), typeof(IntVec3) });

        // Replaces every "IntVec3 + IntVec3" in a method with a call to the given static method (IntVec3, IntVec3) -> IntVec3.
        internal static IEnumerable<CodeInstruction> SwapAdd(IEnumerable<CodeInstruction> code, MethodInfo with, string label)
        {
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi == IntVec3Add)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = with;
                    n++;
                }
                yield return ci;
            }
            if (n == 0)
            {
                Log.Warning(Tag + label + ": no cell addition found (VGE changed?): left as VGE does it.");
            }
        }
    }

    // Api 2 fixes (floor queries only).
    internal static class Setup2
    {
        internal static void Run()
        {
            GroundSize.Apply();
            Floors.Apply();
            Turret.Apply();   // (its Api 4 call is checked at run time)
        }
    }

    // Api 4 fixes; everything that names Api 4 members lives here and below, so an older ProjectZ never loads them.
    internal static class Setup4
    {
        internal static void Run()
        {
            ShipCells.Apply();
            Fog.Apply();
            Decks.Apply();
            DeckRepair.Apply();
        }
    }
}
