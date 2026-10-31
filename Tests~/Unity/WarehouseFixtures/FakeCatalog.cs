using System;
using System.Collections.Generic;
namespace UnityEngine.ResourceManagement.ResourceProviders
{
    public interface IAssetBundleResource { }
    public sealed class AssetBundleRequestOptions { public string BundleName; }
}
namespace SyntheticFixture
{
    public sealed class ResourceLocation
    {
        public string InternalId;
        public string ProviderId;
        public Type ResourceType;
        public object Data;
        public readonly List<ResourceLocation> Dependencies = new List<ResourceLocation>();
    }
    public sealed class FakeCatalog
    {
        public readonly Dictionary<string, IList<ResourceLocation>> Locations = new Dictionary<string, IList<ResourceLocation>>();
        public bool Locate(object key, Type type, out IList<ResourceLocation> locations)
        {
            return Locations.TryGetValue(key as string ?? "", out locations);
        }
    }
}
