using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace IsoRimVGEPack
{
    // Model batch B1a (Defs\VGE_Models_B1a.xml): the states VGE draws with sprites of its own, named for IsoRim's byState
    // models (IsoRim Api.AddModelState, Api 6+; IsoRim rebuilds the model when the state changes):
    //   "empty"    life support system out of algae (VEF Graphic_RefuelableSingle: fuel <= 0, its _empty texture);
    //   "off"      oxygen / astrofuel valve switched off (the vanilla CompFlickable _Off texture);
    //   "hot"      heatsink holding heat (VGE CompHeatsink draws its _Overlay while stored heat > 0);
    //   "cooldown" gravheat absorber on cooldown (VGE CompGravheatAbsorber draws its _Cooldown texture); gravjumper engine
    //              cooling down after a flight (B1b: Building_GravEngine.cooldownCompleteTick, as VGE's Graphic_GravEngineSingle
    //              picks its _Cooldown body and its DrawAt patch the _Orb_Cooldown orb; the orb by bobByState, IsoRim Api 27);
    //              B4: the vanilla grav engine (VGE re-skin, same rule) and the signal jammer on cooldown (VGE2
    //              CompSignalJammer.cooldownTicksLeft > 0, as it draws its _Cooldown art; its flat cooldown sprite left out under a model).
    // VGE's comps are reached by name; a missing type or field leaves that state out (logged once), never the others.
    // While a model stands in (IsoRim Api.ModelStandsIn), VGE's own flat sprites for those states (the heatsink _Overlay,
    // the absorber _Cooldown, drawn every frame by the comps) are left out: the state model shows them. PipeSystem's fill bar
    // of a modelled storage tank stands up as a card over the model's middle (IsoRim Api.BeginUpright, hinged on the bar,
    // drawn there for the moment instead of at its 2D offset), as IsoRim's work bars do, instead of lying flat through the tank.
    internal static class ModelStates
    {
        private static readonly HashSet<ThingDef> lifeSupport = new HashSet<ThingDef>();
        private static readonly HashSet<ThingDef> valves = new HashSet<ThingDef>();
        private static readonly HashSet<ThingDef> heatsinks = new HashSet<ThingDef>();
        private static readonly HashSet<ThingDef> absorbers = new HashSet<ThingDef>();
        private static readonly HashSet<ThingDef> engines = new HashSet<ThingDef>();
        private static readonly HashSet<ThingDef> jammers = new HashSet<ThingDef>();
        private static Type jammerType;
        private static AccessTools.FieldRef<object, int> jammerCooldown;
        private static Type heatsinkType, absorberType;
        private static AccessTools.FieldRef<object, float> storedHeat;
        private static AccessTools.FieldRef<object, int> cooldownEnd;

        internal static void Run()
        {
            Add(lifeSupport, "VGE_LifeSupportSystem");
            Add(valves, "VGE_OxygenValve", "VGE_AstrofuelValve");
            heatsinkType = AccessTools.TypeByName("VanillaGravshipExpanded.CompHeatsink");
            if (heatsinkType != null && AccessTools.Field(heatsinkType, "storedHeat") != null)
            {
                storedHeat = AccessTools.FieldRefAccess<float>(heatsinkType, "storedHeat");
                Add(heatsinks, "VGE_SmallHeatsink", "VGE_LargeHeatsink");
            }
            else
            {
                Log.Warning(Pack.Tag + "VGE's heatsink heat not found: heatsinks keep their cool model in 3D.");
            }
            absorberType = AccessTools.TypeByName("VanillaGravshipExpanded.CompGravheatAbsorber");
            if (absorberType != null && AccessTools.Field(absorberType, "cooldownEndTick") != null)
            {
                cooldownEnd = AccessTools.FieldRefAccess<int>(absorberType, "cooldownEndTick");
                Add(absorbers, "VGE_GravheatAbsorber");
            }
            else
            {
                Log.Warning(Pack.Tag + "VGE's gravheat absorber cooldown not found: the absorber keeps its idle model in 3D.");
            }
            Add(engines, "VGE_GravjumperEngine", "GravEngine", "VGE_GravhulkEngine");   // GravEngine: VGE's re-skin (B4); the hulk engine (B6a): the same _Cooldown art
            // (B4, Chapter 2) the signal jammer on cooldown: VGE2 CompSignalJammer.OnCooldown (cooldownTicksLeft > 0), as it
            // draws its SignalJammer_Cooldown art
            jammerType = AccessTools.TypeByName("VanillaGravshipExpanded2.CompSignalJammer");
            if (jammerType != null && AccessTools.Field(jammerType, "cooldownTicksLeft") != null)
            {
                jammerCooldown = AccessTools.FieldRefAccess<int>(jammerType, "cooldownTicksLeft");
                Add(jammers, "SignalJammer");
            }
            if (lifeSupport.Count + valves.Count + heatsinks.Count + absorbers.Count + engines.Count + jammers.Count > 0)
            {
                IsoRim.Api.AddModelState(new Func<Thing, string>(State));
            }
            Harmony h = new Harmony("peren.isorimwaylith.vgepack.models");
            foreach (Type t in new[] { heatsinkType, absorberType, jammerType })
            {
                System.Reflection.MethodInfo m = t != null ? AccessTools.DeclaredMethod(t, "PostDraw") : null;
                if (m != null)
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(ModelStates), nameof(SpritePostDraw_Prefix)));
                }
                else if (t != null)
                {
                    Log.Warning(Pack.Tag + t.Name + ".PostDraw not found: its flat state sprite may show over the 3D model.");
                }
            }
            Type storage = AccessTools.TypeByName("PipeSystem.CompResourceStorage");
            System.Reflection.MethodInfo sm = storage != null ? AccessTools.DeclaredMethod(storage, "PostDraw") : null;
            if (sm != null && AccessTools.Field(storage, "request") != null)
            {
                barRequest = AccessTools.FieldRefAccess<GenDraw.FillableBarRequest>(storage, "request");
                h.Patch(sm, prefix: new HarmonyMethod(typeof(ModelStates), nameof(BarPostDraw_Prefix)),
                    finalizer: new HarmonyMethod(typeof(ModelStates), nameof(BarPostDraw_Finalizer)));
            }
        }

        // VGE's heatsink / absorber PostDraw only draws the state sprite (ThingComp.PostDraw is empty): skipped under a model.
        public static bool SpritePostDraw_Prefix(ThingComp __instance)
        {
            return !IsoRim.Api.ModelStandsIn(__instance.parent);
        }

        private static AccessTools.FieldRef<object, GenDraw.FillableBarRequest> barRequest;
        private static UnityEngine.Vector3 barCenter;   // the bar's own centre while it is drawn over the middle (main thread)

        public static void BarPostDraw_Prefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            Thing t = __instance.parent;
            if (IsoRim.Api.ModelStandsIn(t))
            {
                ref GenDraw.FillableBarRequest req = ref barRequest(__instance);
                barCenter = req.center;
                UnityEngine.Vector3 mid = t.DrawPos;
                req.center = new UnityEngine.Vector3(mid.x, barCenter.y, mid.z);
                IsoRim.Api.BeginUpright(IsoRim.Api.ModelHeight(t.def) + 0.3f);   // hinged on the bar, now over the middle
                __state = true;
            }
        }

        public static Exception BarPostDraw_Finalizer(ThingComp __instance, Exception __exception, bool __state)
        {
            if (__state)
            {
                IsoRim.Api.EndUpright();
                barRequest(__instance).center = barCenter;
            }
            return __exception;
        }

        private static void Add(HashSet<ThingDef> set, params string[] names)
        {
            foreach (string n in names)
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                if (d != null)
                {
                    set.Add(d);
                }
            }
        }

        // The thing's state, or null for things this pack does not name (asked for every thing with byState models).
        private static string State(Thing t)
        {
            ThingDef d = t.def;
            if (lifeSupport.Contains(d))
            {
                CompRefuelable r = t.TryGetComp<CompRefuelable>();
                return r != null && r.Fuel <= 0f ? "empty" : null;
            }
            if (valves.Contains(d))
            {
                CompFlickable f = t.TryGetComp<CompFlickable>();
                return f != null && !f.SwitchIsOn ? "off" : null;
            }
            if (heatsinks.Contains(d))
            {
                ThingComp c = Comp(t, heatsinkType);
                return c != null && storedHeat(c) > 0f ? "hot" : null;
            }
            if (engines.Contains(d))
            {
                Building_GravEngine e = t as Building_GravEngine;
                return e != null && Find.TickManager != null && Find.TickManager.TicksGame < e.cooldownCompleteTick ? "cooldown" : null;
            }
            if (absorbers.Contains(d))
            {
                ThingComp c = Comp(t, absorberType);
                return c != null && Find.TickManager != null && Find.TickManager.TicksGame < cooldownEnd(c) ? "cooldown" : null;
            }
            if (jammers.Contains(d))
            {
                ThingComp c = Comp(t, jammerType);
                return c != null && jammerCooldown(c) > 0 ? "cooldown" : null;
            }
            return null;
        }

        private static ThingComp Comp(Thing t, Type type)
        {
            if (t is ThingWithComps twc)
            {
                List<ThingComp> comps = twc.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (type.IsInstanceOfType(comps[i]))
                    {
                        return comps[i];
                    }
                }
            }
            return null;
        }
    }
}
