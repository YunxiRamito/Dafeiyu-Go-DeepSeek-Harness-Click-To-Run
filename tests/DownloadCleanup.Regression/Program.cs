using DeepSeekHarnessLauncher;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Provide an absolute output directory.");
int count = 0;
void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException("FAIL " + name); count++; Console.WriteLine("PASS " + name); }
string output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
string root = Path.Combine(output, "cleanup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string CreateOwned() { string path = Path.Combine(root, ".dsh-bundle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
void CreateDirectoryLink(string path, string target)
{
    try { Directory.CreateSymbolicLink(path, target); }
    catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
    {
        // Junctions exercise the same reparse-point protection without requiring
        // administrator privileges or changing the machine's Developer Mode.
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        string outputText = process.StandardOutput.ReadToEnd(), errorText = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Cannot create test junction: " + outputText + errorText, error);
    }
}
try
{
    string volume = Path.GetPathRoot(root);
    string nonexistent = Path.Combine(volume, ".dsh-bundle-" + Guid.NewGuid().ToString("N"));
    Check(!Directory.Exists(nonexistent), "volume-root cleanup probe is nonexistent and never creates files");
    Check(DownloadSupport.TryDeleteOwnedDirectory(nonexistent, volume, ".dsh-bundle-"), "volume-root parent comparison preserves its directory separator");
    string owned = CreateOwned(); string nested = Path.Combine(owned, "package", "node_modules", "read-only"); Directory.CreateDirectory(nested);
    string file = Path.Combine(nested, "test.dat"); File.WriteAllText(file, "owned"); File.SetAttributes(file, FileAttributes.ReadOnly);
    Check(DownloadSupport.TryDeleteOwnedDirectory(owned + Path.DirectorySeparatorChar, root + Path.DirectorySeparatorChar, ".dsh-bundle-"), "owned tree with read-only package file is removed");
    Check(!Directory.Exists(owned), "read-only deletion leaves no owned directory");
    owned = CreateOwned(); file = Path.Combine(owned, "locked.dat"); File.WriteAllText(file, "locked");
    using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        string message = null;
        Check(!DownloadSupport.TryDeleteOwnedDirectory(owned, root, ".dsh-bundle-", value => message = value), "bounded retries return false for a still-locked file");
        Check(Directory.Exists(owned) && !string.IsNullOrEmpty(message), "cleanup failure remains visible and preserves locked data");
    }
    Check(DownloadSupport.TryDeleteOwnedDirectory(owned, root, ".dsh-bundle-"), "owned tree is removable once file is released");
    owned = CreateOwned(); file = Path.Combine(owned, "transient.dat"); File.WriteAllText(file, "transient");
    var transient = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
    var release = Task.Run(() => { Thread.Sleep(70); transient.Dispose(); });
    Check(DownloadSupport.TryDeleteOwnedDirectory(owned, root, ".dsh-bundle-"), "lightweight IO retries recover a transient file lock"); release.GetAwaiter().GetResult();
    owned = CreateOwned(); File.WriteAllText(Path.Combine(owned, "keep.txt"), "keep");
    Check(!DownloadSupport.TryDeleteOwnedDirectory(owned, output, ".dsh-bundle-") && Directory.Exists(owned), "wrong parent is refused");
    string invalid = Path.Combine(root, ".dsh-bundle-not-a-guid"); Directory.CreateDirectory(invalid);
    Check(!DownloadSupport.TryDeleteOwnedDirectory(invalid, root, ".dsh-bundle-") && Directory.Exists(invalid), "prefix without exact GUID is refused");
    Check(!DownloadSupport.TryDeleteOwnedDirectory(root, output, ""), "empty ownership prefix is refused");
    string namedFile = Path.Combine(root, ".dsh-bundle-" + Guid.NewGuid().ToString("N")); File.WriteAllText(namedFile, "not a directory");
    Check(!DownloadSupport.TryDeleteOwnedDirectory(namedFile, root, ".dsh-bundle-") && File.Exists(namedFile), "owned-looking ordinary file is refused");
    string target = Path.Combine(root, "unowned-target"); Directory.CreateDirectory(target);
    string sentinel = Path.Combine(target, "keep.txt"); File.WriteAllText(sentinel, "outside data"); File.SetAttributes(sentinel, FileAttributes.ReadOnly);
    string linkOwner = CreateOwned(); CreateDirectoryLink(Path.Combine(linkOwner, "external-link"), target);
    Check(DownloadSupport.TryDeleteOwnedDirectory(linkOwner, root, ".dsh-bundle-"), "nested directory link removed without following it");
    Check(File.ReadAllText(sentinel) == "outside data" && (File.GetAttributes(sentinel) & FileAttributes.ReadOnly) != 0, "linked target data and attributes remain unchanged");
    string rootLink = Path.Combine(root, ".dsh-bundle-" + Guid.NewGuid().ToString("N")); CreateDirectoryLink(rootLink, target);
    Check(!DownloadSupport.TryDeleteOwnedDirectory(rootLink, root, ".dsh-bundle-"), "owned-looking root reparse point is refused"); Directory.Delete(rootLink);
    string parentLink = Path.Combine(root, "parent-link"); CreateDirectoryLink(parentLink, target);
    string linkedChild = Path.Combine(target, ".dsh-bundle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(linkedChild);
    Check(!DownloadSupport.TryDeleteOwnedDirectory(Path.Combine(parentLink, Path.GetFileName(linkedChild)), parentLink, ".dsh-bundle-") && Directory.Exists(linkedChild), "reparse-point parent is refused"); Directory.Delete(parentLink);
    File.SetAttributes(sentinel, FileAttributes.Normal);
    Console.WriteLine("PASS " + count + " owned download cleanup checks; no real installation or volume-root file was modified.");
}
finally
{
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, true);
}
