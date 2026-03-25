using System;
using System.IO;
using Avalonia;

namespace MultiPlayerAll;

class Program
{
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "MultiPlayerAll", "crash.log");

    [STAThread]
    public static void Main(string[] args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            File.AppendAllText(LogFile, $"{DateTime.Now} [UnhandledException] {e.ExceptionObject}\n\n");
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            File.AppendAllText(LogFile, $"{DateTime.Now} [UnobservedTaskException] {e.Exception}\n\n");
            e.SetObserved();
        };

        try
        {
            var issues = CheckDependencies();
            if (issues.Count > 0)
            {
                // Load the relevant setup guide
                var setupFile = OperatingSystem.IsWindows() ? "SETUP-Windows.md" :
                    OperatingSystem.IsMacOS() ? "SETUP-Mac.md" : "SETUP-Linux.md";
                var appDir = AppContext.BaseDirectory;
                var setupPath = Path.Combine(appDir, setupFile);
                var setupContent = File.Exists(setupPath) ? File.ReadAllText(setupPath) : "";

                var errorMsg = "MISSING DEPENDENCIES\n\n" + string.Join("\n\n", issues);
                var fullMsg = string.IsNullOrEmpty(setupContent)
                    ? errorMsg
                    : errorMsg + "\n\n--- SETUP GUIDE ---\n\n" + setupContent;

                BuildAvaloniaApp().Start((app, _) =>
                {
                    var scroll = new Avalonia.Controls.ScrollViewer
                    {
                        Content = new Avalonia.Controls.TextBlock
                        {
                            Text = fullMsg,
                            Foreground = Avalonia.Media.Brushes.White,
                            FontSize = 13,
                            FontFamily = new Avalonia.Media.FontFamily("Consolas,Courier New,monospace"),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Avalonia.Thickness(20)
                        }
                    };
                    var button = new Avalonia.Controls.Button
                    {
                        Content = "OK",
                        Width = 80,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Margin = new Avalonia.Thickness(0, 10, 0, 15)
                    };
                    var panel = new Avalonia.Controls.DockPanel();
                    Avalonia.Controls.DockPanel.SetDock(button, Avalonia.Controls.Dock.Bottom);
                    panel.Children.Add(button);
                    panel.Children.Add(scroll);

                    var window = new Avalonia.Controls.Window
                    {
                        Title = "MultiPlayerAll - Missing Dependencies",
                        Width = 650,
                        Height = 500,
                        WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
                        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(40, 0, 0)),
                        Content = panel
                    };
                    button.Click += (_, _) => window.Close();
                    window.Show();
                }, args);
                return;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogFile, $"{DateTime.Now} [Main] {ex}\n\n");
            throw;
        }
    }

    private static List<string> CheckDependencies()
    {
        var issues = new List<string>();

        // Check libmpv
        try
        {
            System.Runtime.InteropServices.NativeLibrary.Load(
                OperatingSystem.IsWindows() ? "libmpv-2.dll" :
                OperatingSystem.IsMacOS() ? "libmpv.dylib" : "libmpv.so.2");
        }
        catch
        {
            if (OperatingSystem.IsWindows())
                issues.Add("libmpv-2.dll not found. Make sure it's in the same folder as the executable.");
            else if (OperatingSystem.IsMacOS())
                issues.Add("libmpv not found. Install it with: brew install mpv");
            else
                issues.Add("libmpv not found. Install it with: sudo apt install libmpv-dev (Debian/Ubuntu) or sudo dnf install mpv-libs-devel (Fedora)");
        }

        // Windows: check VC++ runtime
        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Runtime.InteropServices.NativeLibrary.Load("vcruntime140.dll");
            }
            catch
            {
                issues.Add("Visual C++ Runtime not found. Download from: https://aka.ms/vs/17/release/vc_redist.x64.exe");
            }
        }

        if (issues.Count > 0)
        {
            var msg = string.Join("\n\n", issues);
            File.AppendAllText(LogFile, $"{DateTime.Now} [DependencyCheck] {msg}\n\n");
            Console.Error.WriteLine(msg);
        }

        return issues;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            var useAngle = Environment.GetCommandLineArgs().Any(a => a == "--angle");
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    useAngle ? Win32RenderingMode.AngleEgl : Win32RenderingMode.Wgl
                }
            });
        }

        return builder;
    }
}
