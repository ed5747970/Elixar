// Elixar
// Removes caches, temp files and update leftovers from drive C:.
// Never touches Windows system files, Program Files or your documents.
// Also saves a read-only scan (c-drive-scan.json) for the Elixar Drive Map dashboard.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// every DllImport looks only in System32, never next to Elixar.exe (a Downloads folder is writable by any program)
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
[assembly: AssemblyTitle("Elixar")]
[assembly: AssemblyProduct("Elixar")]
[assembly: AssemblyDescription("Elixar - the cure for a cluttered PC. Made by Elisar.")]
[assembly: AssemblyCompany("Elisar")]
[assembly: AssemblyCopyright("Made by Elisar (c) 2026")]
[assembly: AssemblyVersion("1.2.55.1")]
[assembly: AssemblyFileVersion("1.2.55.1")]

namespace CDriveCleaner
{
    // The code lives in Core.cs, Catalog.cs, DashParts.cs, MainForm.cs, SelfTest.cs and Program.cs (and the other files in src).
}
