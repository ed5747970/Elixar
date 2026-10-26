// CureServer.exe - the Cure Server service and Cure Console (Elixar for Business). The work is in Elixar.exe next to it (CureServer.cs).
using System;
using System.Linq;
using System.ServiceProcess;
using CDriveCleaner;

static class CureServerMain
{
    const string Help =
        "Cure Server (Elixar for Business) - receives Cure Agent reports and shows them in the Cure Console. Reports only.\n\n" +
        "  CureServer.exe --install [--port 8443] [--cert <thumbprint>] [--group \"DOMAIN\\Group\"]\n" +
        "        no --cert: Windows makes a certificate for this server (5 years)\n" +
        "        --group: who may open the Cure Console (this server's administrators always can)\n" +
        "  CureServer.exe --new-token        new enrollment token for new PCs (enrolled PCs keep reporting)\n" +
        "  CureServer.exe --remove-pc <name> take a PC off the list (a PC still running Cure Agent joins again unless the token is changed)\n" +
        "  CureServer.exe --uninstall [--purge]   --purge also deletes the reports";

    static string Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static int Main(string[] args)
    {
        if (args.Contains("--service") || (args.Length == 0 && !Environment.UserInteractive))   // the service is started with --service; msiexec/Intune/SCCM run the install options without a desktop
        { ServiceBase.Run(new CureServer.Service()); return 0; }
        try
        {
            if (args.Contains("--install"))
            {
                int port; if (!int.TryParse(Arg(args, "--port") ?? CureServer.DefaultPort.ToString(), out port)) port = -1;
                var r = CureSetup.InstallServer(System.Reflection.Assembly.GetEntryAssembly().Location, port, Arg(args, "--cert"), Arg(args, "--group"));
                if (r.Error != null) { Console.WriteLine(r.Error); return 1; }
                Console.WriteLine("Cure Server is running.\n\n  Cure Console:      " + r.Url + "\n  Certificate:       " + r.Thumbprint + "\n  Enrollment token:  " + r.Token +
                                  "  (shown once; keep it private)\n\nSettings for every PC (Group Policy / Intune): " + r.RegFile);
                return 0;
            }
            if (args.Contains("--new-token")) { var t = CureSetup.NewServerToken(); Console.WriteLine(t == null ? "Cure Server isn't installed." : "New enrollment token: " + t); return t == null ? 1 : 0; }
            if (args.Contains("--remove-pc"))
            {
                CureSetup.StopServer();   // the running service keeps the list in memory
                var s = CureServer.FromRegistry(); bool ok = s.Remove(Arg(args, "--remove-pc") ?? "");
                CureSetup.StartServer();
                Console.WriteLine(ok ? "Removed from the list. Its old key is refused; if Cure Agent still runs there, it joins again with the enrollment token (make a new one with --new-token to keep it out)." : "No PC by that name."); return ok ? 0 : 1;
            }
            if (args.Contains("--uninstall")) { CureSetup.UninstallServer(args.Contains("--purge")); Console.WriteLine("Cure Server is removed."); return 0; }
        }
        catch (Exception e) { Console.WriteLine("Cure Server: " + e.Message); return 1; }
        Console.WriteLine(Help); return 4;
    }
}
