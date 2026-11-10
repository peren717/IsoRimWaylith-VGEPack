using System;
using Verse;

namespace IsoRimVGEPackShared
{
    // VGE's direct shots with an overhead projectile, one rule for both parts of the pack (compiled into each).
    // VGE's mass lance (the astropede's) needs a line of sight (its verb's requireLineOfSight), but its slug flies overhead
    // (flyOverhead) only to pass over walls and cover; VGE's mass driver turret fires the same slug with no line of sight, as
    // artillery. So the property, not the name, decides: a VGE weapon whose verb needs a line of sight and whose projectile
    // flies overhead is a direct shot (drawn straight, stays on its floor); every other overhead projectile stays a shell.
    // Read from the defs each time it is asked, so a patch to VGE's verbs or projectiles is followed.
    internal static class DirectShot
    {
        private static readonly string[] Vge = { "vanillaexpanded.gravship", "vanillaexpanded.gravship2" };

        // A verb about to shoot (ProjectZ's lobbed-shot rule).
        internal static bool Is(Verb verb)
        {
            if (!(verb is Verb_LaunchProjectile launch) || verb.verbProps == null || !verb.verbProps.requireLineOfSight)
            {
                return false;
            }
            ThingDef weapon = verb.EquipmentSource?.def;
            if (!FromVge(weapon))
            {
                return false;
            }
            ThingDef projectile = launch.Projectile;
            return projectile?.projectile != null && projectile.projectile.flyOverhead;
        }

        // A projectile in flight (IsoRim's arc rule): the verb of its weapon that fires it.
        internal static bool Is(Projectile p)
        {
            ThingDef weapon = p?.EquipmentDef;
            if (weapon?.Verbs == null || p.def.projectile == null || !p.def.projectile.flyOverhead || !FromVge(weapon))
            {
                return false;
            }
            foreach (VerbProperties v in weapon.Verbs)
            {
                if (v.defaultProjectile == p.def && v.requireLineOfSight)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool FromVge(ThingDef weapon)
        {
            string id = weapon?.modContentPack?.PackageIdPlayerFacing;
            return id != null && (string.Equals(id, Vge[0], StringComparison.OrdinalIgnoreCase) || string.Equals(id, Vge[1], StringComparison.OrdinalIgnoreCase));
        }
    }
}
