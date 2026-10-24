using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace IsoRimVGEPack
{
    // IsoRim Waylith - VGE Pack, IsoRim part (needs IsoRim Api 26; older: this part does nothing, logged once).
    //  - Armour hull corners: VGE Chapter 2 draws diagonal corner pieces for its gravship armour in its own flat section layer.
    //    In IsoRim's 3D view the armour is a wall block; the corners become the same 3D wedges as the vanilla hull's, by VGE's
    //    own rule (SectionLayer_GravshipArmorHull.ShouldDrawCornerPiece, called by name) and colour, and that layer's flat
    //    corner pieces are left out (its substructure edges and indoor mask stay): IsoRim.Api.AddWallCorners, HideFlatLayer.
    //  - Gravship scene: the armour layer flies with the ship (Api.FlyLayer); VGE's scaffold mask layers are no geometry.
    //  - (IsoRim Api 30) Vac barrier fields: the 3D field across the doorway takes VGE's recolourable barrier colour
    //    (Building_VacBarrier_Recolorable.barrierColor): IsoRim.Api.AddGlassColor.
    // VGE is reached by name: a missing type or method leaves that part as VGE draws it (logged once).
    [StaticConstructorOnStartup]
    public static class Pack
    {
        internal const string Tag = "[IsoRim VGE Pack] ";

        static Pack()
        {
            try
            {
                if (IsoRim.Api.Version < 26)
                {
                    Log.Message(Tag + "IsoRim has no wall piece API (Api 26): VGE armour corners stay flat in 3D.");
                    return;
                }
                Setup26.Run();
                if (IsoRim.Api.Version >= 30)
                {
                    Setup30.Run();
                }
            }
            catch (Exception e)
            {
                Log.Error(Tag + "Setup failed (VGE armour corners stay flat in 3D): " + e);
            }
        }
    }

    // Everything that names Api 30 members lives here, so an older IsoRim never loads them.
    internal static class Setup30
    {
        private const float FieldOpacity = 0.35f;
        private static Type barrierType;
        private static FieldInfo barrierColour;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void Run()
        {
            barrierType = AccessTools.TypeByName("VanillaGravshipExpanded.Building_VacBarrier_Recolorable");
            barrierColour = barrierType != null ? AccessTools.Field(barrierType, "barrierColor") : null;
            if (barrierColour == null || barrierColour.FieldType != typeof(Color))
            {
                Log.Warning(Pack.Tag + "VGE's vac barrier colour not found: 3D vac barrier fields keep their own colour.");
                return;
            }
            IsoRim.Api.AddGlassColor(new Func<Thing, Color?>(FieldColour));
        }

        // VGE's barrier colour for its recolourable vac barriers (the field's tint), at the field's opacity; others: null.
        private static Color? FieldColour(Thing t)
        {
            if (t == null || !barrierType.IsInstanceOfType(t))
            {
                return null;
            }
            Color c = (Color)barrierColour.GetValue(t);
            return new Color(c.r, c.g, c.b, FieldOpacity);
        }
    }

    // Everything that names Api 26 types lives here, so an older IsoRim never loads them.
    internal static class Setup26
    {
        private static MethodInfo shouldDraw;
        private static readonly object[] args = new object[5];

        internal static void Run()
        {
            Type layer = AccessTools.TypeByName("VanillaGravshipExpanded2.SectionLayer_GravshipArmorHull");
            ThingDef armour = DefDatabase<ThingDef>.GetNamedSilentFail("VGE_GravshipArmor");
            if (layer != null && armour != null)
            {
                shouldDraw = AccessTools.Method(layer, "ShouldDrawCornerPiece");
                if (shouldDraw != null)
                {
                    IsoRim.Api.AddWallCorners(armour, new IsoRim.CornerRule(ArmourCorner));
                    IsoRim.Api.HideFlatLayer(layer, new Func<Material, bool>(IsCornerMaterial));
                }
                else
                {
                    Log.Warning(Pack.Tag + "VGE's armour corner rule not found: armour corners stay flat in 3D.");
                }
                IsoRim.Api.FlyLayer(layer, true);
            }
            foreach (string name in new[] { "VanillaGravshipExpanded.SectionLayer_ScaffoldMask", "VanillaGravshipExpanded.SectionLayer_ScaffoldClear" })
            {
                Type t = AccessTools.TypeByName(name);
                if (t != null)
                {
                    IsoRim.Api.NotGeometryLayer(t);
                }
            }
        }

        // VGE's corner piece pictures (AngledGravshipArmorHull_<dir>, _Partial_<dir>); not its substructure or mask ones.
        private static bool IsCornerMaterial(Material m)
        {
            Texture t = m != null ? m.mainTexture : null;
            return t != null && t.name != null && t.name.StartsWith("AngledGravshipArmorHull");
        }

        // VGE's rule for the cell, as the two armour neighbours of its corner type and VGE's colour.
        private static bool ArmourCorner(Map map, IntVec3 cell, out IntVec3 legA, out IntVec3 legB, out Color colour)
        {
            legA = legB = IntVec3.Invalid;
            colour = Color.white;
            args[0] = cell;
            args[1] = map;
            args[2] = map.terrainGrid;
            args[3] = null;
            args[4] = null;
            if (!(bool)shouldDraw.Invoke(null, args))
            {
                return false;
            }
            colour = args[4] is Color c ? c : Color.white;
            string type = args[3] != null ? args[3].ToString() : "";
            if (type.EndsWith("_NW")) { legA = IntVec3.West; legB = IntVec3.North; }
            else if (type.EndsWith("_NE")) { legA = IntVec3.North; legB = IntVec3.East; }
            else if (type.EndsWith("_SE")) { legA = IntVec3.East; legB = IntVec3.South; }
            else if (type.EndsWith("_SW")) { legA = IntVec3.South; legB = IntVec3.West; }
            else return false;
            return true;
        }
    }
}
