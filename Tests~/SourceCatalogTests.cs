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
