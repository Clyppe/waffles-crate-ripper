using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Waffles.CrateRipper.Tests
{
    internal static class BackendTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Backend never erases existing export folders or source ancestors", FreshExportGuard));
        }

        private static void FreshExportGuard()
        {
            using (var fixture = new Fixture("backend-preflight"))
            {
                var executable = fixture.Write("not-an-executable", "This file must never launch.");
                Directory.CreateDirectory(fixture.PathFor("source"));
                var existing = fixture.Write("non-empty/keep.txt", "keep");
                Check.Throws<IOException>(() => AssetRipperBackend.ExportAsync(executable, fixture.PathFor("source"), fixture.PathFor("non-empty"), null, CancellationToken.None).GetAwaiter().GetResult(), "Nonempty output must fail before launching extraction");
                Check.Equal("keep", File.ReadAllText(existing), "Existing export content must remain unchanged.");
                Check.Throws<IOException>(() => AssetRipperBackend.ExportAsync(executable, fixture.PathFor("source"), fixture.Root, null, CancellationToken.None).GetAwaiter().GetResult(), "An ancestor of the source must not be an export target");
                Check.Throws<IOException>(() => AssetRipperBackend.ExportAsync(executable, fixture.PathFor("source"), fixture.PathFor("source"), null, CancellationToken.None).GetAwaiter().GetResult(), "The source must not be its own export target");
                Check.Throws<IOException>(() => AssetRipperBackend.ExportAsync(executable, fixture.PathFor("source"), fixture.PathFor("Assets"), null, CancellationToken.None).GetAwaiter().GetResult(), "A project asset root must not be an export target");
                Check.True(!Directory.Exists(fixture.PathFor("Assets")), "Rejected targets must not be created.");
            }
        }
    }
}
