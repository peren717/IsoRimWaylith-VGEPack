using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace IsoRimVGEPack
{
    // Model batch B7b (Defs\VGE_Models_B7b.xml): VGE draws its enemy grav engines' orb every frame in its own DrawAt
    // (VanillaGravshipExpanded2.Building_EnemyGravEngine: the base's DrawAt, then the flat _Orb picture). While IsoRim's
    // model stands in, the orb is IsoRim's bob part, so VGE's DrawAt is skipped for that thing (IsoRim Api.SkipWhenModelled,
    // Api 20; the pack needs 26), as for the mechanoid orbs (batch B8a1). With the 3D view off, or without a model (the
    // gravlock tether until its model is approved), VGE draws as always. VGE is reached by name: a missing type leaves its
    // flat orb (logged once).
    internal static class EnemyOrbs
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Run()
        {
            Type t = AccessTools.TypeByName("VanillaGravshipExpanded2.Building_EnemyGravEngine");
            if (t == null)
            {
                return;   // Chapter 2 is not loaded
            }
            MethodInfo m = AccessTools.DeclaredMethod(t, "DrawAt");
            if (m == null || !IsoRim.Api.SkipWhenModelled(m))
            {
                Log.Warning(Pack.Tag + t.FullName + ".DrawAt not patched: its flat orb may show over the 3D model.");
            }
        }
    }
}
