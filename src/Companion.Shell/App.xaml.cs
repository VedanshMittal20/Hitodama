using System.IO;
using System.Windows;
using Companion.Core;
using Companion.Shell.Windows;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace Companion.Shell;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstance;
    private IslandWindow? _island;
    private NotifyIcon? _trayIcon;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] [Unhandled] {args.ExceptionObject}\n"); }
            catch (Exception loggingException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not write unhandled exception log ({loggingException.GetType().Name}).");
            }
        };
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] [Dispatcher] {args.Exception}\n"); }
            catch (Exception loggingException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not write dispatcher exception log ({loggingException.GetType().Name}).");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] [UnobservedTask] {args.Exception}\n"); }
            catch (Exception loggingException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not write unobserved task log ({loggingException.GetType().Name}).");
            }
        };

        File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] Shell starting...\n");

        _singleInstance = new Mutex(initiallyOwned: true, "Companion.SingleInstance", out bool isNew);
        if (!isNew)
        {
            File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] Another instance is already running. Shutting down.\n");
            Shutdown();
            return;
        }

        _ownsMutex = true;
        base.OnStartup(e);
        _island = new IslandWindow();
        _island.Show();
        CreateTrayIcon();
        try
        {
            _trayIcon?.ShowBalloonTip(3000, "Void", "Void is ready at the top of your screen! Click or scroll to expand.", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show startup balloon ({exception.GetType().Name}).");
        }
        File.AppendAllText(Path.Combine(AppPaths.Root, "shell.log"), $"[{DateTime.UtcNow:u}] Shell started successfully.\n");
    }

    private void CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show island", null, (_, _) => _island?.ShowDock());
        menu.Items.Add("Shape Void", null, (_, _) => _island?.OpenStudioWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _trayIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Void",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => _island?.ToggleDock();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/void.ico", UriKind.Absolute));
            if (resource is not null)
            {
                using (resource.Stream)
                using (var icon = new System.Drawing.Icon(resource.Stream))
                {
                    return (System.Drawing.Icon)icon.Clone();
                }
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not load tray icon resource ({exception.GetType().Name}); using the application icon.");
        }

        return System.Drawing.SystemIcons.Application;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        if (_singleInstance is not null)
        {
            if (_ownsMutex)
            {
                _singleInstance.ReleaseMutex();
            }

            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }
}
