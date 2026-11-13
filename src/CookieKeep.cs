// Cookie keep-list: clearing cookies normally signs you out of every website. With a keep-list, Elixar removes the
// cookies of every other site (trackers, sites you visited once) and keeps the ones you chose, so you stay signed in there.
// How, safely: each browser's cookie file (closed browser) is copied into Elixar's protected folder through checked handles
// and edited there with Windows' own SQLite (built into Windows 10 and 11); the original goes to Undo for 7 days and the
// edited copy takes its place. Nothing is read or written through a link.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    // ---------------- a very small SQLite (Windows' winsqlite3.dll; libsqlite3 on Linux for the self-test) ----------------
    public sealed class SqliteDb : IDisposable
    {
        static class W
        {
            const string L = "winsqlite3.dll"; const CallingConvention C = CallingConvention.StdCall;
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_open_v2(byte[] f, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_close_v2(IntPtr db);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr st, IntPtr tail);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_step(IntPtr st);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_finalize(IntPtr st);
            [DllImport(L, CallingConvention = C)] public static extern IntPtr sqlite3_column_text(IntPtr st, int i);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_column_bytes(IntPtr st, int i);
            [DllImport(L, CallingConvention = C)] public static extern long sqlite3_column_int64(IntPtr st, int i);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_bind_int64(IntPtr st, int i, long v);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_reset(IntPtr st);
            [DllImport(L, CallingConvention = C)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
        }
        static class X
        {
            const string L = "libsqlite3.so.0"; const CallingConvention C = CallingConvention.Cdecl;
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_open_v2(byte[] f, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_close_v2(IntPtr db);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr st, IntPtr tail);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_step(IntPtr st);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_finalize(IntPtr st);
            [DllImport(L, CallingConvention = C)] public static extern IntPtr sqlite3_column_text(IntPtr st, int i);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_column_bytes(IntPtr st, int i);
            [DllImport(L, CallingConvention = C)] public static extern long sqlite3_column_int64(IntPtr st, int i);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_bind_int64(IntPtr st, int i, long v);
            [DllImport(L, CallingConvention = C)] public static extern int sqlite3_reset(IntPtr st);
            [DllImport(L, CallingConvention = C)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
        }
        static readonly bool Win = CDriveCleaner.Win.IsWindows;
        IntPtr db;
        const int ROW = 100, DONE = 101, OPEN_READWRITE = 2, OPEN_CREATE = 4, OPEN_NOMUTEX = 0x8000;

        static byte[] Z(string s) { var b = Encoding.UTF8.GetBytes(s ?? ""); var z = new byte[b.Length + 1]; Array.Copy(b, z, b.Length); return z; }

        public SqliteDb(string file, bool create)
        {
            int flags = OPEN_READWRITE | OPEN_NOMUTEX | (create ? OPEN_CREATE : 0);
            int rc = Win ? W.sqlite3_open_v2(Z(file), out db, flags, IntPtr.Zero) : X.sqlite3_open_v2(Z(file), out db, flags, IntPtr.Zero);
            if (rc != 0) { string e = Err(); Dispose(); throw new IOException("SQLite couldn't open the file: " + e); }
        }
        string Err() { if (db == IntPtr.Zero) return "?"; return Utf8(Win ? W.sqlite3_errmsg(db) : X.sqlite3_errmsg(db), -1); }

        static string Utf8(IntPtr p, int len)
        {
            if (p == IntPtr.Zero) return "";
            if (len < 0) { len = 0; while (Marshal.ReadByte(p, len) != 0) len++; }
            var b = new byte[len]; Marshal.Copy(p, b, 0, len); return Encoding.UTF8.GetString(b);
        }

        IntPtr Prepare(string sql)
        {
            IntPtr st; byte[] s = Z(sql);
            int rc = Win ? W.sqlite3_prepare_v2(db, s, s.Length, out st, IntPtr.Zero) : X.sqlite3_prepare_v2(db, s, s.Length, out st, IntPtr.Zero);
            if (rc != 0) throw new IOException("SQLite: " + Err());
            return st;
        }
        int Step(IntPtr st) { return Win ? W.sqlite3_step(st) : X.sqlite3_step(st); }
        void Finalize(IntPtr st) { if (Win) W.sqlite3_finalize(st); else X.sqlite3_finalize(st); }

        public void Exec(string sql)
        {
            var st = Prepare(sql);
            try { int rc; while ((rc = Step(st)) == ROW) { } if (rc != DONE) throw new IOException("SQLite: " + Err()); }
            finally { Finalize(st); }
        }

        // rows of (rowid, text) from a query that returns those two columns
        public List<KeyValuePair<long, string>> Rows(string sql, int textColumns = 1)
        {
            var list = new List<KeyValuePair<long, string>>();
            var st = Prepare(sql);
            try
            {
                int rc;
                while ((rc = Step(st)) == ROW)
                {
                    long id = Win ? W.sqlite3_column_int64(st, 0) : X.sqlite3_column_int64(st, 0);
                    var parts = new List<string>();
                    for (int c = 1; c <= textColumns; c++)
                    {
                        IntPtr p = Win ? W.sqlite3_column_text(st, c) : X.sqlite3_column_text(st, c);
                        int n = Win ? W.sqlite3_column_bytes(st, c) : X.sqlite3_column_bytes(st, c);
                        parts.Add(Utf8(p, n));
                    }
                    list.Add(new KeyValuePair<long, string>(id, string.Join("\t", parts)));
                    if (list.Count > 500000) break;
                }
                if (rc != DONE && rc != ROW) throw new IOException("SQLite: " + Err());
            }
            finally { Finalize(st); }
            return list;
        }

        // DELETE ... WHERE rowid = ? for each id, in one transaction
        public int DeleteIds(string table, IEnumerable<long> ids)
        {
            if (!Regex.IsMatch(table, "^[a-z_]+$")) throw new ArgumentException("table");
            int n = 0;
            Exec("BEGIN");
            var st = Prepare("DELETE FROM " + table + " WHERE rowid = ?1");
            try
            {
                foreach (var id in ids)
                {
                    if (Win) { W.sqlite3_bind_int64(st, 1, id); } else { X.sqlite3_bind_int64(st, 1, id); }
                    if (Step(st) != DONE) throw new IOException("SQLite: " + Err());
                    if (Win) W.sqlite3_reset(st); else X.sqlite3_reset(st);
                    n++;
                }
            }
            catch { Finalize(st); st = IntPtr.Zero; try { Exec("ROLLBACK"); } catch { } throw; }
            finally { if (st != IntPtr.Zero) Finalize(st); }
            Exec("COMMIT");
            return n;
        }

        public void Dispose() { if (db != IntPtr.Zero) { if (Win) W.sqlite3_close_v2(db); else X.sqlite3_close_v2(db); db = IntPtr.Zero; } }
    }

    public class CookieSite { public string Site = ""; public int Count; public bool SignedIn, Tracker; public HashSet<string> Browsers = new HashSet<string>(); }

    public static class CookieKeep
    {
        public static string ListFileOverride, WorkOverride;   // self-test
        static string ListFile { get { return ListFileOverride ?? Paths.FileIn("cookie-keep.txt"); } }
        static string Work { get { return WorkOverride ?? Path.Combine(Paths.Data, "cookie-work"); } }

        static readonly HashSet<string> TwoPart = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "co.uk", "org.uk", "ac.uk", "gov.uk", "co.il", "org.il", "ac.il", "gov.il", "net.il", "muni.il", "com.au", "net.au", "org.au", "co.nz", "co.jp", "ne.jp",
          "com.br", "com.mx", "com.tr", "co.in", "co.za", "com.cn", "com.tw", "com.hk", "com.sg", "co.kr", "com.ar", "com.ua", "com.pl" };
        static readonly string[] Trackers = { "doubleclick.net", "google-analytics.com", "googletagmanager.com", "googlesyndication.com", "googleadservices.com", "adnxs.com",
                                              "scorecardresearch.com", "criteo.com", "criteo.net", "taboola.com", "outbrain.com", "rubiconproject.com", "pubmatic.com",
                                              "casalemedia.com", "openx.net", "quantserve.com", "bing.com", "clarity.ms", "hotjar.com", "adsrvr.org", "demdex.net",
                                              "everesttech.net", "rlcdn.com", "bluekai.com", "mathtag.com", "yieldmo.com", "amazon-adsystem.com", "facebook.net", "tiktokw.us" };
        static readonly Regex SignIn = new Regex(@"(sess|sid\b|^sid|ssid|auth|token|login|logged|account|^user|__secure-|__host-|sapisid|estsauth|mspauth|rpssecauth|remember)", RegexOptions.IgnoreCase);
        static readonly Regex SiteOk = new Regex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$");

        // "accounts.google.com" / ".google.com" -> "google.com"; "x.bank.co.il" -> "bank.co.il"
        public static string SiteOf(string host)
        {
            string h = (host ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (h.Length == 0) return "";
            System.Net.IPAddress ip; if (System.Net.IPAddress.TryParse(h, out ip) || !h.Contains(".")) return h;
            var p = h.Split('.');
            int keep = p.Length >= 3 && TwoPart.Contains(p[p.Length - 2] + "." + p[p.Length - 1]) ? 3 : 2;
            return string.Join(".", p.Skip(Math.Max(0, p.Length - keep)));
        }
        public static bool Kept(string host, ICollection<string> keep) { return keep.Contains(SiteOf(host)); }

        // ---- the list (Elixar's protected folder) ----
        public static List<string> Load()
        {
            try { return File.Exists(ListFile) ? File.ReadAllLines(ListFile).Select(x => x.Trim().ToLowerInvariant()).Where(x => SiteOk.IsMatch(x)).Distinct().ToList() : new List<string>(); }
            catch { return new List<string>(); }
        }
        public static void Save(IEnumerable<string> sites)
        {
            var l = sites.Select(x => (x ?? "").Trim().ToLowerInvariant()).Where(x => SiteOk.IsMatch(x)).Distinct().OrderBy(x => x).Take(500).ToList();
            SafeFile.Save(ListFile, string.Join("\n", l));
        }

        // ---- cookie files: Chromium (table cookies, column host_key) and Firefox (moz_cookies, host) ----
        public class CookieDb { public string File = "", Browser = "", Table = "", Column = ""; }
        public static Func<List<CookieDb>> DbsOverride;
        public static List<CookieDb> Dbs()
        {
            if (DbsOverride != null) return DbsOverride();
            var l = new List<CookieDb>();
            var names = new[] { "Chrome", "Edge", "Brave" }; var roots = Privacy.BrowserRoots();
            for (int i = 0; i < roots.Count; i++)
                foreach (var f in Privacy.ProfileFiles(new[] { roots[i] }, new[] { Path.Combine("Network", "Cookies"), "Cookies" }))
                    l.Add(new CookieDb { File = f, Browser = i < names.Length ? names[i] : "Chromium", Table = "cookies", Column = "host_key" });
            string ff = Path.Combine(UserDirs.Roaming, "Mozilla", "Firefox", "Profiles");
            foreach (var f in Privacy.FirefoxCookies(ff).Where(x => x.EndsWith("cookies.sqlite", StringComparison.OrdinalIgnoreCase)))
                l.Add(new CookieDb { File = f, Browser = "Firefox", Table = "moz_cookies", Column = "host" });
            return l;
        }
        static readonly string[] Sidecars = { "-journal", "-wal", "-shm" };

        // a private copy (with its journal) in Elixar's folder, through checked handles; null if the browser holds it
        static string CopyIn(CookieDb d)
        {
            string dir = Path.Combine(Work, Guid.NewGuid().ToString("N"));
            if (Win.IsWindows) { if (!Locked.Lock(Work, false)) throw new IOException("Elixar's work folder isn't safe."); SafeFile.MakeDir(dir); }
            else Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, "c.sqlite");
            CopyFile(d.File, copy);
            foreach (var s in Sidecars) if (File.Exists(d.File + s)) CopyFile(d.File + s, copy + s);
            return copy;
        }
        static void CopyFile(string from, string to) { if (Win.IsWindows) SafeFile.Copy(from, to); else File.Copy(from, to); }
        static void DropWork(string copy) { try { string dir = Path.GetDirectoryName(copy); if (Win.IsWindows) SafeDelete.Delete(dir); else Directory.Delete(dir, true); } catch { } }

        static List<KeyValuePair<long, string>> Read(string copy, CookieDb d)
        {
            if (!Regex.IsMatch(d.Table + d.Column, "^[a-z_]+$")) throw new ArgumentException("table");
            using (var db = Open(copy, d)) return db.Rows("SELECT rowid, " + d.Column + ", name FROM " + d.Table, 2);
        }

        // The cookie file is in your folders, so any program you run could have replaced it. Elixar edits it with
        // administrator rights, so: no triggers or views (code that would run with those rights when rows are deleted),
        // the cookie table must be a plain table, and the schema is not trusted to call functions.
        static SqliteDb Open(string copy, CookieDb d)
        {
            var db = new SqliteDb(copy, false);
            try
            {
                try { db.Exec("PRAGMA trusted_schema = OFF"); } catch { }
                var odd = db.Rows("SELECT rowid, type || ' ' || name FROM sqlite_master WHERE type IN ('trigger', 'view')");
                if (odd.Count > 0) throw new IOException("the cookie file has code in it (" + odd[0].Value + "), so Elixar won't edit it");
                var t = db.Rows("SELECT rowid, type FROM sqlite_master WHERE name = '" + d.Table + "'");
                if (t.Count != 1 || t[0].Value != "table") throw new IOException("not a browser cookie file");
                return db;
            }
            catch { db.Dispose(); throw; }
        }

        // every site with cookies, in every browser (closed or not: read from a copy)
        public static List<CookieSite> Sites(out List<string> problems)
        {
            problems = new List<string>();
            var map = new Dictionary<string, CookieSite>();
            foreach (var d in Dbs())
            {
                string copy = null;
                try
                {
                    copy = CopyIn(d);
                    foreach (var r in Read(copy, d))
                    {
                        var parts = r.Value.Split('\t'); string site = SiteOf(parts[0]); if (site.Length == 0) continue;
                        CookieSite s; if (!map.TryGetValue(site, out s)) map[site] = s = new CookieSite { Site = site, Tracker = Trackers.Any(t => site == t || site.EndsWith("." + t)) };
                        s.Count++; s.Browsers.Add(d.Browser);
                        if (parts.Length > 1 && SignIn.IsMatch(parts[1]) && !s.Tracker) s.SignedIn = true;
                    }
                }
                catch (Exception e) { problems.Add(d.Browser + ": " + e.Message); }
                finally { if (copy != null) DropWork(copy); }
            }
            return map.Values.OrderByDescending(s => s.SignedIn).ThenByDescending(s => s.Count).ThenBy(s => s.Site).ToList();
        }

        // ---- clean with the keep-list: the other sites' cookies go, the original file goes to Undo ----
        public static int Clean(IEnumerable<CookieDb> dbs, ICollection<string> keep, Action<string> log)
        {
            int removed = 0;
            var list = dbs.ToList();
            if (list.Count == 0) return 0;
            var batch = Quarantine.Begin("Cookies (kept " + keep.Count + " sites)", "cookies", list.Sum(d => SafeLen(d.File)));
            try
            {
                foreach (var d in list)
                {
                    string copy = null;
                    try
                    {
                        copy = CopyIn(d);
                        var rows = Read(copy, d);
                        var drop = rows.Where(r => !Kept(r.Value.Split('\t')[0], keep)).Select(r => r.Key).ToList();
                        if (drop.Count == 0) { if (log != null) log("  " + d.Browser + ": only kept sites' cookies, nothing to remove"); continue; }
                        using (var db = Open(copy, d))
                        {
                            db.DeleteIds(d.Table, drop);
                            try { db.Exec("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
                            db.Exec("VACUUM");   // the removed cookies' bytes leave the file
                        }
                        foreach (var s in Sidecars) if (File.Exists(copy + s)) { if (Win.IsWindows) SafeDelete.Delete(copy + s); else File.Delete(copy + s); }
                        // the original (and its journal) to Undo, then the edited copy in its place
                        Quarantine.MoveIn(batch, d.File, log);
                        foreach (var s in Sidecars) if (File.Exists(d.File + s)) Quarantine.MoveIn(batch, d.File + s, log);
                        CopyFile(copy, d.File);
                        removed += drop.Count;
                        if (log != null) log("  " + d.Browser + ": removed " + drop.Count + " cookies, kept " + (rows.Count - drop.Count));
                    }
                    catch (Exception e) { if (log != null) log("  " + d.Browser + ": not changed (" + e.Message + ")"); }
                    finally { if (copy != null) DropWork(copy); }
                }
            }
            finally { if (batch.Items.Count > 0) Quarantine.Commit(batch); else Quarantine.Discard(batch); }
            return removed;
        }
        static long SafeLen(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }
    }
}
