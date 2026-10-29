using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRimVGEPack
{
    // (IsoRim Api 37) The effects VGE's DrawGravship draws round its arriving ship, in 3D at the shell's pose: thruster flames
    // with their lens flares, the grav engine's and grav field extenders' glows, and the downwash as dust thrown up on the
    // ground round the footprint. Same materials, timing and strength as VGE (LandingStructureBase.DrawGravship, the 1.7
    // build); placed as IsoRim's own gravship flight places them (ShipFlight.Effects): flames stood round their thrust at
    // nozzle height, glows stood facing the camera, flares at the flames' 3D points (IsoRim.Api flight effect cards).
    // Not drawn in 3D: VGE's screen-filling downwash and heat distortion layers (laid over the whole view, a tint before a
    // landing), its flat shadow (the shell's IsoRim shadow instead) and its exhaust smoke puffs (its own fleck system, flat).
    // What to draw is read on VGE's temporary map at capture time (thrusters, engine, extenders, VGE's scan), in shell space.
    internal static class ShipArrivalEffects
    {
        internal sealed class Flame
        {
            public Vector3 local;       // the flame's centre, relative to the capture centre (map axes)
            public Rot4 rot;            // the thruster's rotation
            public float size;
            public Material mat;
            public List<ShaderParameter> parameters;
        }

        internal sealed class Data
        {
            public readonly List<Flame> flames = new List<Flame>();
            public readonly List<Vector3> extenders = new List<Vector3>();   // relative to the capture centre
            public Vector3? engine;                                          // relative to the capture centre
            public readonly List<Vector3> edge = new List<Vector3>();   // the hull's outline cells, relative to the capture centre
            public float dustDebt;      // dust puffs owed (Dust)
        }

        private const float NozzleShare = 0.5f;      // as ShipFlight: flames at half the wall height over the deck
        private const float OrbHeight = 1.2f;        // the engine glow over the deck
        private const float ExtenderHeight = 0.6f;   // an extender's glow over the deck

        private static AccessTools.FieldRef<object, IntVec3> enginePos;
        private static AccessTools.FieldRef<object, List<IntVec3>> extenderPositions;
        private static FieldInfo thrustersField;
        private static Type enemyThrusterType;
        private static FieldInfo etFlameSize, etOffsets, etParameters;
        private static PropertyInfo etShader;
        private static Material matLensFlare, matExtenderGlow, matEngineGlow;
        private static readonly MaterialPropertyBlock flareBlock = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock flameBlock = new MaterialPropertyBlock();
        private static readonly int Color2 = Shader.PropertyToID("_Color2");
        private static bool ready;

        // For the harness: effect draws made.
        internal static int FlameDraws, GlowDraws, ScreenDraws;

        internal static void Resolve(Type baseType)
        {
            try
            {
                enginePos = AccessTools.FieldRefAccess<IntVec3>(baseType, "enginePos");
                extenderPositions = AccessTools.FieldRefAccess<List<IntVec3>>(baseType, "gravFieldExtenderPositions");
                thrustersField = AccessTools.Field(baseType, "thrusters");
                enemyThrusterType = AccessTools.TypeByName("VanillaGravshipExpanded.EnemyThrusterExtension");
                if (enemyThrusterType != null)
                {
                    etFlameSize = AccessTools.Field(enemyThrusterType, "flameSize");
                    etOffsets = AccessTools.Field(enemyThrusterType, "flameOffsetsPerDirection");
                    etParameters = AccessTools.Field(enemyThrusterType, "flameShaderParameters");
                    etShader = AccessTools.Property(enemyThrusterType, "FlameShaderType");
                }
                matLensFlare = Mat(baseType, "MatGravshipLensFlare");
                matExtenderGlow = Mat(baseType, "MatGravFieldExtenderGlow");
                matEngineGlow = Mat(baseType, "MatGravEngineGlow");
                ready = thrustersField != null;
            }
            catch (Exception e)
            {
                Log.Warning(Pack.Tag + "VGE's arrival effects not found (" + e.Message + "): enemy ships fly in without flames and glows in 3D.");
                ready = false;
            }
            if (!ready)
            {
                Log.Warning(Pack.Tag + "VGE's arrival effects not found: enemy ships fly in without flames and glows in 3D.");
            }
        }

        private static Material Mat(Type t, string name)
        {
            FieldInfo f = AccessTools.Field(t, name);
            return f != null ? f.GetValue(null) as Material : null;
        }

        // Right after VGE's scan and photo, on the temporary map: what VGE will draw round the ship, relative to the capture
        // centre (the shell's origin).
        internal static Data Capture(Thing structure, Vector3 centre, CellRect bounds, Map tempMap)
        {
            if (!ready)
            {
                return null;
            }
            Data d = new Data();
            Outline(d, tempMap, bounds, centre);
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            if (thrustersField.GetValue(structure) is IEnumerable thrusters)
            {
                foreach (object o in thrusters)
                {
                    Thing t = o as Thing;
                    if (t == null || !t.Spawned || !seen.Add(t.Position))
                    {
                        continue;   // a thruster of an earlier capture (VGE keeps adding), or gone
                    }
                    float flameSize;
                    List<Vector3> offsets;
                    ShaderTypeDef shader;
                    List<ShaderParameter> parameters;
                    CompGravshipThruster comp = t.TryGetComp<CompGravshipThruster>();
                    if (comp != null)
                    {
                        CompProperties_GravshipThruster props = comp.Props;
                        flameSize = props.flameSize;
                        offsets = props.flameOffsetsPerDirection;
                        shader = props.FlameShaderType;
                        parameters = props.flameShaderParameters;
                    }
                    else
                    {
                        object ext = enemyThrusterType != null ? Ext(t.def, enemyThrusterType) : null;
                        if (ext == null)
                        {
                            continue;
                        }
                        flameSize = (float)etFlameSize.GetValue(ext);
                        offsets = etOffsets.GetValue(ext) as List<Vector3>;
                        shader = etShader.GetValue(ext) as ShaderTypeDef;
                        parameters = etParameters.GetValue(ext) as List<ShaderParameter>;
                    }
                    if (shader == null || shader.Shader == null)
                    {
                        continue;
                    }
                    // ---- VGE DrawThrusterFlame
                    float size = t.def.size.x * flameSize;
                    Rot4 rot = t.Rotation;
                    Vector3 offset = offsets != null && offsets.Count > rot.AsInt ? rot.AsQuat * offsets[rot.AsInt] : Vector3.zero;
                    Vector3 at = GenThing.TrueCenter(t.Position, rot, t.def.size, 0f) - rot.AsIntVec3.ToVector3() * (t.def.size.z * 0.5f + size * 0.5f) + offset;
                    // ---- end VGE
                    MaterialRequest req = new MaterialRequest(shader.Shader);
                    req.renderQueue = 3201;
                    d.flames.Add(new Flame
                    {
                        local = new Vector3(at.x - centre.x, 0f, at.z - centre.z), rot = rot, size = size, mat = MaterialPool.MatFrom(req),
                        parameters = parameters ?? new List<ShaderParameter>()
                    });
                }
            }
            HashSet<IntVec3> ext2 = new HashSet<IntVec3>();
            List<IntVec3> extenders = extenderPositions(structure);
            if (extenders != null)
            {
                Vector2 half = ThingDefOf.GravFieldExtender.graphicData.drawSize * 0.5f;
                foreach (IntVec3 c in extenders)
                {
                    if (ext2.Add(c))
                    {
                        d.extenders.Add(new Vector3(c.x + half.x - centre.x, 0f, c.z + half.y - centre.z));
                    }
                }
            }
            IntVec3 e = enginePos(structure);
            if (e.IsValid && e.x >= 0 && e.z >= 0)
            {
                d.engine = new Vector3(e.x + 0.5f - centre.x, 0f, e.z + 0.5f - centre.z);
            }
            return d;
        }

        // Dust puffs (the game's thick dust puff, drawn by IsoRim as standing smoke) along the footprint's edge, from when
        // the ship is a few cells up until it rests; more as it nears the ground. Spawned by game time, so none pile up while
        // paused, and more at higher speeds.
        private const float DustFrom = 8f;           // cells of height where the dust starts
        private const float DustPerCellSecond = 1.5f;   // puffs per cell of edge per second at full strength
        private static readonly Color DustColour = new Color(0.78f, 0.71f, 0.58f, 1f);
        internal static int DustPuffs;

        private static void Dust(Data d, Map map, Vector3 ground, float height, Quaternion turn, float num)
        {
            if (Find.TickManager == null || Find.TickManager.Paused || height > DustFrom || d.edge.Count == 0)
            {
                return;
            }
            float strength = Mathf.InverseLerp(DustFrom, 0f, height) * Mathf.InverseLerp(0.3f, 0.9f, num);
            d.dustDebt += d.edge.Count * DustPerCellSecond * strength * Time.deltaTime * Find.TickManager.TickRateMultiplier;
            int n = Mathf.Min(16, Mathf.FloorToInt(d.dustDebt));
            d.dustDebt -= n;
            if (d.dustDebt > 16f)
            {
                d.dustDebt = 0f;
            }
            for (int i = 0; i < n; i++)
            {
                // a cell on the hull's outline, thrown a little out from it
                Vector3 e = d.edge[Rand.Range(0, d.edge.Count)];
                Vector2 jitter = Rand.InsideUnitCircle * 1.5f;
                Vector3 p = turn * new Vector3(e.x + jitter.x, 0f, e.z + jitter.y);
                Vector3 at = new Vector3(ground.x + p.x, 0f, ground.z + p.z);
                if (!at.ToIntVec3().InBounds(map))
                {
                    continue;
                }
                FleckMaker.ThrowDustPuffThick(at, map, Rand.Range(2.5f, 4.5f) * (0.6f + 0.4f * strength), DustColour);
                DustPuffs++;
            }
        }

        // The ship's cells (substructure, foundations, buildings) on the temporary map that have an open side: its outline.
        private static void Outline(Data d, Map map, CellRect bounds, Vector3 centre)
        {
            if (map == null)
            {
                return;
            }
            Func<IntVec3, bool> inside = c => c.InBounds(map) && bounds.Contains(c)
                && (map.terrainGrid.FoundationAt(c) != null || map.terrainGrid.TerrainAt(c).IsSubstructure || c.GetEdifice(map) != null);
            foreach (IntVec3 c in bounds)
            {
                if (!inside(c))
                {
                    continue;
                }
                for (int i = 0; i < 4; i++)
                {
                    if (!inside(c + GenAdj.CardinalDirections[i]))
                    {
                        d.edge.Add(new Vector3(c.x + 0.5f - centre.x, 0f, c.z + 0.5f - centre.z));
                        break;
                    }
                }
            }
        }

        private static object Ext(ThingDef def, Type type)
        {
            if (def.modExtensions == null)
            {
                return null;
            }
            foreach (DefModExtension x in def.modExtensions)
            {
                if (type.IsInstanceOfType(x))
                {
                    return x;
                }
            }
            return null;
        }

        // Every frame the shell is drawn: num is VGE's remapped progress (0-1), num2 its height share ((1 - num)^5).
        internal static void Draw(Data d, Map map, Vector3 ground, float height, float yaw, float num, float num2)
        {
            if (d == null)
            {
                return;
            }
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);

            // ---- VGE's downwash and heat distortion are screen-filling layers: in 3D they tinted the whole view (the user's
            // "yellow overlay" before a landing). Not drawn. The downwash becomes dust thrown up on the ground round the
            // footprint as the ship comes down (not in a vacuum, as VGE's), growing with VGE's progress.
            if (num > 0f && map != null && !map.Biome.inVacuum)
            {
                Dust(d, map, ground, height, turn, num);
            }
            if (d.flames.Count == 0)
            {
                return;   // as VGE: no thrusters, no flames and no glows
            }
            Color value = new Color(1f, 1f, 1f, 1f);
            value *= Mathf.Lerp(0.75f, 1f, Mathf.PerlinNoise1D(num * 100f));
            value.a = Mathf.InverseLerp(0f, 0.1f, 1f - num);
            if (value.a <= 0f)
            {
                return;
            }

            // ---- thruster flames, stood round their thrust at nozzle height, each with VGE's lens flare
            float nozzle = height + IsoRim.Api.WallHeight * NozzleShare;
            if (matLensFlare != null)
            {
                matLensFlare.SetColor(Color2, value);
            }
            foreach (Flame f in d.flames)
            {
                Vector3 p = turn * f.local;
                Vector3 at = new Vector3(ground.x + p.x, nozzle, ground.z + p.z);
                flameBlock.Clear();
                flameBlock.SetColor(Color2, value);
                foreach (ShaderParameter sp in f.parameters)
                {
                    sp.Apply(flameBlock);
                }
                IsoRim.Api.DrawAxialCard(at, turn * f.rot.AsQuat * Vector3.forward, f.size, f.mat, flameBlock);
                FlameDraws++;
                if (matLensFlare != null)
                {
                    flareBlock.SetVector(ShaderPropertyIDs.DrawPos, IsoRim.Api.WorldToViewport(at));
                    IsoRim.Api.DrawScreenCard(matLensFlare, flareBlock);
                    ScreenDraws++;
                }
            }

            // ---- the grav field extenders' and the engine's glows, stood facing the camera
            if (matExtenderGlow != null)
            {
                matExtenderGlow.SetColor(Color2, value);
                foreach (Vector3 e in d.extenders)
                {
                    Vector3 p = turn * e;
                    IsoRim.Api.DrawGlowCard(new Vector3(ground.x + p.x, height + ExtenderHeight, ground.z + p.z), 8f, matExtenderGlow);
                    GlowDraws++;
                }
            }
            if (matEngineGlow != null && d.engine.HasValue)
            {
                matEngineGlow.SetColor(Color2, value);
                Vector3 p = turn * d.engine.Value;
                IsoRim.Api.DrawGlowCard(new Vector3(ground.x + p.x, height + OrbHeight, ground.z + p.z), 12.5f, matEngineGlow);
                GlowDraws++;
            }
        }
    }
}
