using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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
        private static AccessTools.FieldRef<Projectile, Thing> equipmentOf;
        private static bool equipmentTried;

        // A verb about to shoot (ProjectZ's lobbed-shot rule).
        internal static bool Is(Verb verb) => verb is Verb_LaunchProjectile launch && Is(verb, launch.Projectile);

        // A projectile in flight (IsoRim's arc rule, asked when it is first drawn: plain reads only). A verb of the equipment
        // that fired it which fires this projectile now decides (so a projectile switched at run time counts too); also, as
        // before, a verb of its weapon that fires it by default.
        internal static bool Is(Projectile p)
        {
            if (p?.def.projectile == null || !p.def.projectile.flyOverhead)
            {
                return false;
            }
            List<Verb> verbs = FiringVerbs(p);
            if (verbs != null)
            {
                for (int i = 0; i < verbs.Count; i++)
                {
                    if (verbs[i] is Verb_LaunchProjectile launch && launch.Projectile == p.def && Is(launch, p.def))
                    {
                        return true;
                    }
                }
            }
            ThingDef weapon = p.EquipmentDef;
            if (weapon?.Verbs == null || !FromVge(weapon))
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

        // A verb of a VGE weapon that needs a line of sight, firing a projectile that flies overhead.
        private static bool Is(Verb verb, ThingDef projectile)
        {
            if (!(verb is Verb_LaunchProjectile) || verb.verbProps == null || !verb.verbProps.requireLineOfSight || !FromVge(verb.EquipmentSource?.def))
            {
                return false;
            }
            return projectile?.projectile != null && projectile.projectile.flyOverhead;
        }

        // The verbs of the equipment that fired p, as the game recorded it in p (Projectile.equipment: the weapon, or the turret
        // a pawn mans, whose gun fires): plain reads. Null without equipment (or if the game's field is gone).
        private static List<Verb> FiringVerbs(Projectile p)
        {
            if (!equipmentTried)
            {
                equipmentTried = true;
                try
                {
                    equipmentOf = AccessTools.FieldRefAccess<Projectile, Thing>("equipment");
                }
                catch (Exception e)
                {
                    equipmentOf = null;   // (then only the weapon's default projectiles decide, as before)
                    Log.Warning("[IsoRim VGE Pack] Projectile.equipment not found: only the weapon's default projectiles decide direct shots (" + e.GetType().Name + ").");
                }
            }
            Thing equipment = equipmentOf?.Invoke(p);
            if (equipment is Building_TurretGun turret)
            {
                equipment = turret.gun;
            }
            return (equipment as ThingWithComps)?.GetComp<CompEquippable>()?.AllVerbs;
        }

        private static bool FromVge(ThingDef weapon) => Vge.Owns(weapon?.modContentPack);
    }
}
