using System;
using System.Xml;
using Verse;

namespace IsoRimVGEPack
{
    // Runs its operation (apply) only when IsoRim Waylith's Api.Version is at least minVersion; otherwise does nothing (and
    // counts as done, so no patch error). For ModelDef fields an older IsoRim does not know (Patches/VGE_Models_B1b.xml:
    // bobByState, Api 27): added as XML only when IsoRim has them, so the pack loads either way.
    public class PatchOperation_IfIsoRimApi : PatchOperation
    {
        public int minVersion;
        public PatchOperation apply;

        protected override bool ApplyWorker(XmlDocument xml)
        {
            int have;
            try
            {
                have = IsoRim.Api.Version;
            }
            catch (Exception)
            {
                have = 0;
            }
            if (have < minVersion || apply == null)
            {
                return true;
            }
            return apply.Apply(xml);
        }
    }
}
