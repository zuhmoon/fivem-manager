using System.Runtime.InteropServices;
using System.Windows;
using Velopack;

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
