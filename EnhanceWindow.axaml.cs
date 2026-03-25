using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MultiPlayerAll;

public partial class EnhanceWindow : Window
{
    private readonly WriteableBitmap _original;
    private readonly int _width;
    private readonly int _height;
    private readonly double _timestamp;
    private WriteableBitmap _processed;

    public EnhanceWindow(string screenshotPath, double timestamp)
    {
        InitializeComponent();
        _timestamp = timestamp;

        var ts = TimeSpan.FromSeconds(timestamp);
        TimestampLabel.Text = $"Frame at {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";

        // Load the screenshot into a WriteableBitmap via render
        using var stream = File.OpenRead(screenshotPath);
        var bmp = new Avalonia.Media.Imaging.Bitmap(stream);
        _width = bmp.PixelSize.Width;
        _height = bmp.PixelSize.Height;

        _original = new WriteableBitmap(bmp.PixelSize, bmp.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var dst = _original.Lock())
        {
            bmp.CopyPixels(new PixelRect(0, 0, _width, _height), dst.Address, dst.RowBytes * _height, dst.RowBytes);
        }

        _processed = _original;
        EnhancedImage.Source = _original;

        // Wire up slider events
        BrightnessSlider.ValueChanged += (_, _) => ApplyEnhancements();
        ContrastSlider.ValueChanged += (_, _) => ApplyEnhancements();
        SharpnessSlider.ValueChanged += (_, _) => ApplyEnhancements();
        GrayscaleCheck.IsCheckedChanged += (_, _) => ApplyEnhancements();
        InvertCheck.IsCheckedChanged += (_, _) => ApplyEnhancements();
        ZoomSlider.ValueChanged += (_, _) =>
        {
            var zoom = ZoomSlider.Value / 100.0;
            EnhancedImage.Width = _width * zoom;
            EnhancedImage.Height = _height * zoom;
            ZoomValue.Text = $"{(int)ZoomSlider.Value}%";
        };
    }

    private unsafe void ApplyEnhancements()
    {
        if (_original == null || BrightnessSlider == null) return;

        var brightness = (int)BrightnessSlider.Value;
        var contrast = (int)ContrastSlider.Value;
        var sharpness = (int)SharpnessSlider.Value;
        var grayscale = GrayscaleCheck?.IsChecked == true;
        var invert = InvertCheck?.IsChecked == true;

        BrightnessValue.Text = brightness.ToString();
        ContrastValue.Text = contrast.ToString();
        SharpnessValue.Text = sharpness.ToString();

        var result = new WriteableBitmap(_original.PixelSize, _original.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        using (var src = _original.Lock())
        using (var dst = result.Lock())
        {
            byte* srcPtr = (byte*)src.Address;
            byte* dstPtr = (byte*)dst.Address;
            int pixels = _width * _height;

            // Contrast factor
            double contrastFactor = (259.0 * (contrast + 255)) / (255.0 * (259 - contrast));

            for (int p = 0; p < pixels; p++)
            {
                int i = p * 4;
                double b = srcPtr[i];      // Blue
                double g = srcPtr[i + 1];  // Green
                double r = srcPtr[i + 2];  // Red
                byte a = srcPtr[i + 3];    // Alpha

                // Brightness
                r += brightness;
                g += brightness;
                b += brightness;

                // Contrast
                r = contrastFactor * (r - 128) + 128;
                g = contrastFactor * (g - 128) + 128;
                b = contrastFactor * (b - 128) + 128;

                // Grayscale
                if (grayscale)
                {
                    double gray = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = g = b = gray;
                }

                // Invert
                if (invert)
                {
                    r = 255 - r;
                    g = 255 - g;
                    b = 255 - b;
                }

                dstPtr[i] = Clamp(b);
                dstPtr[i + 1] = Clamp(g);
                dstPtr[i + 2] = Clamp(r);
                dstPtr[i + 3] = a;
            }
        }

        // Simple sharpness (unsharp mask approximation) - apply if > 0
        if (sharpness > 0)
        {
            var sharpened = ApplySharpness(result, sharpness / 100.0);
            _processed = sharpened;
        }
        else
        {
            _processed = result;
        }

        EnhancedImage.Source = _processed;
    }

    private unsafe WriteableBitmap ApplySharpness(WriteableBitmap input, double amount)
    {
        var output = new WriteableBitmap(input.PixelSize, input.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        using var src = input.Lock();
        using var dst = output.Lock();

        byte* srcPtr = (byte*)src.Address;
        byte* dstPtr = (byte*)dst.Address;
        int stride = src.RowBytes;

        for (int y = 1; y < _height - 1; y++)
        {
            for (int x = 1; x < _width - 1; x++)
            {
                for (int c = 0; c < 3; c++) // B, G, R
                {
                    int idx = y * stride + x * 4 + c;
                    double center = srcPtr[idx];
                    double neighbors =
                        srcPtr[(y - 1) * stride + x * 4 + c] +
                        srcPtr[(y + 1) * stride + x * 4 + c] +
                        srcPtr[y * stride + (x - 1) * 4 + c] +
                        srcPtr[y * stride + (x + 1) * 4 + c];

                    double sharpened = center + amount * (4 * center - neighbors);
                    dstPtr[idx] = Clamp(sharpened);
                }
                dstPtr[y * stride + x * 4 + 3] = srcPtr[y * stride + x * 4 + 3]; // alpha
            }
        }

        return output;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)v, 0, 255);

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 0;
        SharpnessSlider.Value = 0;
        ZoomSlider.Value = 100;
        GrayscaleCheck.IsChecked = false;
        InvertCheck.IsChecked = false;
        EnhancedImage.Width = double.NaN;
        EnhancedImage.Height = double.NaN;
        EnhancedImage.Source = _original;
    }

    private async void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "MultiPlayerAll", "enhanced.png");
            _processed.Save(tempPath);

            if (OperatingSystem.IsWindows())
            {
                var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-Command \"Add-Type -Assembly System.Windows.Forms; [System.Windows.Forms.Clipboard]::SetImage([System.Drawing.Image]::FromFile('{tempPath.Replace("'", "''")}'))\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                ps?.WaitForExit(3000);
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("osascript",
                    $"-e \"set the clipboard to (read (POSIX file \\\"{tempPath}\\\") as TIFF picture)\"")?.WaitForExit(3000);
            }
            else
            {
                System.Diagnostics.Process.Start("xclip",
                    $"-selection clipboard -t image/png -i \"{tempPath}\"")?.WaitForExit(3000);
            }

            Title = "Enhance — Copied to clipboard!";
            await System.Threading.Tasks.Task.Delay(2000);
            Title = "Enhance";
        }
        catch (Exception ex)
        {
            Title = $"Enhance — Copy failed: {ex.Message}";
        }
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Enhanced Image",
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
            }
        });

        if (file != null)
        {
            _processed.Save(file.Path.LocalPath);
            Title = $"Enhance — Saved to {file.Name}";
        }
    }
}
