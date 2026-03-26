using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace MultiPlayerAll;

public partial class EnhanceWindow : Window
{
    private readonly WriteableBitmap _original;
    private readonly int _width;
    private readonly int _height;
    private readonly double _timestamp;
    private WriteableBitmap _processed;
    private int _upscaleFactor = 1;
    private string _channel = "all";

    // Pan/Zoom
    private double _zoomLevel = 1.0;
    private double _panX, _panY;
    private Point _lastPanPoint;
    private bool _isPanning;
    private TranslateTransform _translateTransform = new();
    private ScaleTransform _scaleTransform = new(1, 1);

    public EnhanceWindow() { InitializeComponent(); }

    public EnhanceWindow(string screenshotPath, double timestamp)
    {
        InitializeComponent();
        _timestamp = timestamp;

        var ts = TimeSpan.FromSeconds(timestamp);
        TimestampLabel.Text = $"Frame at {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";

        using var stream = File.OpenRead(screenshotPath);
        var bmp = new Avalonia.Media.Imaging.Bitmap(stream);
        _width = bmp.PixelSize.Width;
        _height = bmp.PixelSize.Height;

        _original = new WriteableBitmap(bmp.PixelSize, bmp.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var dst = _original.Lock())
            bmp.CopyPixels(new PixelRect(0, 0, _width, _height), dst.Address, dst.RowBytes * _height, dst.RowBytes);

        _processed = _original;
        EnhancedImage.Source = _original;

        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_scaleTransform);
        transformGroup.Children.Add(_translateTransform);
        EnhancedImage.RenderTransform = transformGroup;

