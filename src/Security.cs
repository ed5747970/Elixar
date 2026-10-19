// Security: signatures, fingerprints, locked folders, link-safe deletion, running without admin rights,
// and the protected copy in Program Files used by the schedules.

using System;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CDriveCleaner
{
    public static class Win
    {
        public static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        // Program Files, from Windows' own record in HKLM (only administrators can change it). The %ProgramFiles% and
        // %ProgramW6432% variables can be redefined by any program you run (in HKCU\Environment), and Elixar runs as
        // administrator: a redefined variable could make it install, or start, a copy in a folder that program controls.
        public static string ProgramFiles { get { return Pf("ProgramW6432Dir", "ProgramFilesDir", Environment.SpecialFolder.ProgramFiles, @"C:\Program Files"); } }
        public static string ProgramFilesX86 { get { return Pf("ProgramFilesDir (x86)", "ProgramFilesDir", Environment.SpecialFolder.ProgramFilesX86, @"C:\Program Files (x86)"); } }

        static string Pf(string name, string fallbackName, Environment.SpecialFolder folder, string last)
        {
            if (IsWindows)
                try
                {
                    using (var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Default))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion"))
                    {
                        foreach (var n in new[] { name, fallbackName })
                        {
                            string v = k == null ? null : k.GetValue(n, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                            if (!string.IsNullOrEmpty(v) && Path.IsPathRooted(v) && !v.Contains("%")) return v.TrimEnd('\\');
                        }
                    }
                }
                catch { }
            try { string f = Environment.GetFolderPath(folder); if (!string.IsNullOrEmpty(f)) return f.TrimEnd('\\'); } catch { }
            return last;
        }

        // The full path of a Windows tool. Starting "powershell.exe" by name makes Windows look in Elixar's own folder
        // and the current folder first, so a same-named file planted next to a downloaded Elixar.exe would run with
        // administrator rights. Elixar always names the copy in the Windows folder instead.
        public static string SystemExe(string name)
        {
            if (!IsWindows || string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return name;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return System.IO.Path.Combine(win, name);
            string sys = System.IO.Path.Combine(win, Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32");
            if (name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)) return System.IO.Path.Combine(System.IO.Path.Combine(sys, @"WindowsPowerShell\v1.0"), name);
            return System.IO.Path.Combine(sys, name);
        }
    }

    // ============================ file fingerprints and digital signatures ============================
    public static class Trust
    {
        public static string Sha256(string file)
        {
            using (var s = File.OpenRead(file))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData, pwszURLReference;
            public uint dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        // true when the file carries a valid Authenticode signature (checked by Windows, offline)
        public static bool HasValidSignature(string file)
        {
            if (!Win.IsWindows || !File.Exists(file)) return false;
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = file };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,              // no UI
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1,           // a file
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0x1000 | 0x80  // cache-only URL retrieval, no revocation network check
                };
                return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data) == 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pFile); }
        }

        // the signer's name (CN), read properly even when it contains a comma ("Docker, Inc.")
        public static string Signer(string file)
        {
            try
            {
                var c = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
                string n = c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                return string.IsNullOrEmpty(n) ? CN(c.Subject) : n.Trim();
            }
            catch { return ""; }
        }

        public static string CN(string subject)
        {
            foreach (var part in (subject ?? "").Split(','))
            {
                var p = part.Trim();
                if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p.Substring(3).Trim('"');
            }
            return (subject ?? "").Trim();
        }

        // the signature is valid and the signer is exactly one of these publishers
        // (not just a name that contains the word: "Not Google Ltd" or "Googly Tools" don't count)
        public static bool SignedBy(string file, params string[] publishers)
        {
            return HasValidSignature(file) && IsPublisher(Signer(file), publishers);
        }

        public static bool IsPublisher(string signer, params string[] publishers)
        {
            Func<string, string> norm = x => (x ?? "").Trim().TrimEnd('.').Replace(",", "").ToLowerInvariant();
            string s = norm(signer);
            return s.Length > 0 && publishers.Any(p => norm(p) == s);
        }

        public static readonly string[] Google = { "Google LLC", "Google Inc" };
        public static readonly string[] DockerInc = { "Docker Inc", "Docker, Inc." };

        // Windows checks many files at once (also catalog-signed Windows files): path -> (status, signer)
        public const string SigScript =
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $input | ForEach-Object { $p = $_.Trim(); if ($p) { " +
            "try { $s = Get-AuthenticodeSignature -LiteralPath $p -ErrorAction Stop; $n = if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '' }; " +
            "$p + \"`t\" + $s.Status + \"`t\" + $n } catch { $p + \"`tUnknownError`t\" } } }";

        public static Dictionary<string, string[]> CheckMany(IEnumerable<string> files)
        {
            var res = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var list = files.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!Win.IsWindows || list.Count == 0) return res;
            // the file list goes in as data, never as part of the script
            string script = "$files = @(" + string.Join(",", list.Select(f => "'" + f.Replace("'", "''").Replace("\u2018", "\u2018\u2018").Replace("\u2019", "\u2019\u2019") + "'")) + "); " + SigScript.Replace("$input |", "$files |");
            string o = StoreData.RunPs(script, 120000);
            foreach (var raw in (o ?? "").Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split('\t');
                if (p.Length >= 3) res[p[0]] = new[] { p[1], CN(p[2]) };
            }
            return res;
        }
    }

    // ============================ folders only administrators can change ============================
    public static class Locked
    {
        static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        static readonly SecurityIdentifier TrustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1031853339-3806323940-2287843442");

        static bool Trusted(IdentityReference id)
        {
            var sid = id as SecurityIdentifier;
            if (sid == null) { try { sid = (SecurityIdentifier)id.Translate(typeof(SecurityIdentifier)); } catch { return false; } }
            return sid == Admins || sid == SystemSid || sid == TrustedInstaller;
        }

        const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        static DirectorySecurity AdminOnly(bool usersRead = false)
        {
            var sec = new DirectorySecurity();
            sec.SetOwner(Admins);
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (usersRead) sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            return sec;
        }

        // creates the folder (or re-locks it) so only Administrators and SYSTEM can change or read it
        public static bool Lock(string dir, bool usersRead = false)
        {
            if (!Win.IsWindows) { Directory.CreateDirectory(dir); return !Lx.On || (Lx.Private(dir, true) && Lx.OwnedSafely(dir, true)); }   // Linux: only you can open it (0700), and not a link
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir, AdminOnly(usersRead));
                else
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return false;   // never lock through a link
                    if (!IsLocked(dir))
                    {
                        if (OnlyAdminsMadeAnything(dir)) di.SetAccessControl(AdminOnly(usersRead));
                        else
                        {
                            // someone else made it or put things in it: links or files planted inside can't be trusted,
                            // so it is set aside and a fresh folder is made
                            string aside = dir.TrimEnd('\\') + ".untrusted-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                            try { Directory.Move(dir, aside); } catch { return false; }
                            Directory.CreateDirectory(dir, AdminOnly(usersRead));
                        }
                    }
                }
                return IsLocked(dir);
            }
            catch { return false; }
        }

        // owner is Administrators/SYSTEM/TrustedInstaller and nobody else may write
        public static bool IsLocked(string path)
        {
            if (!Win.IsWindows) return Lx.OwnedSafely(path, true);
            try
            {
                FileSystemSecurity sec = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
                                                                 : new FileInfo(path).GetAccessControl();
                if (!Trusted(sec.GetOwner(typeof(SecurityIdentifier)))) return false;
                foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (r.AccessControlType != AccessControlType.Allow) continue;
                    if ((r.FileSystemRights & WriteRights) == 0) continue;
                    if (!Trusted(r.IdentityReference))
                    {
                        // CREATOR OWNER on folders only gives rights to things an admin created
                        var sid = r.IdentityReference as SecurityIdentifier;
                        if (sid != null && sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
                        return false;
                    }
                }
                if (File.Exists(path) || Directory.Exists(path))
                {
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // the folder and everything in it were made by administrators (or Windows), and nothing inside is a link
        public static bool OnlyAdminsMadeAnything(string dir)
        {
            if (!OwnedByAdmins(dir)) return false;
            try
            {
                foreach (var e in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(e) & FileAttributes.ReparsePoint) != 0 || !OwnedByAdmins(e)) return false;
                    if (Directory.Exists(e) && !OnlyAdminsMadeAnything(e)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // made by an administrator (or Windows), not by a normal program
        public static bool OwnedByAdmins(string path)
        {
            if (!Win.IsWindows) return Lx.OwnedSafely(path, false);
            try
            {
                FileSystemSecurity sec = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                                                                 : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
                return Trusted(sec.GetOwner(typeof(SecurityIdentifier)));
            }
            catch { return false; }
        }
    }

    // ============================ places Elixar never deletes ============================
    // Your own folders, checked against Windows' record of your profile.
    // Elixar runs as administrator, but %TEMP% and the "User Shell Folders" (where AppData is) can be changed by any
    // program you run, without administrator rights. Pointed at C:\Windows\Installer or Program Files, "Your temp files"
    // would then empty that folder as administrator. So a folder is used only when it is inside your real profile
    // (from HKLM\...\ProfileList, which only administrators can change); otherwise Windows' standard place is used.
    public static class UserDirs
    {
        static string profile; static bool read;
        public static string ProfileOverride;   // self-test

        public static string Profile
        {
            get
            {
                if (ProfileOverride != null) return ProfileOverride;
                if (read) return profile;
                read = true;
                if (!Win.IsWindows) return profile = null;
                try
                {
                    string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid))
                    {
                        string v = k == null ? null : k.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if (string.IsNullOrEmpty(v)) return profile = null;
                        // expanded from Windows' own folder, not from environment variables (those can be changed by you)
                        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        v = Regex.Replace(v, "%SystemDrive%", Path.GetPathRoot(win).TrimEnd('\\'), RegexOptions.IgnoreCase);
                        v = Regex.Replace(v, "%SystemRoot%|%windir%", win.TrimEnd('\\'), RegexOptions.IgnoreCase);
                        if (v.Contains("%") || !Path.IsPathRooted(v)) return profile = null;
                        return profile = Path.GetFullPath(v).TrimEnd('\\');
                    }
                }
                catch { return profile = null; }
            }
        }

        // the folder Windows reports, if it is really inside your profile; otherwise the standard place in your profile
        public static string Checked(string reported, string standard)
        {
            if (!Win.IsWindows) return reported;
            string prof = Profile;
            if (prof == null) return reported;   // no record (rare): nothing better to compare with
            try
            {
                string full = Path.GetFullPath(reported).TrimEnd('\\');
                if (full.StartsWith(prof + "\\", StringComparison.OrdinalIgnoreCase) && full.IndexOf("\\..", StringComparison.Ordinal) < 0) return full;
            }
            catch { }
            return Path.Combine(prof, standard);
        }

        public static string Local { get { return Checked(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AppData\Local"); } }
        public static string Roaming { get { return Checked(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"AppData\Roaming"); } }
        public static string Temp { get { return Checked(Path.GetTempPath(), @"AppData\Local\Temp"); } }
        public static string Home { get { string p = Profile; return p ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
    }

    public static class Protect
    {
        static string N(string p)
        {
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\', '/').ToLowerInvariant();
        }

        static List<string> exact, subtree;

        static void Init()
        {
            if (exact != null) return;
            var ex = new List<string>(); var sub = new List<string>();
            Func<Environment.SpecialFolder, string> F = f => { try { return Environment.GetFolderPath(f); } catch { return ""; } };
            string win = F(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(win)) win = @"C:\Windows";
            foreach (var d in new[] { "System32", "SysWOW64", "WinSxS", "Boot", "Fonts", "servicing", "assembly", "Microsoft.NET", "SystemApps", "SystemResources", "INF", "Sysnative" })
                sub.Add(N(Path.Combine(win, d)));
            string pf = F(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf)) sub.Add(N(Path.Combine(pf, "WindowsApps")));
            ex.Add(N(win));
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                                      Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
                                      Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic,
                                      Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonDocuments })
            {
                string p = F(f);
                if (!string.IsNullOrEmpty(p)) ex.Add(N(p));
            }
            string up = F(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(up))
            {
                ex.Add(N(Path.Combine(up, "Downloads")));
                string users = Path.GetDirectoryName(up);
                if (!string.IsNullOrEmpty(users)) ex.Add(N(users));
            }
            if (Win.IsWindows) sub.Add(N(Install.Dir));
            exact = ex.Distinct().ToList(); subtree = sub.Distinct().ToList();
        }

        // true for Windows' own folders, drive roots and the main personal folders themselves
        public static bool IsProtected(string path)
        {
            Init();
            string n = N(path);
            if (n.Length <= 3) return true;                                  // a drive root ("c:")
            if (exact.Contains(n)) return true;
            foreach (var s in subtree) if (n == s || n.StartsWith(s + "\\") || n.StartsWith(s + "/")) return true;
            return false;
        }
    }

    // ============================ deleting without following links ============================
    // Each item is opened without following links and kept open (so it can't be renamed or swapped),
    // its real location is checked, and then it is deleted through that same open handle.
    public static class SafeDelete
    {
        const uint DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint ATTR_DIRECTORY = 0x10, ATTR_REPARSE = 0x400, ATTR_NORMAL = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint size, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_BASIC_INFO { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; public uint Pad; }

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_DISPOSITION_INFO { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_BASIC_INFO info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_BASIC_INFO info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_DISPOSITION_INFO info, uint size);

        internal static string Long(string p) { return p.StartsWith(@"\\?\") ? p : p.StartsWith(@"\\") ? @"\\?\UNC\" + p.Substring(2) : @"\\?\" + p; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetLongPathNameW(string shortPath, StringBuilder buf, uint size);

        // the full name of an existing path with short (8.3) names spelled out, without following links
        public static string LongName(string path)
        {
            string full = Path.GetFullPath(path);
            var sb = new StringBuilder(1024);
            uint n = GetLongPathNameW(Long(full), sb, (uint)sb.Capacity);
            if (n > sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetLongPathNameW(Long(full), sb, (uint)sb.Capacity); }
            return n == 0 ? null : Short(sb.ToString());
        }

        // no link (junction or symbolic link) anywhere in the folder's path: it really is where its name says
        public static bool NoLinks(string dir)
        {
            if (!Win.IsWindows) return true;
            try { return Same(RealPath(dir), LongName(dir)); } catch { return false; }
        }

        static string Short(string p)
        {
            if (p.StartsWith(@"\\?\UNC\")) return @"\\" + p.Substring(8);
            if (p.StartsWith(@"\\?\")) return p.Substring(4);
            return p;
        }

        // where a path really leads (following links); null when it can't be opened
        public static string RealPath(string path)
        {
            using (var h = CreateFileW(Long(Path.GetFullPath(path)), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | 4, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
                return h.IsInvalid ? null : Final(h);
        }

        internal static string Final(SafeFileHandle h)
        {
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n > sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0); }
            return n == 0 ? null : Short(sb.ToString());
        }

        internal static bool Same(string a, string b) { return a != null && b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }

        // the file is really where its name says (no link in the way)
        public static bool RealPathMatches(string path)
        {
            if (!Win.IsWindows) return true;
            try
            {
                string full = Path.GetFullPath(path);
                if (!NoLinks(Path.GetDirectoryName(full))) return false;
                string parent = RealPath(Path.GetDirectoryName(full));
                using (var h = CreateFileW(Long(full), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | 4, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
                    return !h.IsInvalid && parent != null && Same(Final(h), parent.TrimEnd('\\') + "\\" + Path.GetFileName(full));
            }
            catch { return false; }
        }

        // Keeps every folder above path from being renamed, moved or swapped for a link until disposed:
        // each one is opened without allowing delete or rename. Returns null when that isn't possible (or a link is in the way).
        public static IDisposable HoldParents(string path)
        {
            var held = new Held();
            if (!Win.IsWindows) return held;
            try
            {
                var chain = new List<string>();
                for (string d = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) chain.Add(d);
                chain.Reverse();
                foreach (var d in chain)
                {
                    var h = CreateFileW(Long(d), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                                        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                    FILE_BASIC_INFO info;
                    if (h.IsInvalid || !GetFileInformationByHandleEx(h, 0, out info, (uint)Marshal.SizeOf(typeof(FILE_BASIC_INFO))) || (info.FileAttributes & ATTR_REPARSE) != 0)
                    { h.Dispose(); held.Dispose(); return null; }
                    held.Handles.Add(h);
                }
                return held;
            }
            catch { held.Dispose(); return null; }
        }

        sealed class Held : IDisposable
        {
            public readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
            public void Dispose() { foreach (var h in Handles) try { h.Dispose(); } catch { } Handles.Clear(); }
        }

        public static int Blocked;   // how many items were skipped because their real location changed (for the log and self-test)

        // deletes a file or folder tree; returns false when something was skipped
        public static bool Delete(string path)
        {
            string full = Path.GetFullPath(path);
            if (Protect.IsProtected(full)) return false;
            string parent = Path.GetDirectoryName(full.TrimEnd('\\'));
            if (parent == null) return false;
            if (!NoLinks(parent)) { Blocked++; return false; }   // a link somewhere above it could lead into Windows' folders
            string realParent = RealPath(parent);
            if (realParent == null) return false;
            string expected = realParent.TrimEnd('\\') + "\\" + Path.GetFileName(full.TrimEnd('\\'));
            if (Protect.IsProtected(expected)) return false;
            return Remove(full, expected, 0);
        }

        static bool Remove(string path, string expected, int depth)
        {
            if (depth > 200) return false;
            using (var h = CreateFileW(Long(path), DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                       IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;                     // in use or gone
                string real = Final(h);
                if (!Same(real, expected)) { Blocked++; return false; }   // a link was put in the way: leave it
                FILE_BASIC_INFO bi;
                if (!GetFileInformationByHandleEx(h, 0, out bi, (uint)Marshal.SizeOf(typeof(FILE_BASIC_INFO)))) return false;
                bool dir = (bi.FileAttributes & ATTR_DIRECTORY) != 0, link = (bi.FileAttributes & ATTR_REPARSE) != 0;
                bool all = true;
                if (dir && !link)
                {
                    if (Protect.IsProtected(real)) return false;
                    // the folder stays open without delete-sharing, so nothing can rename or swap it while we work inside
                    string[] kids;
                    try { kids = Directory.GetFileSystemEntries(path); } catch { return false; }
                    foreach (var k in kids)
                    {
                        string name = Path.GetFileName(k);
                        if (!Remove(path.TrimEnd('\\') + "\\" + name, real.TrimEnd('\\') + "\\" + name, depth + 1)) all = false;
                    }
                    if (!all) return false;
                }
                var clear = new FILE_BASIC_INFO { FileAttributes = dir ? ATTR_DIRECTORY | (link ? ATTR_REPARSE : 0) : ATTR_NORMAL };
                if ((bi.FileAttributes & 0x1) != 0 || (bi.FileAttributes & 0x4) != 0 || (bi.FileAttributes & 0x2) != 0)   // read-only, system, hidden
                    SetFileInformationByHandle(h, 0, ref clear, (uint)Marshal.SizeOf(typeof(FILE_BASIC_INFO)));
                var disp = new FILE_DISPOSITION_INFO { DeleteFile = true };
                return SetFileInformationByHandle(h, 4, ref disp, 1);
            }
        }
    }

    // ============================ running something without admin rights ============================
    public static class Unelevated
    {
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr sa, int level, int type, out IntPtr newToken);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string app, StringBuilder cmd, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        // starts app with the rights of the signed-in user's desktop (Explorer), not as administrator
        public static Process Start(string app, string arguments) { int code; return Start(app, arguments, 0, out code); }

        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out int code);

        // waitMs > 0: waits that long for it to finish; exitCode is -1 if it didn't finish in time
        public static Process Start(string app, string arguments, int waitMs, out int exitCode)
        {
            exitCode = -1;
            IntPtr proc = IntPtr.Zero, tok = IntPtr.Zero, dup = IntPtr.Zero;
            try
            {
                IntPtr shell = GetShellWindow();
                if (shell == IntPtr.Zero) throw new InvalidOperationException("The Windows desktop is not running.");
                uint pid; GetWindowThreadProcessId(shell, out pid);
                proc = OpenProcess(0x1000 /* query limited */, false, pid);
                if (proc == IntPtr.Zero || !OpenProcessToken(proc, 0x0002 /* duplicate */, out tok)) throw new Win32Exception();
                if (!DuplicateTokenEx(tok, 0x0001 | 0x0002 | 0x0008 | 0x0080 | 0x0100, IntPtr.Zero, 2, 1, out dup)) throw new Win32Exception();
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
                PROCESS_INFORMATION pi;
                var cmd = new StringBuilder("\"" + app + "\" " + arguments, 32768);
                if (!CreateProcessWithTokenW(dup, 0, app, cmd, 0, IntPtr.Zero, null, ref si, out pi)) throw new Win32Exception();
                CloseHandle(pi.hThread);
                Process p = null;
                try { p = Process.GetProcessById(pi.dwProcessId); } catch { }
                if (waitMs > 0 && WaitForSingleObject(pi.hProcess, (uint)waitMs) == 0) { int c; if (GetExitCodeProcess(pi.hProcess, out c)) exitCode = c; }
                CloseHandle(pi.hProcess);
                return p;
            }
            finally
            {
                if (dup != IntPtr.Zero) CloseHandle(dup);
                if (tok != IntPtr.Zero) CloseHandle(tok);
                if (proc != IntPtr.Zero) CloseHandle(proc);
            }
        }
    }

    // ============================ where Elixar keeps its own files ============================
    // C:\ProgramData\Elixar: administrators can change it, everyone can read it.
    public static class Paths
    {
        static bool ready;
        public static string Data
        {
            get
            {
                if (!Win.IsWindows)
                {
                    string ld = Lx.DataDir;
                    if (!ready) { ready = true; try { Locked.Lock(ld); } catch { } }
                    return ld;
                }
                string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrEmpty(pd)) pd = @"C:\ProgramData";
                string d = Path.Combine(pd, "Elixar");
                if (!ready && Sys.IsAdmin())
                {
                    ready = true;
                    try { Locked.Lock(d, true); } catch { }
                }
                return d;
            }
        }
        public static string FileIn(string name) { return Path.Combine(Data, name); }

        // a scratch folder only administrators can open, for files an administrator writes and then uses
        // (the user's Temp folder can be changed by any program between the write and the use)
        public static string Work
        {
            get
            {
                if (!Win.IsWindows || !Sys.IsAdmin()) return Path.GetTempPath();
                string w = Path.Combine(Data, "work");
                return Locked.Lock(w, false) ? w : null;
            }
        }
    }

    // ============================ the protected copy in Program Files ============================
    public static class Install
    {
        public static string Dir
        {
            get
            {
                return Path.Combine(Win.ProgramFiles, "Elixar");
            }
        }
        public static string Exe { get { return Win.IsWindows ? Path.Combine(Dir, "Elixar.exe") : (File.Exists(LinuxExe) ? LinuxExe : Current); } }
        public const string LinuxExe = "/usr/lib/elixar/Elixar.exe";
        public static string Current { get { return System.Windows.Forms.Application.ExecutablePath; } }

        public static bool RunningInstalled { get { return string.Equals(Path.GetFullPath(Current), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); } }

        public static bool IsInstalled { get { return File.Exists(Exe); } }

        public static bool Matches
        {
            get { try { return IsInstalled && Trust.Sha256(Exe) == SelfImage.Sha256(); } catch { return false; } }
        }

        // copies this Elixar.exe into Program Files (only administrators can change files there); returns an error or null.
        // The copy is made from the file Elixar has held open since it started, after checking that it is the running program,
        // and it carries no "downloaded from the internet" mark, so Windows doesn't show its SmartScreen box for it again.
        public static string Ensure()
        {
            if (!Win.IsWindows) return null;   // Linux: the package manager installs and updates Elixar
            try
            {
                if (RunningInstalled)
                {
                    if (Sys.IsAdmin()) { SafeFile.RemoveDownloadMark(Exe); if (Locked.IsLocked(Dir)) { string le = WriteLauncher(); if (le != null) return le; } }
                    return Locked.IsLocked(Exe) ? null : "The Program Files copy is not protected.";
                }
                if (!Sys.IsAdmin()) return "Administrator rights are needed.";
                if (!SelfImage.Verified) return "The Elixar.exe file on disk is not the program that is running (it was changed or replaced), so it was not installed. Download Elixar again.";
                Directory.CreateDirectory(Dir);
                if (!Locked.IsLocked(Dir)) Locked.Lock(Dir);
                if (!Locked.IsLocked(Dir)) return "Could not protect " + Dir + ".";
                if (!Matches)
                {
                    string tmp = Exe + ".new";
                    if (File.Exists(tmp)) File.Delete(tmp);
                    File.WriteAllBytes(tmp, SelfImage.Bytes());
                    if (Trust.Sha256(tmp) != SelfImage.Sha256()) { File.Delete(tmp); return "The copy did not match."; }
                    if (File.Exists(Exe)) { try { File.Delete(Exe); } catch { File.Move(Exe, Exe + ".old-" + DateTime.Now.Ticks); } }
                    File.Move(tmp, Exe);
                }
                // older copies that were in use during an update ("Elixar.exe.old-...") are removed once they are free
                try { foreach (var o in Directory.GetFiles(Dir, "Elixar.exe.old-*")) try { File.Delete(o); } catch { } } catch { }
                SafeFile.RemoveDownloadMark(Exe);
                string lerr = WriteLauncher();
                if (lerr != null) return lerr;
                return Matches && Locked.IsLocked(Exe) ? null : "The copy in Program Files could not be checked.";
            }
            catch (Exception e) { return e.Message; }
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        // a scheduled run that came through the launcher: hide its small console window
        public static void HideLauncherWindow()
        {
            if (!Win.IsWindows) return;
            try { if (AttachConsole(-1)) { var h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); FreeConsole(); } } catch { }
        }

        // ---- the launcher for the schedules that run with administrator rights ----
        // Task Scheduler starts a task with your account's environment variables, and any program you run can set those
        // (HKCU\Environment) without administrator rights. A few of them make every .NET program load a DLL of their
        // choosing (a "profiler") or another copy of .NET. A weekly task that runs with administrator rights would then hand
        // that DLL administrator rights silently. So the task starts cmd.exe /d (which skips cmd's own AutoRun commands),
        // runs this fixed file from the protected folder, which clears those variables, and only then starts Elixar.
        public static string Launcher { get { return Path.Combine(Dir, "elixar-task.cmd"); } }
        public static readonly string[] Cleared = { "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "COR_PROFILER_PATH_32", "COR_PROFILER_PATH_64",
            "COMPLUS_ENABLEPROFILING", "COMPLUS_PROFAPI_PROFILERCOMPATIBILITYSETTING", "COMPLUS_INSTALLROOT", "COMPLUS_VERSION", "COMPLUS_DEFAULTVERSION",
            "COMPLUS_ONLYUSELATESTCLR", "COMPLUS_INSTALLROOT32", "COMPLUS_INSTALLROOT64", "COMPLUS_ZAPDISABLE", "COMPLUS_NGENPATH", "COMPLUS_PRIVATEINSTALLROOT",
            "COMPLUS_TRUSTEDPLATFORMASSEMBLIES", "COMPLUS_APPDOMAINMANAGERASM", "COMPLUS_APPDOMAINMANAGERTYPE", "APPDOMAIN_MANAGER_ASM", "APPDOMAIN_MANAGER_TYPE",
            "PSMODULEPATH" };

        public static string LauncherText
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("@echo off\r\nrem Elixar's schedules that run with administrator rights start here.\r\n");
                sb.Append("rem It clears environment variables that could make .NET load someone else's code, then starts Elixar.\r\n");
                foreach (var v in Cleared) sb.Append("set \"" + v + "=\"\r\n");
                // Elixar is started directly by its full path (never through "start" or a file-type handler); it hides this window
                sb.Append("\"%~dp0Elixar.exe\" %*\r\nexit /b %errorlevel%\r\n");
                return sb.ToString();
            }
        }

        // what Task Scheduler is told to run, e.g. cmd.exe /d /c ""C:\Program Files\Elixar\elixar-task.cmd" --auto-clean"
        public static string CmdExe { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"); } }
        public static string LauncherArgs(string arg) { return "/d /c \"\"" + Launcher + "\" " + arg + "\""; }

        // a task with administrator rights is safe when it runs the launcher exactly like this (or, on Linux, as before)
        public static bool IsSafeElevatedTask(string command, string arguments, string arg)
        {
            if (!Win.IsWindows) return IsProtectedExe(command);
            try
            {
                return string.Equals(Path.GetFullPath((command ?? "").Trim('"')), CmdExe, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals((arguments ?? "").Trim(), LauncherArgs(arg), StringComparison.OrdinalIgnoreCase) && LauncherOk;
            }
            catch { return false; }
        }

        public static bool LauncherOk
        {
            get { try { return File.Exists(Launcher) && File.ReadAllText(Launcher) == LauncherText && Locked.IsLocked(Launcher); } catch { return false; } }
        }

        static string WriteLauncher()
        {
            if (LauncherOk) return null;
            string tmp = Launcher + ".new";
            if (File.Exists(tmp)) File.Delete(tmp);
            File.WriteAllText(tmp, LauncherText, new UTF8Encoding(false));
            if (File.Exists(Launcher)) File.Delete(Launcher);
            File.Move(tmp, Launcher);
            return LauncherOk ? null : "The schedule launcher could not be checked.";
        }

        // starts Elixar again: the protected copy when there is one (never a file in a folder other programs can change)
        public static void Relaunch(string args)
        {
            string exe = Current;
            if (Win.IsWindows && Sys.IsAdmin() && Ensure() == null && IsInstalled) exe = Exe;
            if (!Win.IsWindows) { Process.Start(new ProcessStartInfo(Lx.Tool("mono") ?? "/usr/bin/mono", Lx.Arg(Exe) + " " + (args ?? "")) { UseShellExecute = false }); return; }
            Process.Start(new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false });
        }

        // a schedule is safe only when it starts the protected copy
        public static bool IsProtectedExe(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!Win.IsWindows) return true;   // Linux timers run as you, never as root: nothing to take over
            try { return string.Equals(Path.GetFullPath(path.Trim('"')), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetSearchPathMode(uint flags);
        const uint LoadFromSystem32 = 0x800, SafeSearchPermanent = 0x8001;
        public static bool DllSearchHardened;   // for the self-test

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
        // the running Elixar.exe has its Windows manifest (resource type 24, id 1)
        public static bool HasManifest() { try { return FindResourceW(GetModuleHandleW(null), new IntPtr(1), new IntPtr(24)) != IntPtr.Zero; } catch { return false; } }

        // Windows looks for a DLL in the program's own folder first. For a copy in Downloads, any program could put a fake
        // DLL there, and Elixar runs as administrator. From here on, DLLs come only from System32; the current folder is
        // never searched, and SearchPath uses its safe order.
        public static void HardenDllSearch()
        {
            if (!Win.IsWindows) return;
            try { SetDllDirectoryW(""); } catch { }
            try { DllSearchHardened = SetDefaultDllDirectories(LoadFromSystem32); } catch { }   // Windows 8+, or 7 with KB2533623
            try { SetSearchPathMode(SafeSearchPermanent); } catch { }
        }
    }
}
