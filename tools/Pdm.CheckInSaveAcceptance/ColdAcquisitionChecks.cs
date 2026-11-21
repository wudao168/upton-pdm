using System;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    private sealed class ColdAcquisition
    {
        internal static readonly string[] Cases = {
            "cold-occurrence-verified", "cold-occurrence-open-latest-edit", "cold-occurrence-complete-index", "cold-occurrence-extra-instance",
            "cold-occurrence-configuration-change", "cold-occurrence-different-object", "cold-occurrence-already-open",
            "cold-occurrence-wrong-loaded-path", "cold-occurrence-missing-file", "cold-occurrence-local-disk-edit",
            "cold-occurrence-foreign-memory-edit", "cold-occurrence-outside-manifest", "cold-occurrence-replaced-instance",
            "cold-occurrence-reference-race","cold-occurrence-dirty-at-open","cold-occurrence-stamp-race",
            "cold-occurrence-no-root-inventory","cold-occurrence-inventory-id-conflict","cold-occurrence-no-inventory-flag"
        };

        private readonly string mode;
        private bool loaded;
        private bool preference;
        private int opens;
        private int stamp;
        private bool opening;

        internal int ReadStamp()=>opening&&mode=="cold-occurrence-stamp-race"?++stamp:stamp;

        internal ColdAcquisition(string mode)
        {
            this.mode = mode;
            loaded = mode == "cold-occurrence-already-open";
        }

        internal bool NativeCall(IMethodCallMessage call, object owner, out object result)
        {
            result = null;
            switch (call.MethodName)
            {
                case "GetDocuments":
                    if (loaded) return false;
                    result = new object[0]; return true;
                case "get_ActiveDoc": result = loaded ? owner : null; return true;
                case "GetDocumentDependencies2": result = new string[0]; return true;
                case "OpenDoc6": loaded = true; opens++; result = owner; return true;
                case "GetCurrentWorkingDirectory":
                case "GetSearchFolders": result = ""; return true;
                case "SetCurrentWorkingDirectory":
                case "SetSearchFolders": result = true; return true;
                case "GetUserPreferenceToggle": result = preference; return true;
                case "SetUserPreferenceToggle": preference = (bool)call.Args[1]; result = true; return true;
                default: return false;
            }
        }

        internal void Open(object plugin, object owner, string path, object manifest)
        {
            object opened;
            opening = true;
            try { opened = TreeCall(plugin, "OpenControlledReadOnlyDocument", path, 2, "Default", manifest); }
            finally { opening = false; }
            var expectedOpens = mode == "cold-occurrence-already-open" ? 0 : 1;
            TreeCheck(ReferenceEquals(opened, owner) && opens == expectedOpens, "打开只读总装的窗口或原生打开次数不符");
        }
    }
}
