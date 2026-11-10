using RimWorld;
using Verse;

namespace IsoRimVGEPackZ
{
    // The pack's own defs (1.7/IsoRimZ/Defs, loaded with this assembly), named in one place.
    [DefOf]
    public static class PackDefOf
    {
        public static TerrainDef VGEPack_DamagedShipDeck;

        static PackDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PackDefOf));
        }
    }
}
