using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeepSeekHarnessLauncher
{
    internal static class DroppedFilePolicyFixture
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        internal static int Run()
        {
            int checks = 0;
            void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
            void Refused(Action action, string message)
            {
                bool failed = false; try { action(); } catch { failed = true; }
                Check(failed, message);
            }
            string qa = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_DROP_QA");
            if (String.IsNullOrWhiteSpace(qa) || !Path.IsPathFullyQualified(qa) || !Path.GetPathRoot(qa).TrimEnd((char)92).Equals("G:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("G drive isolated QA required");
            string directory = Path.Combine(qa, "lease-policy"), renamed = directory + "-renamed";
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "普通附件 ' abc.custom");
            if (File.Exists(path))
            {
                var existing = new FileInfo(path).GetAccessControl();
                existing.RemoveAccessRuleAll(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ReadData, AccessControlType.Deny));
                new FileInfo(path).SetAccessControl(existing);
            }
            File.WriteAllText(path, "readable bytes");
            string junction = directory + "-junction";
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(directory);
            using (var process = Process.Start(start)) { process.WaitForExit(); Check(process.ExitCode == 0, "create isolated junction"); }
            try { Refused(() => DshDroppedFiles.Acquire(new[] { Path.Combine(junction, Path.GetFileName(path)) }, 64, DshDroppedFiles.MaximumBytes).Dispose(), "reject reparse ancestor"); }
            finally { Directory.Delete(junction); }
            using (var lease = DshDroppedFiles.Acquire(new[] { path, path }, 64, DshDroppedFiles.MaximumBytes))
            {
                Check(lease.Paths.Length == 1 && lease.Bytes > 0, "deduplicate arbitrary extension");
                Refused(() => File.WriteAllText(path, "replaced"), "immutable file bytes");
                Refused(() => File.Delete(path), "file cannot delete");
                Refused(() => Directory.Move(directory, renamed), "ancestor cannot rename");
                using (var writeDirectory = CreateFile(directory, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
                    Check(writeDirectory.IsInvalid && Marshal.GetLastWin32Error() == 32, "ancestor write/reparse access refused by sharing lock");
            }
            File.WriteAllText(path, "after release"); Check(File.ReadAllText(path) == "after release", "file unlocked after lease ends");
            Directory.Move(directory, renamed); Directory.Move(renamed, directory); Check(true, "ancestor unlocked after lease ends");
            Refused(() => DshDroppedFiles.Acquire(new[] { directory }, 64, DshDroppedFiles.MaximumBytes).Dispose(), "reject directory");
            Refused(() => DshDroppedFiles.Acquire(new[] { "relative.txt" }, 64, DshDroppedFiles.MaximumBytes).Dispose(), "reject relative path");
            Refused(() => DshDroppedFiles.Acquire(new[] { path + ":secret" }, 64, DshDroppedFiles.MaximumBytes).Dispose(), "reject alternate stream");
            Refused(() => DshDroppedFiles.Acquire(new[] { path }, 0, DshDroppedFiles.MaximumBytes).Dispose(), "reject per document count overflow");
            Refused(() => DshDroppedFiles.Acquire(new[] { path }, 64, 0).Dispose(), "reject per document bytes overflow");
            var many = new string[65]; Array.Fill(many, path);
            Refused(() => DshDroppedFiles.Acquire(many, 64, DshDroppedFiles.MaximumBytes).Dispose(), "reject batch overflow");
            var security = new FileInfo(path).GetAccessControl();
            string original = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ReadData, AccessControlType.Deny));
                new FileInfo(path).SetAccessControl(security);
                Refused(() => DshDroppedFiles.Acquire(new[] { path }, 64, DshDroppedFiles.MaximumBytes).Dispose(), "reject unreadable current user file");
            }
            finally
            {
                var restore = new FileSecurity(); restore.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access); new FileInfo(path).SetAccessControl(restore);
            }
            using (var lease = DshDroppedFiles.Acquire(new[] { path }, 64, DshDroppedFiles.MaximumBytes)) Check(lease.Paths.Length == 1, "denial failure released handles");
            Console.WriteLine("Dropped file policy: " + checks + " checks passed. Elevated linked token branch requires elevated fixture coverage.");
            return 0;
        }
    }
}