        BrightnessSlider.ValueChanged += (_, _) => ApplyEnhancements();
        ContrastSlider.ValueChanged += (_, _) => ApplyEnhancements();
        SharpnessSlider.ValueChanged += (_, _) => ApplyEnhancements();
        GammaSlider.ValueChanged += (_, _) => ApplyEnhancements();
        ThresholdSlider.ValueChanged += (_, _) => ApplyEnhancements();
        DenoiseSlider.ValueChanged += (_, _) => ApplyEnhancements();
        EdgeSlider.ValueChanged += (_, _) => ApplyEnhancements();
        ClaheSlider.ValueChanged += (_, _) => ApplyEnhancements();
        GrayscaleCheck.IsCheckedChanged += (_, _) => ApplyEnhancements();
        InvertCheck.IsCheckedChanged += (_, _) => ApplyEnhancements();
    }

    private void UpscaleCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UpscaleCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _upscaleFactor = int.Parse(tag);
            ApplyEnhancements();
        }
    }

    private void ChannelCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ChannelCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _channel = tag;
            ApplyEnhancements();
        }
    }

    // ── Mouse Wheel Zoom ─────────────────────────────────────────────
    private void ImageContainer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(EnhancedImage);
        double oldZoom = _zoomLevel;
        double delta = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        _zoomLevel = Math.Clamp(_zoomLevel * delta, 0.5, 20.0);
        double ratio = _zoomLevel / oldZoom;
        _panX = pos.X - ratio * (pos.X - _panX);
        _panY = pos.Y - ratio * (pos.Y - _panY);
        UpdateTransform();
        e.Handled = true;
    }

    private void ImageContainer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ImageContainer).Properties.IsLeftButtonPressed && _zoomLevel > 1.0)
        {
            _isPanning = true;
            _lastPanPoint = e.GetPosition(ImageContainer);
            e.Handled = true;
        }
    }

    private void ImageContainer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning)
        {
            var current = e.GetPosition(ImageContainer);
            _panX += current.X - _lastPanPoint.X;
            _panY += current.Y - _lastPanPoint.Y;
            _lastPanPoint = current;
            UpdateTransform();
        }
    }

    private void ImageContainer_PointerReleased(object? sender, PointerReleasedEventArgs e) => _isPanning = false;

    private void UpdateTransform()
    {
        _scaleTransform.ScaleX = _zoomLevel;
        _scaleTransform.ScaleY = _zoomLevel;
        _translateTransform.X = _panX;
        _translateTransform.Y = _panY;
        ZoomLabel.Text = $"Zoom: {(int)(_zoomLevel * 100)}% (scroll to zoom, drag to pan)";
    }

    // ── Image Processing ─────────────────────────────────────────────
    private unsafe void ApplyEnhancements()
    {
        if (_original == null || BrightnessSlider == null) return;

        var brightness = (int)BrightnessSlider.Value;
        var contrast = (int)ContrastSlider.Value;
        var sharpness = (int)SharpnessSlider.Value;
        var gamma = GammaSlider.Value / 100.0;
        var threshold = (int)ThresholdSlider.Value;
        var denoise = (int)DenoiseSlider.Value;
        var edge = (int)EdgeSlider.Value;
        var clahe = (int)ClaheSlider.Value;
        var grayscale = GrayscaleCheck?.IsChecked == true;
        var invert = InvertCheck?.IsChecked == true;

        BrightnessValue.Text = brightness.ToString();
        ContrastValue.Text = contrast.ToString();
        SharpnessValue.Text = sharpness.ToString();
        GammaValue.Text = gamma.ToString("F2");
        ThresholdValue.Text = threshold > 0 ? threshold.ToString() : "Off";
        DenoiseValue.Text = denoise > 0 ? denoise.ToString() : "Off";
        EdgeValue.Text = edge > 0 ? edge.ToString() : "Off";
        ClaheValue.Text = clahe > 0 ? clahe.ToString() : "Off";

        // Upscale source if needed
        int w = _width * _upscaleFactor;
        int h = _height * _upscaleFactor;
        WriteableBitmap source;

        if (_upscaleFactor > 1)
        {
            source = Upscale(_original, _width, _height, _upscaleFactor);
        }
        else
        {
            source = _original;
        }

        var result = new WriteableBitmap(new PixelSize(w, h), _original.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        // Gamma LUT
        byte[] gammaLut = new byte[256];
        for (int i = 0; i < 256; i++)
            gammaLut[i] = (byte)Math.Clamp((int)(255 * Math.Pow(i / 255.0, 1.0 / gamma)), 0, 255);

        using (var src = source.Lock())
        using (var dst = result.Lock())
        {
            byte* srcPtr = (byte*)src.Address;
            byte* dstPtr = (byte*)dst.Address;
            int pixels = w * h;
            double contrastFactor = (259.0 * (contrast + 255)) / (255.0 * (259 - contrast));

            for (int p = 0; p < pixels; p++)
            {
                int i = p * 4;
                double b = srcPtr[i];
                double g = srcPtr[i + 1];
                double r = srcPtr[i + 2];
                byte a = srcPtr[i + 3];

                // Brightness + Contrast + Gamma
                r = gammaLut[Clamp(contrastFactor * (r + brightness - 128) + 128)];
                g = gammaLut[Clamp(contrastFactor * (g + brightness - 128) + 128)];
                b = gammaLut[Clamp(contrastFactor * (b + brightness - 128) + 128)];

                // Grayscale
                if (grayscale)
                {
                    double gray = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = g = b = gray;
                }

                // Threshold
                if (threshold > 0)
                {
                    double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = g = b = lum >= threshold ? 255 : 0;
                }

                // Invert
                if (invert) { r = 255 - r; g = 255 - g; b = 255 - b; }

                // Channel isolation
                if (_channel == "r") { g = 0; b = 0; }
                else if (_channel == "g") { r = 0; b = 0; }
                else if (_channel == "b") { r = 0; g = 0; }

                dstPtr[i] = Clamp(b);
                dstPtr[i + 1] = Clamp(g);
                dstPtr[i + 2] = Clamp(r);
                dstPtr[i + 3] = a;
            }
        }

        // Denoise (box blur)
        if (denoise > 0)
            result = BoxBlur(result, w, h, Math.Max(1, denoise / 20));

        // Sharpness
        if (sharpness > 0)
            result = ApplySharpness(result, w, h, sharpness / 100.0);

        // Edge detection (Sobel)
        if (edge > 0)
            result = ApplyEdgeDetect(result, w, h, edge / 100.0);

        // CLAHE (simplified local contrast)
        if (clahe > 0)
            result = ApplyClahe(result, w, h, clahe / 100.0);

        _processed = result;
        EnhancedImage.Source = _processed;
    }

    private static unsafe WriteableBitmap Upscale(WriteableBitmap input, int srcW, int srcH, int factor)
    {
        int dstW = srcW * factor, dstH = srcH * factor;
        var output = new WriteableBitmap(new PixelSize(dstW, dstH), input.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        using var src = input.Lock();
        using var dst = output.Lock();
        byte* s = (byte*)src.Address;
        byte* d = (byte*)dst.Address;
        int srcStride = src.RowBytes, dstStride = dst.RowBytes;

        for (int y = 0; y < dstH; y++)
        {
            double sy = (double)y / factor;
            int y0 = Math.Clamp((int)sy, 0, srcH - 1);
            int y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
            double fy = sy - y0;

            for (int x = 0; x < dstW; x++)
            {
                double sx = (double)x / factor;
                int x0 = Math.Clamp((int)sx, 0, srcW - 1);
                int x1 = Math.Clamp(x0 + 1, 0, srcW - 1);
                double fx = sx - x0;

                for (int c = 0; c < 4; c++)
                {
                    double v00 = s[y0 * srcStride + x0 * 4 + c];
                    double v10 = s[y0 * srcStride + x1 * 4 + c];
                    double v01 = s[y1 * srcStride + x0 * 4 + c];
                    double v11 = s[y1 * srcStride + x1 * 4 + c];

                    double v = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) +
                               v01 * (1 - fx) * fy + v11 * fx * fy;

                    d[y * dstStride + x * 4 + c] = (byte)Math.Clamp((int)v, 0, 255);
                }
            }
        }

        return output;
    }

    private static unsafe WriteableBitmap BoxBlur(WriteableBitmap input, int w, int h, int radius)
    {
        var output = new WriteableBitmap(new PixelSize(w, h), input.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        using var src = input.Lock();
        using var dst = output.Lock();
        byte* s = (byte*)src.Address;
        byte* d = (byte*)dst.Address;
        int stride = src.RowBytes;
        int kernelSize = (2 * radius + 1) * (2 * radius + 1);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (int ky = -radius; ky <= radius; ky++)
                {
                    int sy = Math.Clamp(y + ky, 0, h - 1);
                    for (int kx = -radius; kx <= radius; kx++)
                    {
                        int sx = Math.Clamp(x + kx, 0, w - 1);
                        int idx = sy * stride + sx * 4;
                        sumB += s[idx]; sumG += s[idx + 1]; sumR += s[idx + 2];
                    }
                }
                int di = y * stride + x * 4;
                d[di] = (byte)(sumB / kernelSize);
                d[di + 1] = (byte)(sumG / kernelSize);
                d[di + 2] = (byte)(sumR / kernelSize);
                d[di + 3] = s[di + 3];
            }
        }

        return output;
    }

    private static unsafe WriteableBitmap ApplySharpness(WriteableBitmap input, int w, int h, double amount)
    {
        var output = new WriteableBitmap(new PixelSize(w, h), input.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        using var src = input.Lock();
        using var dst = output.Lock();
        byte* s = (byte*)src.Address;
        byte* d = (byte*)dst.Address;
        int stride = src.RowBytes;

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int idx = y * stride + x * 4 + c;
                    double center = s[idx];
                    double neighbors = s[(y - 1) * stride + x * 4 + c] + s[(y + 1) * stride + x * 4 + c] +
                                       s[y * stride + (x - 1) * 4 + c] + s[y * stride + (x + 1) * 4 + c];
                    d[idx] = Clamp(center + amount * (4 * center - neighbors));
                }
                d[y * stride + x * 4 + 3] = s[y * stride + x * 4 + 3];
            }
        }

        return output;
    }

    private static unsafe WriteableBitmap ApplyEdgeDetect(WriteableBitmap input, int w, int h, double amount)
    {
        var output = new WriteableBitmap(new PixelSize(w, h), input.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        using var src = input.Lock();
        using var dst = output.Lock();
        byte* s = (byte*)src.Address;
        byte* d = (byte*)dst.Address;
        int stride = src.RowBytes;

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int idx = y * stride + x * 4 + c;
                    // Sobel X
                    double gx = -s[(y - 1) * stride + (x - 1) * 4 + c] + s[(y - 1) * stride + (x + 1) * 4 + c]
                              - 2 * s[y * stride + (x - 1) * 4 + c] + 2 * s[y * stride + (x + 1) * 4 + c]
                              - s[(y + 1) * stride + (x - 1) * 4 + c] + s[(y + 1) * stride + (x + 1) * 4 + c];
                    // Sobel Y
                    double gy = -s[(y - 1) * stride + (x - 1) * 4 + c] - 2 * s[(y - 1) * stride + x * 4 + c] - s[(y - 1) * stride + (x + 1) * 4 + c]
                              + s[(y + 1) * stride + (x - 1) * 4 + c] + 2 * s[(y + 1) * stride + x * 4 + c] + s[(y + 1) * stride + (x + 1) * 4 + c];

                    double mag = Math.Sqrt(gx * gx + gy * gy);
                    // Blend edge with original
                    double orig = s[idx];
                    d[idx] = Clamp(orig * (1 - amount) + mag * amount);
                }
                d[y * stride + x * 4 + 3] = s[y * stride + x * 4 + 3];
            }
        }

        return output;
    }

    private static unsafe WriteableBitmap ApplyClahe(WriteableBitmap input, int w, int h, double strength)
    {
        // Simplified local contrast: compare each pixel to its local average and boost the difference
        var output = new WriteableBitmap(new PixelSize(w, h), input.Dpi,
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        int radius = Math.Max(4, w / 40); // adaptive radius

        using var src = input.Lock();
        using var dst = output.Lock();
        byte* s = (byte*)src.Address;
        byte* d = (byte*)dst.Address;
        int stride = src.RowBytes;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int idx = y * stride + x * 4 + c;
                    double pixel = s[idx];

                    // Sample local average (sparse for speed)
                    double sum = 0;
                    int count = 0;
                    for (int ky = -radius; ky <= radius; ky += 2)
                    {
                        int sy = Math.Clamp(y + ky, 0, h - 1);
                        for (int kx = -radius; kx <= radius; kx += 2)
                        {
                            int sx = Math.Clamp(x + kx, 0, w - 1);
                            sum += s[sy * stride + sx * 4 + c];
                            count++;
                        }
                    }
                    double localAvg = sum / count;
                    double diff = pixel - localAvg;
                    d[idx] = Clamp(pixel + diff * strength);
                }
                d[y * stride + x * 4 + 3] = s[y * stride + x * 4 + 3];
            }
        }

        return output;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)v, 0, 255);

    private async void AiEnhanceButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            StatusLabel.Text = "AI Enhance: Sending image to server...";
            StatusLabel.Foreground = Brushes.Yellow;

            // Save current processed image to temp file
            var tempPath = Path.Combine(Path.GetTempPath(), "MultiPlayerAll", "ai_input.png");
            _processed.Save(tempPath);

            // Send to Real-ESRGAN server
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var form = new System.Net.Http.MultipartFormDataContent();
            var imageBytes = await File.ReadAllBytesAsync(tempPath);
            form.Add(new System.Net.Http.ByteArrayContent(imageBytes), "image", "frame.png");
            form.Add(new System.Net.Http.StringContent("4"), "scale");

            StatusLabel.Text = "AI Enhance: Processing on server (may take 10-30 seconds)...";
            var response = await client.PostAsync("http://rmansava.mynetgear.com:9191/api/VideoArchive/enhance", form);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                StatusLabel.Text = $"AI Enhance failed: {err.Substring(0, Math.Min(err.Length, 100))}";
                StatusLabel.Foreground = Brushes.OrangeRed;
                return;
            }

            var resultBytes = await response.Content.ReadAsByteArrayAsync();
            using var ms = new MemoryStream(resultBytes);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);

            // Convert to WriteableBitmap
            int newW = bmp.PixelSize.Width, newH = bmp.PixelSize.Height;
            var result = new WriteableBitmap(bmp.PixelSize, bmp.Dpi,
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var dst = result.Lock())
                bmp.CopyPixels(new PixelRect(0, 0, newW, newH), dst.Address, dst.RowBytes * newH, dst.RowBytes);

            _processed = result;
            EnhancedImage.Source = _processed;
            StatusLabel.Text = $"AI Enhance complete: {newW}x{newH}";
            StatusLabel.Foreground = Brushes.GreenYellow;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            StatusLabel.Text = "AI Enhance: Server not reachable";
            StatusLabel.Foreground = Brushes.OrangeRed;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"AI Enhance error: {ex.Message}";
            StatusLabel.Foreground = Brushes.OrangeRed;
        }
    }

    private async void OcrButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            StatusLabel.Text = "OCR: Sending image to Gemini...";
            StatusLabel.Foreground = Brushes.Yellow;
            OcrResultBox.IsVisible = true;
            OcrResultBox.Text = "";

            var tempPath = Path.Combine(Path.GetTempPath(), "MultiPlayerAll", "ocr_input.png");
            _processed.Save(tempPath);

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var form = new System.Net.Http.MultipartFormDataContent();
            var imageBytes = await File.ReadAllBytesAsync(tempPath);
            var imageContent = new System.Net.Http.ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imageContent, "image", "frame.png");

            var response = await client.PostAsync("http://rmansava.mynetgear.com:9191/api/VideoArchive/ocr", form);

            if (!response.IsSuccessStatusCode)
            {
                OcrResultBox.Text = $"OCR failed: {response.StatusCode}";
                StatusLabel.Text = "OCR failed";
                StatusLabel.Foreground = Brushes.OrangeRed;
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("text").GetString() ?? "No text found";

            OcrResultBox.Text = text;
            StatusLabel.Text = "OCR complete";
            StatusLabel.Foreground = Brushes.GreenYellow;
        }
        catch (Exception ex)
        {
            OcrResultBox.Text = $"OCR error: {ex.Message}";
            StatusLabel.Text = "OCR failed";
            StatusLabel.Foreground = Brushes.OrangeRed;
        }
    }

    private void AutoTextButton_Click(object? sender, RoutedEventArgs e)
    {
        // Optimal preset for reading blurry text
        GrayscaleCheck.IsChecked = true;
        ContrastSlider.Value = 40;
        GammaSlider.Value = 130;
        SharpnessSlider.Value = 60;
        DenoiseSlider.Value = 20;
        ClaheSlider.Value = 40;
        ThresholdSlider.Value = 0;
        EdgeSlider.Value = 0;
        InvertCheck.IsChecked = false;
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 0;
        SharpnessSlider.Value = 0;
        GammaSlider.Value = 100;
        ThresholdSlider.Value = 0;
        DenoiseSlider.Value = 0;
        EdgeSlider.Value = 0;
        ClaheSlider.Value = 0;
        GrayscaleCheck.IsChecked = false;
        InvertCheck.IsChecked = false;
        UpscaleCombo.SelectedIndex = 0;
        ChannelCombo.SelectedIndex = 0;
        _upscaleFactor = 1;
        _channel = "all";
        _zoomLevel = 1.0;
        _panX = _panY = 0;
        UpdateTransform();
        _processed = _original;
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

            Title = "Enhance - Copied to clipboard!";
            await System.Threading.Tasks.Task.Delay(2000);
            Title = "Enhance";
        }
        catch (Exception ex)
        {
            Title = $"Enhance - Copy failed: {ex.Message}";
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
            Title = $"Enhance - Saved to {file.Name}";
        }
    }
}
