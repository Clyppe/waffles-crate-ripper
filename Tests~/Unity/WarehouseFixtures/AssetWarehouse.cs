using System.Collections.Generic;
using UnityEngine;
namespace SLZ.Marrow.Warehouse
{
    public sealed class PalletManifest
    {
        public string PalletBarcode;
        public string PalletPath;
        public string CatalogPath;
        public object Catalog;
    }
    public sealed class AssetWarehouse
    {
        public static AssetWarehouse Instance { get; set; }
        public static bool ready = true;
        public readonly List<Object> Crates = new List<Object>();
        public readonly Dictionary<string, Pallet> PalletRegistry = new Dictionary<string, Pallet>();
        public readonly List<PalletManifest> Manifests = new List<PalletManifest>();
        public bool IsReady { get { return true; } }
        public int CrateCount() { return Crates.Count; }
        public IEnumerable<Object> GetCrates() { return Crates; }
        public IEnumerable<PalletManifest> GetPalletManifests() { return Manifests; }
    }
}
