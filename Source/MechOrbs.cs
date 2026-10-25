using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace IsoRimVGEPack
{
    // Model batch B8a (Defs\VGE_Models_B8a.xml): VGE draws the mechanoid grav engine's and the grav tether's orb every frame
    // in its own DrawAt (Building_MechanoidGravEngine / VanillaGravshipExpanded2.Building_MechanoidGravTether: the base's
    // DrawAt, then the flat bobbing _Orb picture). While IsoRim's model stands in, the orb is IsoRim's bob part, so VGE's
    // DrawAt is skipped for that thing (IsoRim Api.SkipWhenModelled, Api 20; the pack needs 26). With the 3D view off, or
    // without a model, VGE draws as always. VGE is reached by name: a missing type leaves its flat orb (logged once).
    internal static class MechOrbs
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Run()
        {
            foreach (string name in new[] { "VanillaGravshipExpanded.Building_MechanoidGravEngine", "VanillaGravshipExpanded2.Building_MechanoidGravTether" })
            {
                Type t = AccessTools.TypeByName(name);
                if (t == null)
                {
                    continue;   // that chapter is not loaded
                }
                MethodInfo m = AccessTools.DeclaredMethod(t, "DrawAt");
                if (m == null || !IsoRim.Api.SkipWhenModelled(m))
                {
                    Log.Warning(Pack.Tag + name + ".DrawAt not patched: its flat orb may show over the 3D model.");
                }
            }
        }
    }
}
