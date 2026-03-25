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
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogFile, $"{DateTime.Now} [Main] {ex}\n\n");
            throw;
        }
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
