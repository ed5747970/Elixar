// "Disk 0 of ELISAR-PC": which physical disk a drive is on (the number Windows' Disk Management shows), and this PC's name.
// Read-only: one quick question to Windows about the volume, no administrator rights needed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CDriveCleaner
{
    public static class DiskNumber
    {
        [StructLayout(LayoutKind.Sequential)] struct STORAGE_DEVICE_NUMBER { public int DeviceType, DeviceNumber, PartitionNumber; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out STORAGE_DEVICE_NUMBER outBuf, int outSize, out int returned, IntPtr overlapped);

        const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        public static Func<string, string> Override;   // previews and the self-test

        // this PC's name, as Windows shows it in Settings > System > About (case kept)
        public static string HostOverride;   // previews
        public static string Host
        {
            get
            {
                if (HostOverride != null) return HostOverride;
                string h = "";
                try { h = System.Net.Dns.GetHostName(); } catch { }
                if (string.IsNullOrEmpty(h)) try { h = Environment.MachineName; } catch { }
                h = Regex.Replace(h ?? "", @"[^\w.\-]", "");
                return h.Length == 0 ? "this PC" : h.Length > 40 ? h.Substring(0, 40) : h;
            }
        }

        // "0", "1"... on Windows; "sda", "nvme0n1" on Linux; "" when Windows can't tell (a drive spread over several disks)
        public static string Of(string driveOrMount)
        {
            if (Override != null) return Override(driveOrMount);
            string key = driveOrMount ?? "";
            lock (cache) { string c; if (cache.TryGetValue(key, out c)) return c; }
            string n = Win.IsWindows ? Windows(key) : Linux(key);
            lock (cache) cache[key] = n;
            return n;
        }

        static string Windows(string letter)
        {
            if (!Regex.IsMatch(letter, "^[A-Za-z]$")) return "";
            try
            {
                using (var h = CreateFileW(@"\\.\" + letter.ToUpperInvariant() + ":", 0, 3 /* share read, write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return "";
                    STORAGE_DEVICE_NUMBER sdn; int got;
                    if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, out sdn, Marshal.SizeOf(typeof(STORAGE_DEVICE_NUMBER)), out got, IntPtr.Zero)) return "";
                    return sdn.DeviceNumber >= 0 && sdn.DeviceNumber < 1000 ? sdn.DeviceNumber.ToString() : "";
                }
            }
            catch { return ""; }
        }

        // /dev/nvme0n1p2 -> nvme0n1, /dev/sda1 -> sda, /dev/mmcblk0p1 -> mmcblk0
        public static string DiskOfDevice(string dev)
        {
            string n = Path.GetFileName(dev ?? "");
            var m = Regex.Match(n, @"^((?:nvme\d+n\d+)|(?:mmcblk\d+))(?:p\d+)?$");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(n, @"^((?:[shv]|xv)d[a-z]+)\d*$");
            return m.Success ? m.Groups[1].Value : "";
        }

        static string Linux(string point)
        {
            string p = point == "C" ? "/" : point;
            var mt = Lx.Mounts().FirstOrDefault(x => x.Point == p);
            return mt == null ? "" : DiskOfDevice(mt.Device);
        }

        // what the Dashboard shows: "Disk 0 of ELISAR-PC" (or just the PC's name when the disk isn't known)
        public static string Tag(string driveOrMount)
        {
            string n = Of(driveOrMount);
            return n.Length > 0 ? "Disk " + n + " of " + Host : Host;
        }
    }
}
