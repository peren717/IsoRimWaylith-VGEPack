using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace IsoRimVGEPackShared
{
    // Which VGE chapters are here, one answer for both parts of the pack (compiled into each). A chapter is found through one of
    // its own types: the mod that loaded that type's assembly is the chapter's ModContentPack (no package ids). Found once, on
    // first use after startup; null for a chapter that is not loaded.
    // If VGE renames that type while the chapter's code is loaded (other types of its namespace are there), the chapter is
    // found through those instead, and the rename is noted in Missing, which each part puts in its own "not found" note.
    internal static class Vge
    {
        private const string Chapter1Type = "VanillaGravshipExpanded.VGEDefOf";
        private const string Chapter2Namespace = "VanillaGravshipExpanded2";
        private const string Chapter2Type = Chapter2Namespace + ".InternalDefOf";
        private static ModContentPack chapter1, chapter2;
        private static readonly List<string> missing = new List<string>();
        private static bool found;

        internal static ModContentPack Chapter1
        {
            get
            {
                Find();
                return chapter1;
            }
        }

        internal static ModContentPack Chapter2
        {
            get
            {
                Find();
                return chapter2;
            }
        }

        // The chapter types not found although their chapter's code is loaded (a rename on VGE's side), for the "not found"
        // notes.
        internal static List<string> Missing
        {
            get
            {
                Find();
                return missing;
            }
        }

        // Is this mod one of VGE's chapters?
        internal static bool Owns(ModContentPack mod) => mod != null && (mod == Chapter1 || mod == Chapter2);

        // A type name of Chapter 2's while Chapter 2 is not loaded: nothing of it to reach.
        internal static bool MissingChapter2(string typeName) => typeName.StartsWith(Chapter2Namespace + ".", StringComparison.Ordinal) && Chapter2 == null;

        private static void Find()
        {
            if (found)
            {
                return;
            }
            found = true;
            chapter1 = ModOf(Chapter1Type);
            chapter2 = ModOf(Chapter2Type);
        }

        // The mod that loaded typeName's assembly. Without that type: the mod that loaded another type of its namespace (noted
        // in Missing), or null when no such type is loaded (the chapter is not here).
        private static ModContentPack ModOf(string typeName)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null)
            {
                string ns = typeName.Substring(0, typeName.LastIndexOf('.'));
                foreach (Type other in GenTypes.AllTypes)
                {
                    if (other.Namespace == ns)
                    {
                        t = other;
                        missing.Add(typeName.Substring(ns.Length + 1) + " (VGE's " + ns + " is loaded; found through " + other.Name + ")");
                        break;
                    }
                }
                if (t == null)
                {
                    return null;
                }
            }
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies.loadedAssemblies.Contains(t.Assembly))
                {
                    return mod;
                }
            }
            return null;
        }
    }
}
