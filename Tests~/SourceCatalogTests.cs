using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Waffles.CrateRipper.Tests
{
    internal static class SourceCatalogTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Pallet indexes distinct crate types and names", PalletCrateTypes));
            tests.Add(new KeyValuePair<string, Action>("Packed level chunks and crate assets are explicit roots", PackedRoots));
            tests.Add(new KeyValuePair<string, Action>("Map and avatar filters recognize SDK and packed type names", CrateFilters));
            tests.Add(new KeyValuePair<string, Action>("Map matching selects scene instead of similarly named prop", MapMatching));
            tests.Add(new KeyValuePair<string, Action>("Avatar matching retains prefab identity and rejects ambiguous matches", AvatarMatching));
            tests.Add(new KeyValuePair<string, Action>("Exported GUID identity wins over changed asset names", GuidMatching));
            tests.Add(new KeyValuePair<string, Action>("Folder names are safe and deterministic", FolderNames));
            tests.Add(new KeyValuePair<string, Action>("ZIP extracts nested bytes", ZipHappyPath));
            tests.Add(new KeyValuePair<string, Action>("ZIP rejects traversal before writing", ZipTraversal));
            tests.Add(new KeyValuePair<string, Action>("ZIP rejects duplicate and overlapping paths", ZipDuplicates));
            tests.Add(new KeyValuePair<string, Action>("ZIP enforces uncompressed size and entry limits", ZipLimits));
            tests.Add(new KeyValuePair<string, Action>("ZIP rejects symlinks", ZipLinks));
            tests.Add(new KeyValuePair<string, Action>("ZIP respects cancellation and requires empty staging", ZipCancellation));
        }

        private static void PalletCrateTypes()
        {
            using (var fixture = new Fixture("pallet"))
            {
                fixture.Write("A/Test.pallet.json", @"{
                    'root': {'ref':'pallet'},
                    'objects': {
                        'pallet': {'title':'Synthetic pallet', 'crates':[
                            {'ref':'spawn','type':'SpawnableCrate'},
                            {'ref':'avatar','type':'AvatarCrate'},
                            {'ref':'level','type':'LevelCrate'}]},
                        'spawn': {'title':'Zed prop','barcode':'test.zed','mainAsset':'11111111111111111111111111111111'},
                        'avatar': {'title':'Able avatar','barcode':'test.avatar','mainAsset':'22222222222222222222222222222222'},
                        'level': {'title':'Middle level','barcode':'test.level','mainAsset':'33333333333333333333333333333333'}
                    }
                }");
                var crates = SourceCatalog.ReadPallets(fixture.Root);
                Check.Equal(3, crates.Count, "All pallet crate references should be indexed.");
                Check.Equal("Able avatar", crates[0].Name, "Crates should be sorted by name.");
                var spawn = crates.Single(c => c.Barcode == "test.zed");
                Check.Equal("SpawnableCrate", spawn.Kind, "Spawnable crate kind should survive parsing.");
                Check.Equal("11111111111111111111111111111111", spawn.MainAssetGuid, "Main asset GUID should survive parsing.");
                Check.Equal("Synthetic pallet", spawn.PalletName, "Pallet identity should survive parsing.");
                Check.Equal(fixture.PathFor("A"), spawn.SourceFolder, "Source folder must identify the pallet's own directory.");
                fixture.Write("Bad.pallet.json", "{}");
                Check.Throws<InvalidDataException>(() => SourceCatalog.ReadPallets(fixture.Root), "Malformed pallet schema should be reported");
            }
        }

        private static void PackedRoots()
        {
            using (var fixture = new Fixture("packed-level"))
            {
                fixture.Write("Level/Test.pallet.json", @"{
                    'root': {'ref':'pallet'}, 'objects': {
                    'pallet': {'title':'Test','crates':[{'ref':'level','type':'crate-level#0'}, {'ref':'avatar','type':'crate-avatar#0'}]},
                    'level': {'title':'Map','mainAsset':'11111111111111111111111111111111','packedAssets':[
                        {'title':'PersistentScenes','subAssets':['22222222222222222222222222222222']},
                        {'title':'ChunkScenes','subAssets':['33333333333333333333333333333333','22222222222222222222222222222222']},
                        {'title':'Ignore duplicate main','guid':'11111111111111111111111111111111'}]},
                    'avatar': {'title':'Avatar','mainAsset':'44444444444444444444444444444444','packedAssets':[
                        {'title':'PreviewMesh','guid':'55555555555555555555555555555555'}]}
                    }}");
                var crates = SourceCatalog.ReadPallets(fixture.Root);
                var level = crates.Single(c => c.Name == "Map");
                Check.Equal(2, level.AdditionalAssets.Count, "Persistent/chunk roots must be deduplicated and must not duplicate the main scene.");
                Check.True(level.AdditionalAssets.Any(a => a.Guid == "33333333333333333333333333333333"), "Chunk scene GUIDs must survive without a direct YAML reference from the main scene.");
                Check.Equal("55555555555555555555555555555555", crates.Single(c => c.Name == "Avatar").AdditionalAssets.Single().Guid, "Avatar packed preview assets must be identified independently of its prefab.");
            }
        }

        private static void CrateFilters()
        {
            foreach (var type in new[] { "LevelCrate", "crate-level#0" })
            {
                var crate = new CrateEntry { Kind = type };
                Check.True(SourceCatalog.MatchesKind(crate, CrateKindFilter.Maps) && SourceCatalog.MatchesKind(crate, CrateKindFilter.All), "Both SDK and packed level names must be visible.");
                Check.True(!SourceCatalog.MatchesKind(crate, CrateKindFilter.Spawnables), "A map must not be offered as a spawnable prefab.");
            }
            foreach (var type in new[] { "AvatarCrate", "crate-avatar#0" })
                Check.True(SourceCatalog.MatchesKind(new CrateEntry { Kind = type }, CrateKindFilter.Avatars), "Both SDK and packed avatar names must be visible.");
        }

        private static void MapMatching()
        {
            using (var fixture = new Fixture("map-root-match"))
            {
                fixture.Asset("Scenes/Test Map.unity", "11111111111111111111111111111111", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
                fixture.Asset("Props/Test Map.prefab", "22222222222222222222222222222222", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
                var crate = new CrateEntry { Name = "Test Map", Kind = "crate-level#0", OriginalAssetPath = "Assets/Source/Test Map.unity" };
                Check.Equal("Scenes/Test Map.unity", SourceCatalog.MatchMainAsset(fixture.Root, crate), "A level root must be a scene even when a prop shares its name.");
                crate.Name = "Missing"; crate.OriginalAssetPath = "Assets/Missing.unity";
                Check.Equal<string>(null, SourceCatalog.MatchMainAsset(fixture.Root, crate), "Unresolved levels must require explicit scene selection, never a random recovered prefab.");
            }
        }

        private static void AvatarMatching()
        {
            using (var fixture = new Fixture("avatar-root-match"))
            {
                fixture.Asset("Avatars/Actor.prefab", "11111111111111111111111111111111", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
                fixture.Asset("Scenes/Actor.unity", "22222222222222222222222222222222", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
                var crate = new CrateEntry { Name = "Actor", Kind = "AvatarCrate", OriginalAssetPath = "Assets/Original/Actor.prefab" };
                Check.Equal("Avatars/Actor.prefab", SourceCatalog.MatchMainAsset(fixture.Root, crate), "Avatar roots must remain prefabs, preserving their hierarchy and rig dependencies for recovery.");
                fixture.Asset("Other/Actor.prefab", "33333333333333333333333333333333", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
                Check.Equal<string>(null, SourceCatalog.MatchMainAsset(fixture.Root, crate), "Duplicate names must require explicit selection.");
                crate.OriginalAssetPath = "Assets/Avatars/Actor.prefab";
                Check.Equal("Avatars/Actor.prefab", SourceCatalog.MatchMainAsset(fixture.Root, crate), "An exact catalog path must disambiguate equally named avatars.");
            }
        }

        private static void GuidMatching()
        {
            using (var fixture = new Fixture("asset-guid-match"))
            {
                fixture.Asset("Changed/Recovered.unity", "11111111111111111111111111111111", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
                fixture.Asset("Scenes/Original.unity", "22222222222222222222222222222222", "%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n");
                var crate = new CrateEntry { Name = "Original", Kind = "LevelCrate", MainAssetGuid = "11111111111111111111111111111111", OriginalAssetPath = "Assets/Scenes/Original.unity" };
                Check.Equal("Changed/Recovered.unity", SourceCatalog.MatchMainAsset(fixture.Root, crate), "A known GUID must be preferred to a similarly named scene.");
            }
        }

        private static void FolderNames()
        {
            Check.Equal("A_B_C_D_E_F_G_H_I_J", SourceCatalog.FolderName("A\\B/C:D*E?F\"G<H>I|J"), "Reserved filename punctuation should be removed.");
            Check.Equal("_CON", SourceCatalog.FolderName("CON"), "Windows reserved device names should be escaped.");
            Check.Equal("_LPT1.txt", SourceCatalog.FolderName("LPT1.txt"), "Windows device names with suffixes should be escaped.");
            Check.Equal("Unnamed crate", SourceCatalog.FolderName(" .. "), "Empty names should receive a useful fallback.");
            Check.True(SourceCatalog.FolderName(new string('a', 200)).Length <= 100, "Long crate names should be bounded.");
        }

        private static void ZipHappyPath()
        {
            using (var fixture = new Fixture("zip-good"))
            {
                var zip = fixture.Zip("good.zip", Fixture.Entry("Mod/", ""), Fixture.Entry("Mod/pallet.json", "synthetic"), Fixture.Entry("Mod/nested/texture.bin", "\0\u0001\u0002"));
                var output = fixture.PathFor("output");
                SourceCatalog.ExtractZip(zip, output, CancellationToken.None);
                Check.Equal("synthetic", File.ReadAllText(Path.Combine(output, "Mod/pallet.json")), "Nested text should extract unchanged.");
                Check.True(File.ReadAllBytes(Path.Combine(output, "Mod/nested/texture.bin")).SequenceEqual(new byte[] { 0, 1, 2 }), "Binary archive bytes should survive extraction.");
            }
        }

        private static void ZipTraversal()
        {
            foreach (var bad in new[] { "../escape.bin", "a/../escape.bin", "a\\..\\escape.bin", "/absolute.bin", "C:/absolute.bin", "a/./b.bin" })
            using (var fixture = new Fixture("zip-traversal"))
            {
                var zip = fixture.Zip("bad.zip", Fixture.Entry("safe.bin", "safe"), Fixture.Entry(bad, "bad"));
                var output = fixture.PathFor("output");
                Check.Throws<InvalidDataException>(() => SourceCatalog.ExtractZip(zip, output, CancellationToken.None), "Unsafe path should be rejected: " + bad);
                Check.True(!Directory.Exists(output), "Unsafe archives should fail before creating staging files.");
                Check.True(!File.Exists(fixture.PathFor("escape.bin")), "No traversal target may be written.");
            }
        }

        private static void ZipDuplicates()
        {
            foreach (var entries in new[] {
                new[] { Fixture.Entry("a.bin", "a"), Fixture.Entry("a.bin", "b") },
                new[] { Fixture.Entry("a.bin", "a"), Fixture.Entry("A.BIN", "b") },
                new[] { Fixture.Entry("a", "a"), Fixture.Entry("a/b", "b") },
                new[] { Fixture.Entry("a/b", "b"), Fixture.Entry("a", "a") }
            })
            using (var fixture = new Fixture("zip-duplicate"))
            {
                var zip = fixture.Zip("bad.zip", entries);
                var output = fixture.PathFor("output");
                Check.Throws<InvalidDataException>(() => SourceCatalog.ExtractZip(zip, output, CancellationToken.None), "Duplicate paths or file/folder aliases must fail");
                Check.True(!Directory.Exists(output), "Invalid archive aliases must be detected before any writes.");
            }
        }

        private static void ZipLimits()
        {
            using (var fixture = new Fixture("zip-limits"))
            {
                var zip = fixture.Zip("bomb.zip", new KeyValuePair<string, byte[]>("compressed.bin", new byte[2 * 1024 * 1024]));
                Check.Throws<InvalidDataException>(() => SourceCatalog.ExtractZip(zip, fixture.PathFor("bytes"), CancellationToken.None, maximumBytes: 1024), "Highly compressed data must obey the expanded size limit");
                var many = fixture.Zip("many.zip", Fixture.Entry("one", "1"), Fixture.Entry("two", "2"));
                Check.Throws<InvalidDataException>(() => SourceCatalog.ExtractZip(many, fixture.PathFor("entries"), CancellationToken.None, maximumEntries: 1), "Entry count must be bounded");
                Check.True(!Directory.Exists(fixture.PathFor("bytes")) && !Directory.Exists(fixture.PathFor("entries")), "Limit failures must not unpack partial data.");
            }
        }

        private static void ZipLinks()
        {
            using (var fixture = new Fixture("zip-link"))
            {
                var path = fixture.PathFor("link.zip");
                using (var file = File.Create(path))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("link");
                    entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                    using (var output = new StreamWriter(entry.Open())) output.Write("../outside");
                }
                Check.Throws<InvalidDataException>(() => SourceCatalog.ExtractZip(path, fixture.PathFor("output"), CancellationToken.None), "Unix symlink entries must fail");
            }
        }

        private static void ZipCancellation()
        {
            using (var fixture = new Fixture("zip-cancel"))
            {
                var zip = fixture.Zip("good.zip", Fixture.Entry("file", "data"));
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    Check.Throws<OperationCanceledException>(() => SourceCatalog.ExtractZip(zip, fixture.PathFor("output"), cancellation.Token), "Cancellation must stop archive work");
                }
                fixture.Write("not-empty/keep.txt", "keep");
                Check.Throws<IOException>(() => SourceCatalog.ExtractZip(zip, fixture.PathFor("not-empty"), CancellationToken.None), "Existing staging content must not be overwritten");
                Check.Equal("keep", File.ReadAllText(fixture.PathFor("not-empty/keep.txt")), "Existing staging data must survive rejection.");
            }
        }
    }
}
