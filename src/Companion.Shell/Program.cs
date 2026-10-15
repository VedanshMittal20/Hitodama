using System;
using System.IO;
using Companion.Core;

namespace Companion.Shell;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "crash.log"), $"[{DateTime.UtcNow:u}] [AppDomain] {e.ExceptionObject}\n"); } catch { }
        };

        try
        {
            File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] Program.Main started\n");
            var app = new App();
            app.InitializeComponent();
            File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] app.InitializeComponent succeeded\n");
            app.Run();
            File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] app.Run finished\n");
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "crash.log"), $"[{DateTime.UtcNow:u}] [Main Exception] {ex}\n"); } catch { }
        }
    }
}
