// CureAgent.exe - the Cure Agent service (Elixar for Business). The work is in Elixar.exe next to it (CureAgent.cs).
using System;
using System.Linq;
using System.ServiceProcess;
using CDriveCleaner;

static class CureAgentMain
{
    const string Help =
        "Cure Agent (Elixar for Business) - sends this PC's status to your company's Cure Server. Reports only.\n\n" +
        "  CureAgent.exe --install --server https://<server>:8443/ --token <token> [--thumbprint <certificate thumbprint>] [--interval <minutes>]\n" +
        "  CureAgent.exe --uninstall\n" +
        "  CureAgent.exe --send       send a report now and show the answer\n" +
        "  CureAgent.exe --show       show exactly what a report contains (nothing is sent)\n" +
        "  CureAgent.exe --status\n\n" +
        "Group Policy can set the same: HKLM\\SOFTWARE\\Policies\\Elixar\\Cure (Server, EnrollToken, Thumbprint, Interval).";

    static string Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static int Main(string[] args)
    {
        if (args.Contains("--service") || !Environment.UserInteractive) { ServiceBase.Run(new CureAgent.Service()); return 0; }
        try
        {
            if (args.Contains("--install"))
            {
                var c = new CureConfig { Server = Arg(args, "--server") ?? "", Token = Arg(args, "--token") ?? "", Thumbprint = new string((Arg(args, "--thumbprint") ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant() };
                int iv; if (int.TryParse(Arg(args, "--interval") ?? "", out iv)) c.Interval = Math.Max(CureProtocol.MinInterval, Math.Min(CureProtocol.MaxInterval, iv));
                string err = CureSetup.InstallAgent(System.Reflection.Assembly.GetEntryAssembly().Location, c);
                Console.WriteLine(err ?? "Cure Agent is installed and starts with Windows. It reports to " + c.Server);
                return err == null ? 0 : 1;
            }
            if (args.Contains("--uninstall")) { CureSetup.UninstallAgent(); Console.WriteLine("Cure Agent is removed."); return 0; }
            if (args.Contains("--show")) { Console.WriteLine(CureProtocol.Json().Serialize(CureAgent.Collect())); return 0; }
            if (args.Contains("--send")) { var r = CureAgent.SendOnce(); CureAgent.WriteStatus(r); Console.WriteLine(r.Sent ? "Sent. Next report in " + r.NextMinutes + " minutes." : "Not sent: " + r.Error); return r.Sent ? 0 : 1; }
            if (args.Contains("--status"))
            {
                var c = CureAgent.Config(); var d = CureAgent.LoadDevice();
                Console.WriteLine("Server:   " + (c.Server.Length > 0 ? c.Server + " (from " + c.From + ")" : "not set"));
                Console.WriteLine("Settings: " + (c.Error ?? "OK") + (c.Thumbprint.Length > 0 ? " - only the certificate " + c.Thumbprint + " is accepted" : ""));
                Console.WriteLine("This PC:  " + (d != null ? "enrolled as " + d.Id : "not enrolled yet"));
                try { Console.WriteLine("Last:     " + System.IO.File.ReadAllText(CureAgent.StatusFile)); } catch { Console.WriteLine("Last:     nothing sent yet"); }
                return 0;
            }
        }
        catch (Exception e) { Console.WriteLine("Cure Agent: " + e.Message); return 1; }
        Console.WriteLine(Help); return 4;
    }
}
