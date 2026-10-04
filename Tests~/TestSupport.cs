using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Waffles.CrateRipper.Tests
{
    internal static class Check
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + " Expected: " + expected + "; actual: " + actual);
        }

        public static T Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T exception) { return exception; }
            catch (Exception exception) { throw new InvalidOperationException(message + ": wrong exception " + exception.GetType().Name, exception); }
            throw new InvalidOperationException(message + ": operation unexpectedly succeeded");
        }

        public static void SameBytes(string first, string second, string message)
        {
            True(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)), message);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; private set; }
        public Fixture(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), "WafflesCrateRipperTests", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string PathFor(string relative)
        {
            return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        public string Write(string relative, string content)
        {
            string path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public string WriteBytes(string relative, byte[] bytes)
        {
            string path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public string Asset(string relative, string guid, string text)
        {
            string path = Write(relative, text);
            Write(relative + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
            return path;
        }

        public string Zip(string relative, params KeyValuePair<string, byte[]>[] entries)
        {
            string path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var pair in entries)
                {
                    var entry = zip.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    using (var output = entry.Open()) output.Write(pair.Value, 0, pair.Value.Length);
                }
            return path;
        }

        public static KeyValuePair<string, byte[]> Entry(string name, string value)
        {
            return new KeyValuePair<string, byte[]>(name, Encoding.UTF8.GetBytes(value));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
