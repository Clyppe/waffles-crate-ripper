using System.Collections.Generic;

namespace Waffles.CrateRipper
{
    public sealed class ScriptBinding
    {
        public string FullTypeName;
        public string Guid;
        public long FileId;
    }

    public sealed class ShaderBinding
    {
        public string Name;
        public string Guid;
        public long FileId;
    }

    public sealed class RecoveryReport
    {
        public int copiedFiles;
        public int repairedScriptReferences;
        public int reusedShaders;
        public List<string> warnings = new List<string>();
        public List<string> missingScripts = new List<string>();
        public List<string> prefabs = new List<string>();
        public List<string> recoveredSourceFiles = new List<string>();
        public List<string> quarantinedFiles = new List<string>();
        public List<string> missingAssetReferences = new List<string>();
        public string outputFolder;
    }
}
