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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.Wgl // Force native OpenGL instead of ANGLE
                }
            })
            .LogToTrace();
}
