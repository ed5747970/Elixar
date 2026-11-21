// Self-test for 1.2.72: a faster scan. Folders are counted the fast Windows way (with the same answer as before), the
// cleaning list is measured several items at once, and the shared lists used while measuring are made safely from many threads.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CDriveCleaner
{
    public static class SelfTest1272
    {
        static void Run(Action<bool, string> check, string name, Func<string> test)
        {
            string why;
            try { why = test(); }
            catch (Exception ex)
            {
                var at = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Contains("CDriveCleaner.")).Take(4);
                why = "ERROR " + ex.GetType().Name + ": " + ex.Message + " @ " + string.Join(" < ", at);
            }
            check(why == null, name + (why == null ? "" : "  (" + why + ")"));
        }

        public static void All(string root, Action<bool, string> check)
        {
            string R = Path.Combine(root, "t1272"); Directory.CreateDirectory(R);

            Run(check, "Fast count: a folder's size comes out the same as the plain way (deep folders, long paths, links not followed)", () =>
            {
                string top = Path.Combine(R, "tree");
                var rnd = new Random(7); long expect = 0;
                for (int i = 0; i < 40; i++)
                {
                    string d = Path.Combine(top, "d" + (i % 7), "sub" + (i % 3), "x" + i);
                    Directory.CreateDirectory(d);
                    int n = rnd.Next(0, 5000); File.WriteAllBytes(Path.Combine(d, "f" + i + ".bin"), new byte[n]); expect += n;
                }
                // a long path (over 260 characters)
                if (Win.IsWindows)
                {
                    string deep = top; while (deep.Length < 300) deep = Path.Combine(deep, "a-rather-long-folder-name");
                    try { Directory.CreateDirectory(@"\\?\" + deep); File.WriteAllBytes(@"\\?\" + Path.Combine(deep, "deep.bin"), new byte[1234]); expect += 1234; } catch { }
                    // a junction to a big folder: not counted
                    string big = Path.Combine(R, "big"); Directory.CreateDirectory(big); File.WriteAllBytes(Path.Combine(big, "big.bin"), new byte[100000]);
                    try { Sys.Run("cmd.exe", "/c mklink /J \"" + Path.Combine(top, "link") + "\" \"" + big + "\"", 15000); } catch { }
                }
                Fs.FastSizeOff = true; long plain = Fs.SizeOf(top); Fs.FastSizeOff = false;
                long fast = Fs.SizeOf(top);
                // the plain way can't read past 260 characters on every Windows: the fast way counts those too
                if (!Win.IsWindows && fast != plain) return "fast " + fast + " vs plain " + plain;
                if (Win.IsWindows && fast < plain) return "fast " + fast + " is less than plain " + plain;
                if (Win.IsWindows && (fast != expect || Fs.FastSize(top) != expect)) return "expected " + expect + ", got " + fast;
                if (Fs.SizeOf(Path.Combine(R, "missing")) != 0) return "a missing folder isn't 0";
                return null;
            });

            Run(check, "Measuring several at once: Windows' folder lists and your profile are made once, safely, from many threads", () =>
            {
                var errors = new List<string>();
                Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    try
                    {
                        if (Win.IsWindows && !Protect.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.Windows))) lock (errors) errors.Add("Windows not protected");
                        if (string.IsNullOrEmpty(UserDirs.Home)) lock (errors) errors.Add("no home");
                        if (string.IsNullOrEmpty(Paths.Data)) lock (errors) errors.Add("no data folder");
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex.GetType().Name); }
                });
                if (errors.Count > 0) return string.Join(", ", errors.Distinct());
                if (MainForm.MeasureThreads < 2) return "measured one by one";
                return null;
            });
        }
    }
}
