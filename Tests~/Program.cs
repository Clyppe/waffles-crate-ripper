using System;
using System.Collections.Generic;

namespace Waffles.CrateRipper.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            var cases = new List<KeyValuePair<string, Action>>();
            SourceCatalogTests.Register(cases);
            BackendTests.Register(cases);
            RecoveryTests.Register(cases);
            int failed = 0;
            foreach (var test in cases)
            {
                try
                {
                    test.Value();
                    Console.WriteLine("PASS " + test.Key);
                }
                catch (Exception exception)
                {
                    failed++;
                    Console.Error.WriteLine("FAIL " + test.Key + "\n" + exception);
                }
            }
            Console.WriteLine((cases.Count - failed) + "/" + cases.Count + " checks passed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
