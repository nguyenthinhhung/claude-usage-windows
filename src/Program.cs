using System.Threading;

namespace ClaudeUsageTray;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One tray icon is enough; a second launch just focuses nothing and exits.
        using var mutex = new Mutex(true, @"Local\ClaudeUsageTray.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
            return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
