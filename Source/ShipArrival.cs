using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimVGEPack
{
    // (IsoRim Api 37) VGE's enemy ship arrivals in 3D (VanillaGravshipExpanded.LandingStructureBase: Chapter 1's landing
    // structure, Chapter 2's enemy gravship, scenario raid, gravjumper and captured gravjumper). VGE photographs the generated
    // ship from above on a temporary map (RenderAndSaveTexture) and slides that flat picture in along a curve (DrawGravship),
    // its height a slide up the screen. In IsoRim's 3D view the picture is wrong (taken with IsoRim's matrices) and flat:
    //  - right after VGE's photo, on the temporary map, IsoRim reads the ship into a roofed shell (IsoRim.Api.BuildShell over
    //    VGE's captureBounds): walls, shut doors, closed roofs, deck, the buildings that stand out (thrusters, turrets...);
    //  - while it comes in, the shell is drawn at VGE's own pose (DrawGravship's curve, approach and turn, reproduced below),
    //    the slide up the screen turned into a true height as IsoRim's flying gravship (IsoRim.Api.HeightForSlide), with
    //    IsoRim's shadow; VGE's flat picture, its flat shadow and its flat effects round it are left out;
    //  - at impact VGE spawns the real structure and destroys the arrival in the same tick: the next frame shows the real one.
    // The shell sits round the capture centre; the real structure spawns with the temporary map's centre at the arrival's
    // Position, so the shell is placed at Position + (capture centre - temporary map centre) and lands exactly on it.
    // After a reload VGE has no picture (it is not saved) and photographs the ship again: the shell is built again with it.
    // 2D view, no shell (built in 2D, or failed), or VGE's names missing: VGE draws as always.
    internal static class ShipArrival
    {
        private sealed class Arrival
        {
            public Thing thing;
            public IsoRim.Shell shell;
            public Vector3 offset;   // capture centre - temporary map centre (x, z)
            public Game game;        // the game it was built in
            public ShipArrivalEffects.Data fx;   // VGE's flames and glows round it (ShipArrivalEffects)
        }

        // By reference: a loaded game's things can carry the ids of the game left behind.
        private static readonly Dictionary<Thing, Arrival> arrivals = new Dictionary<Thing, Arrival>(ReferenceEqualityComparer.Instance);
        private static Type baseType;
        private static AccessTools.FieldRef<object, int> ticksToImpact, ticksToImpactMax;
        private static AccessTools.FieldRef<object, Rot4> landingRotation;
        private static AccessTools.FieldRef<object, IntVec3> launchDirection;
        private static AccessTools.FieldRef<object, CellRect> captureBounds;
        private static AccessTools.FieldRef<object, Vector3> captureCenter;
        private static bool failed;

        // For the harness: shells drawn this session and draws skipped.
        internal static int Built, Drawn;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void Run(Harmony harmony)
        {
            baseType = AccessTools.TypeByName("VanillaGravshipExpanded.LandingStructureBase");
            if (baseType == null)
            {
                return;   // no VGE
            }
            System.Reflection.MethodInfo render = AccessTools.Method(baseType, "RenderAndSaveTexture");
            System.Reflection.MethodInfo draw = AccessTools.Method(baseType, "DrawGravship");
            try
            {
                ticksToImpact = AccessTools.FieldRefAccess<int>(baseType, "ticksToImpact");
                ticksToImpactMax = AccessTools.FieldRefAccess<int>(baseType, "ticksToImpactMax");
                landingRotation = AccessTools.FieldRefAccess<Rot4>(baseType, "landingRotation");
                launchDirection = AccessTools.FieldRefAccess<IntVec3>(baseType, "launchDirection");
                captureBounds = AccessTools.FieldRefAccess<CellRect>(baseType, "captureBounds");
                captureCenter = AccessTools.FieldRefAccess<Vector3>(baseType, "captureCenter");
            }
            catch (Exception e)
            {
                Log.Warning(Pack.Tag + "VGE's ship arrival fields not found (" + e.Message + "): enemy ship arrivals stay VGE's flat picture in 3D.");
                return;
            }
            if (render == null || draw == null || render.GetParameters().Length < 1 || render.GetParameters()[0].ParameterType != typeof(Map))
            {
                Log.Warning(Pack.Tag + "VGE's ship arrival methods not found: enemy ship arrivals stay VGE's flat picture in 3D.");
                return;
            }
            ShipArrivalEffects.Resolve(baseType);
            harmony.Patch(render, postfix: new HarmonyMethod(typeof(ShipArrival), nameof(Render_Postfix)));
            harmony.Patch(draw, prefix: new HarmonyMethod(typeof(ShipArrival), nameof(DrawGravship_Prefix)));
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DeSpawn)), postfix: new HarmonyMethod(typeof(ShipArrival), nameof(DeSpawn_Postfix)));
        }

        // After VGE's photo, the temporary map still standing: the shell over VGE's capture bounds (only in the 3D view).
        public static void Render_Postfix(Thing __instance, Map __0)
        {
            if (failed || __0 == null || !IsoRim.Api.ViewReady)
            {
                return;
            }
            try
            {
                Sweep();
                Forget(__instance);
                IsoRim.Shell shell = IsoRim.Api.BuildShell(__0, captureBounds(__instance));
                if (shell == null)
                {
                    return;
                }
                Vector3 off = captureCenter(__instance) - __0.Center.ToVector3Shifted();
                off.y = 0f;
                ShipArrivalEffects.Data fx = null;
                try
                {
                    fx = ShipArrivalEffects.Capture(__instance, captureCenter(__instance), captureBounds(__instance), __0);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(Pack.Tag + "Reading VGE's arrival effects failed (the ship flies in without them): " + e, 0x5A17E1);
                }
                arrivals[__instance] = new Arrival { thing = __instance, shell = shell, offset = off, game = Current.Game, fx = fx };
                Built++;
                if (Prefs.DevMode)
                {
                    Log.Message(Pack.Tag + "ship arrival " + __instance.def.defName + ": " + shell);
                }
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(Pack.Tag + "Building the arriving ship's shell failed (VGE's flat picture stays): " + e);
            }
        }

        // In the 3D view with a shell: the shell at VGE's pose instead of VGE's flat picture and everything round it.
        public static bool DrawGravship_Prefix(Thing __instance)
        {
            Arrival a;
            if (failed || !IsoRim.Api.ViewReady || !arrivals.TryGetValue(__instance, out a) || a.shell == null || a.shell.Disposed)
            {
                return true;
            }
            try
            {
                Vector3 ground;
                float height, yaw;
                float num, num2;
                if (!Pose(__instance, a, out ground, out height, out yaw, out num, out num2))
                {
                    return true;
                }
                a.shell.Draw(ground, yaw, height);
                if (a.fx != null)
                {
                    try
                    {
                        ShipArrivalEffects.Draw(a.fx, __instance.Map, ground, height, yaw, num, num2);
                    }
                    catch (Exception e)
                    {
                        a.fx = null;
                        Log.ErrorOnce(Pack.Tag + "Drawing VGE's arrival effects failed (left out from now on): " + e, 0x5A17E2);
                    }
                }
                Drawn++;
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(Pack.Tag + "Drawing the arriving ship's shell failed (VGE's flat picture from now on): " + e);
                return true;
            }
        }

        // VGE's DrawGravship (VanillaGravshipExpanded.LandingStructureBase, the 1.7 build): the same curves as vanilla's
        // GravshipRenderer.UpdateLanding; the slide up the screen becomes a true height.
        internal static bool Pose(Thing t, out Vector3 ground, out float height, out float yaw)
        {
            Arrival a;
            ground = Vector3.zero;
            height = yaw = 0f;
            float num, num2;
            return arrivals.TryGetValue(t, out a) && Pose(t, a, out ground, out height, out yaw, out num, out num2);
        }

        private static bool Pose(Thing t, Arrival a, out Vector3 ground, out float height, out float yaw, out float num, out float num2)
        {
            ground = Vector3.zero;
            height = yaw = 0f;
            num = num2 = 0f;
            int max = ticksToImpactMax(t);
            if (max <= 0)
            {
                return false;
            }
            // ---- VGE
            num = 1f - (float)ticksToImpact(t) / (float)max;
            num = GenMath.RemapClamped(num, 0f, 0.95f, 0f, 1f);
            num2 = Mathf.Pow(1f - num, 5f);
            Rot4 rot = landingRotation(t);
            Vector3 dir = launchDirection(t).ToVector3().normalized;
            float slide;
            Vector3 approach;
            if (rot == Rot4.North || rot == Rot4.South)
            {
                slide = 100f * num2;
                approach = rot.AsQuat * -dir * 200f * num2;
            }
            else
            {
                slide = 200f * num2;
                approach = rot.AsQuat * -dir * 100f * Mathf.Pow(1f - num, 9f);
            }
            // ---- end VGE
            Vector3 at = t.DrawPos + a.offset + approach;
            ground = new Vector3(at.x, 0f, at.z);
            height = IsoRim.Api.HeightForSlide(slide);
            yaw = rot.AsAngle;   // VGE turns its picture by the landing rotation round its centre
            return true;
        }

        public static void DeSpawn_Postfix(Thing __instance)
        {
            if (arrivals.Count > 0 && baseType.IsInstanceOfType(__instance))
            {
                Forget(__instance);
            }
        }

        private static void Forget(Thing t)
        {
            Arrival a;
            if (arrivals.TryGetValue(t, out a))
            {
                arrivals.Remove(t);
                if (a.shell != null)
                {
                    a.shell.Dispose();
                }
            }
        }

        // Arrivals of a game left behind (loaded over) or on a map gone: their shells let go.
        private static void Sweep()
        {
            if (arrivals.Count == 0)
            {
                return;
            }
            List<Thing> gone = null;
            foreach (KeyValuePair<Thing, Arrival> kv in arrivals)
            {
                Thing t = kv.Key;
                if (kv.Value.game != Current.Game || !t.Spawned || t.Map == null || Find.Maps == null || !Find.Maps.Contains(t.Map))
                {
                    (gone ?? (gone = new List<Thing>())).Add(t);
                }
            }
            if (gone != null)
            {
                foreach (Thing t in gone)
                {
                    Forget(t);
                }
            }
        }

        // For the harness: the arrival's shell, or null.
        internal static IsoRim.Shell ShellOf(Thing t)
        {
            Arrival a;
            return arrivals.TryGetValue(t, out a) ? a.shell : null;
        }

        internal static int Count => arrivals.Count;
    }
}
