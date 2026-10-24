using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimVGEPack
{
    // VGE's crew bunk bed (BunkBeds.dll, CompBunkBed) with its 3D model (Defs\VGE_Models_B2b.xml; each sleeper on their own deck
    // by IsoRim's ModelDef.slots, Api 28, Patches\VGE_Models_B2b.xml):
    //  - CompBunkBed.PostDraw draws the upper bunk as a flat sprite every frame: left out while the model stands in
    //    (IsoRim Api.SkipWhenModelled), as the model has both decks.
    //  - BunkBeds moves a sleeper's flat sprite up the screen for the upper bunk (its PawnRenderer.GetBodyPos postfix). A
    //    sleeper drawn as a flat sprite on the model (3D figures off) lies on its own deck by height already, so that move
    //    is taken back out while the model stands in: the position the game had before BunkBeds' postfix. 3D figures do not
    //    use GetBodyPos and need nothing.
    // BunkBeds is reached by name; without it nothing is patched.
    internal static class BunkBedsGlue
    {
        private static Type compType;

        [ThreadStatic] private static bool haveBefore;
        [ThreadStatic] private static Vector3 before;

        internal static void Run(Harmony h)
        {
            compType = AccessTools.TypeByName("BunkBeds.CompBunkBed");
            if (compType == null)
            {
                return;   // no BunkBeds loaded
            }
            MethodInfo postDraw = AccessTools.DeclaredMethod(compType, "PostDraw");
            if (postDraw == null || !IsoRim.Api.SkipWhenModelled(postDraw))
            {
                Log.Warning(Pack.Tag + "BunkBeds' upper bunk sprite could not be left out under the 3D model.");
            }
            MethodInfo bodyPos = AccessTools.Method(typeof(PawnRenderer), "GetBodyPos");
            if (bodyPos != null)
            {
                h.Patch(bodyPos, postfix: new HarmonyMethod(typeof(BunkBedsGlue), nameof(Before)) { priority = Priority.First });
                h.Patch(bodyPos, postfix: new HarmonyMethod(typeof(BunkBedsGlue), nameof(After)) { priority = Priority.Last, after = new[] { "BunkBedsMod" } });
            }
        }

        // Ahead of BunkBeds: the game's own position.
        public static void Before(Vector3 __result)
        {
            before = __result;
            haveBefore = true;
        }

        // After BunkBeds: its upper-bunk move taken back while the bunk bed's model stands in.
        public static void After(Pawn ___pawn, ref Vector3 __result)
        {
            if (!haveBefore)
            {
                return;
            }
            haveBefore = false;
            try
            {
                Pawn pawn = ___pawn;   // the renderer's pawn (private field)
                Building_Bed bed = pawn != null ? pawn.CurrentBed() : null;
                if (bed != null && HasComp(bed) && IsoRim.Api.ModelStandsIn(bed))
                {
                    __result = before;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Pack.Tag + "Placing a bunk bed sleeper failed: " + e, 0x5c1b0a17);
            }
        }

        private static bool HasComp(ThingWithComps t)
        {
            var comps = t.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                if (compType.IsInstanceOfType(comps[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
