// Similar photos: pictures that look the same but aren't byte-for-byte copies (burst shots, resized or re-saved copies).
// Each photo is shrunk to 9x8 grey pixels and turned into a 64-bit fingerprint (a "difference hash"); photos whose
// fingerprints differ in only a few bits look alike. Read only.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;

namespace CDriveCleaner
{
    public static class SimilarPhotos
    {
        public const int MaxDistance = 5;   // of 64 bits
        public const long MaxBytes = 80L << 20, MaxPixels = 100L * 1000 * 1000;

        public static ulong? Fingerprint(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                if (fs.Length > MaxBytes) return null;
                using (var img = Image.FromStream(fs, false, false))
                {
                    // only the size is read so far; a picture of more than 100 million pixels is skipped (it could need gigabytes)
                    if ((long)img.Width * img.Height > MaxPixels) return null;
                    Image src = img, thumb = null;
                    try
                    {
                        // the small preview a camera stores inside the photo is much faster to read
                        if (img.PropertyIdList.Contains(0x501B))
                        {
                            var pi = img.GetPropertyItem(0x501B);
                            if (pi.Value != null && pi.Value.Length < (1 << 20)) { thumb = Image.FromStream(new MemoryStream(pi.Value), false, false); if ((long)thumb.Width * thumb.Height > MaxPixels) { thumb.Dispose(); thumb = null; } else src = thumb; }
                        }
                    }
                    catch { thumb = null; src = img; }
                    try
                    {
                        using (var bmp = new Bitmap(9, 8))
                        {
                            using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(src, 0, 0, 9, 8); }
                            ulong h = 0; int bit = 0;
                            for (int y = 0; y < 8; y++)
                                for (int x = 0; x < 8; x++)
                                {
                                    Color a = bmp.GetPixel(x, y), b = bmp.GetPixel(x + 1, y);
                                    int la = a.R * 299 + a.G * 587 + a.B * 114, lb = b.R * 299 + b.G * 587 + b.B * 114;
                                    if (la > lb) h |= 1UL << bit;
                                    bit++;
                                }
                            return h;
                        }
                    }
                    finally { if (thumb != null) thumb.Dispose(); }
                }
                }
            }
            catch { return null; }
        }

        public static int Distance(ulong a, ulong b)
        {
            ulong x = a ^ b; int n = 0;
            while (x != 0) { x &= x - 1; n++; }
            return n;
        }

        public static List<List<FileInfo>> Find(List<FileInfo> photos, Action<string> status, CancellationToken ct)
        {
            var hashes = new List<KeyValuePair<FileInfo, ulong>>();
            for (int i = 0; i < photos.Count; i++)
            {
                if (ct.IsCancellationRequested) return new List<List<FileInfo>>();
                if (status != null && i % 25 == 0) status("Comparing photos (" + i + " of " + photos.Count + ")...");
                var h = Fingerprint(photos[i].FullName);
                if (h.HasValue) hashes.Add(new KeyValuePair<FileInfo, ulong>(photos[i], h.Value));
            }
            // groups of photos that are close to each other (union-find)
            var parent = Enumerable.Range(0, hashes.Count).ToArray();
            Func<int, int> root = null;
            root = i => parent[i] == i ? i : (parent[i] = root(parent[i]));
            for (int i = 0; i < hashes.Count; i++)
            {
                if (ct.IsCancellationRequested) return new List<List<FileInfo>>();
                for (int j = i + 1; j < hashes.Count; j++)
                    if (Distance(hashes[i].Value, hashes[j].Value) <= MaxDistance) { int a = root(i), b = root(j); if (a != b) parent[b] = a; }
            }
            return Enumerable.Range(0, hashes.Count).GroupBy(root).Where(g => g.Count() > 1)
                .Select(g => g.Select(i => hashes[i].Key).OrderByDescending(f => f.Length).ToList())   // the biggest (best quality) first
                .OrderByDescending(g => g.Skip(1).Sum(f => f.Length)).ToList();
        }
    }
}
