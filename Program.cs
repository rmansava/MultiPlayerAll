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
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.WriteAllText(LogFile, $"{DateTime.Now}\n{ex}\n");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // Prefer native OpenGL (WGL) for mpv compatibility, fall back to default
        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Software
                }
            });
        }

        return builder;
    }
}
