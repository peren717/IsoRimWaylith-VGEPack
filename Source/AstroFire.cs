using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimVGEPack
{
    // (IsoRim Api 46) VGE's astrofire (VGE_Astrofire, burning astrofuel): a VEF custom fire (VEF.Things.CustomFire drawn by
    // VEF.Things.Graphic_CustomFireFlicker), not the game's Fire, so in 3D it stayed VGE's flat purple sprite. Routed into
    // IsoRim's 3D particle fire, recoloured purple from VGE's own fire glow (146, 76, 237); its flat smoke and sparks are
    // left out like the game's fire's. Other VEF custom fires (other mods') are not touched. VEF is reached by name: a
    // missing type or field leaves the astrofire as VGE draws it (logged once).
    internal static class AstroFire
    {
        internal const string DefName = "VGE_Astrofire";
        private static ThingDef def;
        private static AccessTools.FieldRef<object, float> fireSize;

        private static readonly Color[] Flame =
        {
            new Color(0.90f, 0.58f, 1.00f),
            new Color(0.74f, 0.36f, 1.00f),
            new Color(0.62f, 0.24f, 1.00f),
            new Color(0.54f, 0.17f, 0.96f),
            new Color(0.48f, 0.13f, 0.92f),
            new Color(0.44f, 0.11f, 0.88f),
        };

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void Run()
        {
            def = DefDatabase<ThingDef>.GetNamedSilentFail(DefName);
            if (def == null)
            {
                return;   // no VGE (or no astrofire in it): nothing to do, nothing to say
            }
            Type fire = AccessTools.TypeByName("VEF.Things.CustomFire");
            Type graphic = AccessTools.TypeByName("VEF.Things.Graphic_CustomFireFlicker");
            if (fire == null || graphic == null || AccessTools.Field(fire, "fireSize")?.FieldType != typeof(float))
            {
                Log.Warning(Pack.Tag + "VEF's custom fire not found: the astrofire stays VGE's flat sprite in 3D.");
                return;
            }
            fireSize = AccessTools.FieldRefAccess<float>(fire, "fireSize");
            IsoRim.Api.AddFireLook(def, Flame, new Color(0.92f, 0.74f, 1f), new Color(0.44f, 0.40f, 0.50f));
            if (!IsoRim.Api.AddBurningFire(graphic, fire, new Func<Thing, float>(Size)))
            {
                Log.Warning(Pack.Tag + "IsoRim could not take over the astrofire's drawing: it stays VGE's flat sprite in 3D.");
            }
        }

        // The astrofire's own fire size; other things drawn with the same graphic (other custom fires, fire overlays): pass.
        private static float Size(Thing t)
        {
            return t != null && t.def == def ? fireSize(t) : -1f;
        }
    }
}
