// A pretend IMAP mail server on this PC, for the self-test and previews only (it never touches a real mailbox).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CDriveCleaner
{
    public class FakeImap : IDisposable
    {
        readonly TcpListener listener;
        public int Port, Moved, Logins, BadLogins;
        public List<string> Commands = new List<string>();
        public string User = "me@example.com", Pass = "abcd efgh";
        public Dictionary<string, List<long>> Boxes = new Dictionary<string, List<long>>
        {
            { "INBOX", Enumerable.Range(101, 40).Select(i => (long)i).Concat(new long[] { 201, 202, 203 }).ToList() },
            { "Bulk Mail", Enumerable.Range(1, 12).Select(i => (long)i).ToList() },
            { "Trash", new List<long>() },
        };

        public FakeImap()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(Loop) { IsBackground = true }.Start();
        }

        public Stream Connect() { var c = new TcpClient("127.0.0.1", Port); return c.GetStream(); }

        void Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                new Thread(() => Serve(c)) { IsBackground = true }.Start();
            }
        }

        void Serve(TcpClient c)
        {
            using (c)
            using (var s = c.GetStream())
            {
                var r = new StreamReader(s, Encoding.ASCII);
                Action<string> W = x => { var b = Encoding.UTF8.GetBytes(x + "\r\n"); s.Write(b, 0, b.Length); };
                W("* OK Fake IMAP ready");
                string box = null;
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    lock (Commands) Commands.Add(Regex.Replace(line, "LOGIN .*", "LOGIN ***"));
                    var p = line.Split(new[] { ' ' }, 3);
                    string tag = p[0], cmd = p.Length > 1 ? p[1].ToUpperInvariant() : "", rest = p.Length > 2 ? p[2] : "";
                    if (cmd == "UID") { var q = rest.Split(new[] { ' ' }, 2); cmd = "UID " + q[0].ToUpperInvariant(); rest = q.Length > 1 ? q[1] : ""; }
                    switch (cmd)
                    {
                        case "CAPABILITY": W("* CAPABILITY IMAP4rev1 MOVE UIDPLUS SPECIAL-USE"); W(tag + " OK done"); break;
                        case "LOGIN":
                            if (rest == "\"" + User + "\" \"" + Pass + "\"") { Logins++; W(tag + " OK logged in"); }
                            else { BadLogins++; W(tag + " NO [AUTHENTICATIONFAILED] Invalid credentials"); }
                            break;
                        case "LIST":
                            W("* LIST (\\HasNoChildren) \"/\" \"INBOX\""); W("* LIST (\\HasNoChildren \\Junk) \"/\" \"Bulk Mail\"");
                            W("* LIST (\\HasNoChildren \\Trash) \"/\" \"Trash\""); W("* LIST (\\HasNoChildren \\Sent) \"/\" \"Sent\""); W(tag + " OK done"); break;
                        case "EXAMINE": case "SELECT":
                            box = rest.Trim('"'); W("* " + (Boxes.ContainsKey(box) ? Boxes[box].Count : 0) + " EXISTS"); W(tag + " OK [" + (cmd == "EXAMINE" ? "READ-ONLY" : "READ-WRITE") + "] done"); break;
                        case "UID SEARCH":
                        {
                            var all = box != null && Boxes.ContainsKey(box) ? Boxes[box] : new List<long>();
                            IEnumerable<long> hit = all;
                            if (rest.Contains("List-Unsubscribe")) hit = all.Where(u => u >= 101 && u < 200);
                            else if (rest.Contains("LARGER")) hit = all.Where(u => u >= 200);
                            W("* SEARCH " + string.Join(" ", hit)); W(tag + " OK done");
                            break;
                        }
                        case "UID FETCH":
                        {
                            var uids = rest.Split(' ')[0].Split(',').Select(long.Parse).ToList();
                            int n = 1;
                            foreach (var u in uids)
                            {
                                string hdr = "From: " + (u % 3 == 0 ? "\"Shop Deals\" <deals@shop.example>" : u % 3 == 1 ? "News Weekly <news@weekly.example>" : "noreply@social.example") + "\r\n\r\n";
                                W("* " + (n++) + " FETCH (UID " + u + " RFC822.SIZE " + (u >= 200 ? 15000000 : 30000 + u * 10) + " BODY[HEADER.FIELDS (FROM)] {" + Encoding.UTF8.GetByteCount(hdr) + "}");
                                var b = Encoding.UTF8.GetBytes(hdr); s.Write(b, 0, b.Length);
                                W(")");
                            }
                            W(tag + " OK done");
                            break;
                        }
                        case "UID MOVE":
                        {
                            var q = rest.Split(new[] { ' ' }, 2);
                            var uids = q[0].Split(',').Select(long.Parse).ToList();
                            string dest = q[1].Trim('"');
                            if (box == null || !Boxes.ContainsKey(dest)) { W(tag + " NO no such folder"); break; }
                            int m = Boxes[box].RemoveAll(uids.Contains);
                            lock (this) Moved += m;
                            Boxes[dest].AddRange(uids);
                            W(tag + " OK moved");
                            break;
                        }
                        case "LOGOUT": W("* BYE"); W(tag + " OK bye"); return;
                        default: W(tag + " BAD unknown"); break;
                    }
                }
            }
        }

        public void Dispose() { try { listener.Stop(); } catch { } }
    }
}
