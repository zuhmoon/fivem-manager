using System.Runtime.InteropServices;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace FiveMManager;

public partial class App : Application
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static void Main(string[] args)
    {
        // Must be the first thing that runs. The installer and the updater relaunch this exe with
        // hook arguments (--veloapp-install and friends); this call services them and exits. Put any
        // work above it and it happens during install/uninstall too.
        VelopackApp.Build().Run();

        // Diagnosing "it never updates" on a machine you can't attach a debugger to. Prints the raw
        // state instead of the app's silent handling of it.
        if (args.Contains("--updatecheck"))
        {
            AttachConsole(-1);
            try
            {
                var mgr = new UpdateManager(new GithubSource(Core.UpdateRepo, null, false));
                Console.WriteLine($"repo          : {Core.UpdateRepo}");
                Console.WriteLine($"IsInstalled   : {mgr.IsInstalled}");
                Console.WriteLine($"CurrentVersion: {mgr.CurrentVersion}");
                var info = mgr.CheckForUpdatesAsync().GetAwaiter().GetResult();
                Console.WriteLine(info is null ? "result        : already on the latest version"
                                               : $"result        : update available -> {info.TargetFullRelease.Version}");
                Environment.Exit(0);
            }
            catch (Exception ex) { Console.WriteLine("THREW: " + ex); Environment.Exit(1); }
        }

        if (args.Contains("--selftest"))
        {
            AttachConsole(-1); // attach to parent console so output is visible from a terminal
            try { Core.SelfTest(); Console.WriteLine("SELFTEST OK"); Environment.Exit(0); }
            catch (Exception ex) { Console.WriteLine("SELFTEST FAIL: " + ex.Message); Environment.Exit(1); }
        }

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }
}
