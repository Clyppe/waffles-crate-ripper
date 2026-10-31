using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Waffles.CrateRipper
{
    // Unity 2021's Windows Mono can enumerate a >MAX_PATH file, but then fail
    // to open that same path with DirectoryNotFoundException. Convert only at
    // the OS boundary; Unity asset paths, GUID matching and containment checks
    // must keep ordinary paths, not Windows device-path prefixes.
    internal static class LongPathIO
    {
        internal static string ForIO(string path)
        {
            if (Path.DirectorySeparatorChar != '\\' || string.IsNullOrEmpty(path)) return path;
            var full = Path.GetFullPath(path).Replace('/', '\\');
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            return full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + full.Substring(2)
                : @"\\?\" + full;
        }

        internal static string Logical(string path)
        {
            if (Path.DirectorySeparatorChar != '\\' || path == null) return path;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }
    }

    internal static class LongPathFile
    {
        internal static bool Exists(string path)
        {
            try { return File.Exists(LongPathIO.ForIO(path)); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
        internal static byte[] ReadAllBytes(string path) { return File.ReadAllBytes(LongPathIO.ForIO(path)); }
        internal static string ReadAllText(string path) { return File.ReadAllText(LongPathIO.ForIO(path)); }
        internal static string ReadAllText(string path, Encoding encoding) { return File.ReadAllText(LongPathIO.ForIO(path), encoding); }
        internal static FileStream OpenRead(string path) { return File.OpenRead(LongPathIO.ForIO(path)); }
        internal static FileAttributes GetAttributes(string path) { return File.GetAttributes(LongPathIO.ForIO(path)); }
        internal static void Copy(string source, string destination, bool overwrite) { File.Copy(LongPathIO.ForIO(source), LongPathIO.ForIO(destination), overwrite); }
        internal static void Delete(string path) { File.Delete(LongPathIO.ForIO(path)); }
        internal static void WriteAllText(string path, string text) { File.WriteAllText(LongPathIO.ForIO(path), text); }
        internal static void WriteAllText(string path, string text, Encoding encoding) { File.WriteAllText(LongPathIO.ForIO(path), text, encoding); }
        internal static void WriteAllLines(string path, IEnumerable<string> lines) { File.WriteAllLines(LongPathIO.ForIO(path), lines); }
    }

    internal static class LongPathDirectory
    {
        internal static bool Exists(string path)
        {
            try { return Directory.Exists(LongPathIO.ForIO(path)); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
        internal static DirectoryInfo CreateDirectory(string path) { return Directory.CreateDirectory(LongPathIO.ForIO(path)); }
        internal static DirectoryInfo GetParent(string path) { return Directory.GetParent(LongPathIO.Logical(path)); }
        internal static void Delete(string path, bool recursive) { Directory.Delete(LongPathIO.ForIO(path), recursive); }
        internal static void Move(string source, string destination) { Directory.Move(LongPathIO.ForIO(source), LongPathIO.ForIO(destination)); }
        internal static IEnumerable<string> EnumerateFiles(string path, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
        { return Directory.EnumerateFiles(LongPathIO.ForIO(path), pattern, option).Select(LongPathIO.Logical); }
        internal static string[] GetFiles(string path, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
        { return EnumerateFiles(path, pattern, option).ToArray(); }
        internal static IEnumerable<string> EnumerateDirectories(string path, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
        { return Directory.EnumerateDirectories(LongPathIO.ForIO(path), pattern, option).Select(LongPathIO.Logical); }
        internal static IEnumerable<string> EnumerateFileSystemEntries(string path)
        { return Directory.EnumerateFileSystemEntries(LongPathIO.ForIO(path)).Select(LongPathIO.Logical); }
    }
}
