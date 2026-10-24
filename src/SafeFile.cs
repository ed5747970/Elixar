// Link-proof file work for an app that runs as administrator, and a check that the Elixar.exe on disk is the program
// that is actually running.
//
// A normal program can't change Windows' folders, but it can put a link (junction) in a folder it owns, so that a path
// like "D:\Phone backup\DCIM" quietly leads into C:\Windows\System32. Elixar runs as administrator, so every copy, move or
// write into a folder a normal program can change goes through a handle that is checked for where it really is.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CDriveCleaner
{
    public static class SafeFile
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80, SYNCHRONIZE = 0x00100000;
        const uint FILE_LIST_DIRECTORY = 0x1, FILE_ADD_FILE = 0x2, FILE_TRAVERSE = 0x20;
        const uint SHARE_READ = 1, SHARE_WRITE = 2, CREATE_NEW = 1, OPEN_EXISTING = 3;
        const uint FLAG_BACKUP_SEMANTICS = 0x02000000, FLAG_OPEN_REPARSE_POINT = 0x00200000, ATTR_NORMAL = 0x80;
        const uint ATTR_DIRECTORY = 0x10, ATTR_REPARSE = 0x400;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_BASIC_INFO { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; public uint Pad; }
        [StructLayout(LayoutKind.Sequential)]
        struct FILE_DISPOSITION_INFO { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_STATUS_BLOCK { public IntPtr Status, Information; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_BASIC_INFO info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_DISPOSITION_INFO info, uint size);
        [DllImport("ntdll.dll")]
        static extern int NtSetInformationFile(SafeFileHandle h, out IO_STATUS_BLOCK iosb, IntPtr info, int length, int cls);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFileW(string name);

        public static int Blocked;   // how many times a link was found in the way (for the log and self-test)

        static uint Attributes(SafeFileHandle h)
        {
            FILE_BASIC_INFO bi;
            return GetFileInformationByHandleEx(h, 0, out bi, (uint)Marshal.SizeOf(typeof(FILE_BASIC_INFO))) ? bi.FileAttributes : uint.MaxValue;
        }

        static void DropThroughHandle(SafeFileHandle h)
        {
            var d = new FILE_DISPOSITION_INFO { DeleteFile = true };
            SetFileInformationByHandle(h, 4, ref d, 1);
        }

        static IOException InTheWay(string path) { Blocked++; return new IOException("a link was in the way, so Elixar left " + path + " alone"); }

        // a file opened for reading, only if it is a plain file that really is where its name says
        public static FileStream OpenRead(string path)
        {
            string full = Path.GetFullPath(path);
            if (!Win.IsWindows) return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!SafeDelete.NoLinks(Path.GetDirectoryName(full))) throw InTheWay(full);
            var h = CreateFileW(SafeDelete.Long(full), GENERIC_READ, SHARE_READ, IntPtr.Zero, OPEN_EXISTING, FLAG_BACKUP_SEMANTICS | FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) throw new IOException("could not open " + full + " (error " + Marshal.GetLastWin32Error() + ")");
            uint a = Attributes(h);
            if (a == uint.MaxValue || (a & (ATTR_DIRECTORY | ATTR_REPARSE)) != 0 || !SafeDelete.Same(SafeDelete.Final(h), SafeDelete.LongName(full))) { h.Dispose(); throw InTheWay(full); }
            return new FileStream(h, FileAccess.Read);
        }

        // a new file, only if it was really made where its name says (otherwise it is removed again before anything is written)
        public static FileStream CreateNew(string path)
        {
            string full = Path.GetFullPath(path);
            if (!Win.IsWindows) return new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            string dir = Path.GetDirectoryName(full);
            if (!SafeDelete.NoLinks(dir)) throw InTheWay(full);
            var h = CreateFileW(SafeDelete.Long(full), GENERIC_WRITE | DELETE | FILE_READ_ATTRIBUTES, 0, IntPtr.Zero, CREATE_NEW, ATTR_NORMAL | FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) throw new IOException("could not create " + full + " (error " + Marshal.GetLastWin32Error() + ")");
            string real = SafeDelete.Final(h), want = SafeDelete.LongName(dir);
            if (want == null || !SafeDelete.Same(real, want.TrimEnd('\\') + "\\" + Path.GetFileName(full))) { DropThroughHandle(h); h.Dispose(); throw InTheWay(full); }
            return new FileStream(h, FileAccess.Write);
        }

        // copies a file; the copy is checked to be complete
        public static long Copy(string src, string dst)
        {
            using (var i = OpenRead(src))
            {
                long n = i.Length;
                try
                {
                    using (var o = CreateNew(dst)) { i.CopyTo(o, 1 << 20); o.Flush(true); if (o.Length != n) throw new IOException("the copy is a different size"); }
                }
                catch { try { SafeDelete.Delete(dst); } catch { } throw; }
                return n;
            }
        }

        // writes text to a new file (replacing an old one), through a checked handle
        public static void WriteText(string path, string text) { WriteBytes(path, Encoding.UTF8.GetBytes(text)); }

        public static void WriteBytes(string path, byte[] b)
        {
            if (File.Exists(path)) { if (Win.IsWindows) SafeDelete.Delete(path); else File.Delete(path); }
            using (var o = CreateNew(path)) o.Write(b, 0, b.Length);
        }

        // a file you chose in a Save box. Elixar runs as administrator, so it never writes through a link another program put
        // in that folder: when the checked write isn't possible there, it says so instead of writing the ordinary way
        public static void Save(string path, byte[] data)
        {
            try { WriteBytes(path, data); }
            catch (Exception e)
            {
                throw new IOException("Elixar couldn't save there safely (" + e.Message + "). Choose another folder, for example Documents or Desktop.", e);
            }
        }
        public static void Save(string path, string text) { Save(path, Encoding.UTF8.GetBytes(text)); }

        // moves a file into a folder on the same drive, by handle: the file and the folder are both checked for where they
        // really are, and the move goes into exactly that folder even if a link is put in place of its name afterwards
        public static bool MoveInto(string src, string dstDir, string name)
        {
            string full = Path.GetFullPath(src), dir = Path.GetFullPath(dstDir);
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name == "." || name == "..") throw new ArgumentException("bad file name: " + name);
            if (!Win.IsWindows) { File.Move(full, Path.Combine(dir, name)); return true; }
            if (!SafeDelete.NoLinks(Path.GetDirectoryName(full)) || !SafeDelete.NoLinks(dir)) throw InTheWay(full);
            // backup semantics: only matters while Windows' backup rights are switched on (BackupRights), otherwise the normal checks apply
            using (var h = CreateFileW(SafeDelete.Long(full), DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_READ | SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FLAG_BACKUP_SEMANTICS | FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
            using (var dh = CreateFileW(SafeDelete.Long(dir), FILE_LIST_DIRECTORY | FILE_ADD_FILE | FILE_TRAVERSE | SYNCHRONIZE, SHARE_READ | SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                                        FLAG_BACKUP_SEMANTICS | FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
            {
                if (h.IsInvalid || dh.IsInvalid) throw new IOException("could not open " + (h.IsInvalid ? full : dir));
                uint a = Attributes(h), da = Attributes(dh);
                if (a == uint.MaxValue || (a & (ATTR_DIRECTORY | ATTR_REPARSE)) != 0 || !SafeDelete.Same(SafeDelete.Final(h), SafeDelete.LongName(full))) throw InTheWay(full);
                if (da == uint.MaxValue || (da & ATTR_DIRECTORY) == 0 || (da & ATTR_REPARSE) != 0 || !SafeDelete.Same(SafeDelete.Final(dh), SafeDelete.LongName(dir))) throw InTheWay(dir);
                // FILE_RENAME_INFORMATION: ReplaceIfExists, RootDirectory (the checked folder), FileNameLength, FileName (just the name)
                int ptr = IntPtr.Size, nameOff = ptr * 2 + 4;
                byte[] nb = Encoding.Unicode.GetBytes(name);
                int size = nameOff + nb.Length + 2;
                IntPtr buf = Marshal.AllocHGlobal(size);
                bool added = false;
                try
                {
                    for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                    dh.DangerousAddRef(ref added);
                    if (ptr == 8) Marshal.WriteInt64(buf, 8, dh.DangerousGetHandle().ToInt64()); else Marshal.WriteInt32(buf, 4, dh.DangerousGetHandle().ToInt32());
                    Marshal.WriteInt32(buf, ptr * 2, nb.Length);
                    Marshal.Copy(nb, 0, buf + nameOff, nb.Length);
                    IO_STATUS_BLOCK io;
                    int st = NtSetInformationFile(h, out io, buf, size, 10 /* FileRenameInformation */);
                    if (st != 0) throw new IOException("Windows did not move " + full + " (status 0x" + st.ToString("X8") + ")");
                    return true;
                }
                finally { if (added) dh.DangerousRelease(); Marshal.FreeHGlobal(buf); }
            }
        }

        // a free name in the folder: "photo.jpg", "photo (2).jpg", ...
        public static string FreeName(string dir, string name)
        {
            string n = name, b = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; File.Exists(Path.Combine(dir, n)) || Directory.Exists(Path.Combine(dir, n)); i++) n = b + " (" + i + ")" + ext;
            return n;
        }

        // makes the folder (and the ones above it) and checks that no link was in the way
        public static void MakeDir(string dir)
        {
            Directory.CreateDirectory(dir);
            if (!SafeDelete.NoLinks(dir)) throw InTheWay(dir);
        }

        // removes Windows' "downloaded from the internet" mark from a file
        public static void RemoveDownloadMark(string file)
        {
            if (Win.IsWindows) try { DeleteFileW(SafeDelete.Long(Path.GetFullPath(file)) + ":Zone.Identifier"); } catch { }
        }

        public static bool HasDownloadMark(string file)
        {
            if (!Win.IsWindows) return false;
            using (var h = CreateFileW(SafeDelete.Long(Path.GetFullPath(file)) + ":Zone.Identifier", FILE_READ_ATTRIBUTES, SHARE_READ | SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                return !h.IsInvalid;
        }
    }

    // ============================ is the Elixar.exe on disk the program that is running? ============================
    // Elixar is often started from Downloads, a folder any program can change. A running .exe can't be overwritten but it can
    // be renamed, and a different file put in its place; copying "Elixar.exe" into Program Files would then install that
    // file. So Elixar holds its own file open from the first moment (nobody can rename or replace it after that) and checks
    // that the file's code is the code Windows loaded.
    public static class SelfImage
    {
        static FileStream held;
        static bool? same;
        static byte[] bytes;

        public static void Hold()
        {
            if (!Win.IsWindows || held != null) return;
            try { held = new FileStream(Install.Current, FileMode.Open, FileAccess.Read, FileShare.Read); } catch { }
        }

        public static bool Verified
        {
            get
            {
                if (!Win.IsWindows) return true;
                if (same == null) { try { same = held != null && Compare(Bytes()); } catch { same = false; } }
                return same.Value;
            }
        }

        // the file's bytes, read from the handle held since start-up
        public static byte[] Bytes()
        {
            if (bytes != null) return bytes;
            if (held == null) return bytes = File.ReadAllBytes(Install.Current);   // not on Windows
            lock (held)
            {
                held.Position = 0;
                var b = new byte[held.Length];
                int got = 0;
                while (got < b.Length) { int n = held.Read(b, got, b.Length - got); if (n <= 0) throw new IOException("short read"); got += n; }
                return bytes = b;
            }
        }

        public static string Sha256()
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Bytes())).Replace("-", "");
        }

        static int I32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
        static int U16(byte[] b, int o) { return BitConverter.ToUInt16(b, o); }

        // compares every section of the file with the same section in memory, skipping only the few bytes Windows itself
        // fills in when it loads a program (relocated addresses and the import address table)
        public static bool Compare(byte[] f)
        {
            IntPtr mem = Marshal.GetHINSTANCE(typeof(SelfImage).Module);
            if (mem == IntPtr.Zero || mem == new IntPtr(-1)) return false;
            return Compare(f, (at, len) => { var b = new byte[len]; Marshal.Copy(mem + at, b, 0, len); return b; });
        }

        // the same check against any loaded image (the self-test uses a simulated one)
        public static bool Compare(byte[] f, Func<int, int, byte[]> image)
        {
            int pe = I32(f, 0x3C);
            if (pe <= 0 || pe + 24 > f.Length || I32(f, pe) != 0x4550) return false;
            int nsec = U16(f, pe + 6), optSize = U16(f, pe + 20), opt = pe + 24;
            int dd = opt + (U16(f, opt) == 0x20B ? 112 : 96);
            int relocRva = I32(f, dd + 5 * 8), relocSize = I32(f, dd + 5 * 8 + 4), iatRva = I32(f, dd + 12 * 8), iatSize = I32(f, dd + 12 * 8 + 4);
            int tab = opt + optSize;

            // the section table in memory must match the file's (no section added, moved or resized)
            int mpe = I32(image(0x3C, 4), 0);
            var mfh = image(mpe, 24);
            if (I32(mfh, 0) != 0x4550 || U16(mfh, 6) != nsec) return false;
            int mtab = mpe + 24 + U16(mfh, 20);
            var mh = image(mtab, 40 * nsec);
            for (int i = 0; i < mh.Length; i++) if (mh[i] != f[tab + i]) return false;

            Func<int, int> off = rva =>
            {
                for (int i = 0; i < nsec; i++)
                {
                    int s = tab + 40 * i, va = I32(f, s + 12), raw = I32(f, s + 16);
                    if (rva >= va && rva < va + raw) return I32(f, s + 20) + (rva - va);
                }
                return -1;
            };
            var skip = new HashSet<int>();
            for (int k = 0; k < iatSize; k++) skip.Add(iatRva + k);
            int p = relocSize > 0 ? off(relocRva) : -1, end = p + relocSize;
            while (p >= 0 && p + 8 <= end && p + 8 <= f.Length)
            {
                int page = I32(f, p), block = I32(f, p + 4);
                if (block < 8) break;
                for (int e = p + 8; e + 2 <= p + block; e += 2)
                {
                    int v = U16(f, e), type = v >> 12, at = page + (v & 0xFFF);
                    int n = type == 3 ? 4 : type == 10 ? 8 : 0;
                    for (int k = 0; k < n; k++) skip.Add(at + k);
                }
                p += block;
            }

            long compared = 0;
            for (int i = 0; i < nsec; i++)
            {
                int s = tab + 40 * i, vsize = I32(f, s + 8), va = I32(f, s + 12), raw = I32(f, s + 16), ptr = I32(f, s + 20);
                int len = Math.Min(raw, vsize);
                if (len <= 0) continue;
                if (ptr + len > f.Length) return false;
                var m = image(va, len);
                for (int k = 0; k < len; k++)
                    if (m[k] != f[ptr + k] && !skip.Contains(va + k)) return false;
                compared += len;
            }
            return compared > 0;
        }
    }
}
