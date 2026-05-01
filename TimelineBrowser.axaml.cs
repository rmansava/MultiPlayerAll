using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MultiPlayerAll;

public partial class TimelineBrowser : Window
{
    private readonly string _remotePath;
    private readonly double _totalDuration;
    private readonly MainWindow _mainWindow;
    private readonly string _apiBaseUrl;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private int _generationVersion;
    private int _thumbWidth = 240;
    private int _thumbHeight = 135;
    private int _loadedCount;

    public TimelineBrowser() { InitializeComponent(); }

    public TimelineBrowser(string remotePath, double totalDuration, MainWindow mainWindow, string apiBaseUrl)
    {
        InitializeComponent();
        _remotePath = remotePath;
        _totalDuration = totalDuration;
        _mainWindow = mainWindow;
        _apiBaseUrl = apiBaseUrl;

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(100);
            await Generate();
        });
    }

    private void GenerateButton_Click(object? sender, RoutedEventArgs e) => _ = Generate();

    private void SizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SizeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int w))
        {
            _thumbWidth = w;
            _thumbHeight = (int)(w * 9.0 / 16.0);
            // Resize existing thumbnails
            foreach (var child in ThumbnailPanel.Children)
            {
                if (child is StackPanel panel && panel.Children.Count > 0 && panel.Children[0] is Image img)
                {
                    img.Width = _thumbWidth;
                    img.Height = _thumbHeight;
                }
            }
        }
    }

    private int GetIntervalSeconds()
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return int.Parse(tag);
        return 30;
    }

    private async Task Generate()
    {
        var interval = GetIntervalSeconds();
        var version = Interlocked.Increment(ref _generationVersion);
        _loadedCount = 0;
        StatusText.Text = "Requesting thumbnails from server...";
        ThumbnailPanel.Children.Clear();

        try
        {
            var url = $"{_apiBaseUrl}/thumbnails?path={Uri.EscapeDataString(_remotePath)}&interval={interval}&width=240&height=135";
            StatusText.Text = $"Path: {_remotePath}";
            try
            {
                var logDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MultiPlayerAll");
                System.IO.Directory.CreateDirectory(logDir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "crash.log"),
                    $"{DateTime.Now} Timeline request: {url}\n");
            }
            catch { }
            var json = await _httpClient.GetStringAsync(url);
            List<ThumbnailApiInfo>? thumbInfos = null;
            string? genStatus = null;

            // Server returns either a plain array (complete) or { status, thumbnails } (generating)
            try
            {
                thumbInfos = System.Text.Json.JsonSerializer.Deserialize<List<ThumbnailApiInfo>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                var resp = System.Text.Json.JsonSerializer.Deserialize<ThumbnailApiResponse>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                thumbInfos = resp?.Thumbnails;
                genStatus = resp?.Status;
            }

            if (version != _generationVersion) return;

            ThumbnailPanel.Children.Clear();

            if (thumbInfos == null || thumbInfos.Count == 0)
            {
                StatusText.Text = "No thumbnails generated — is ffmpeg installed on the server?";
                // generation complete
                return;
            }

            var baseUrl = _apiBaseUrl.Replace("/api/VideoArchive", "");
            StatusText.Text = $"Loading {thumbInfos.Count} thumbnails...";

            foreach (var thumb in thumbInfos)
            {
                if (version != _generationVersion) return;

                var ts = TimeSpan.FromSeconds(thumb.Timestamp);
                var panel = new StackPanel { Margin = new Thickness(4) };

                var img = new Image
                {
                    Width = _thumbWidth,
                    Height = _thumbHeight,
                    Stretch = Stretch.Uniform,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                };

                try
                {
                    var imgData = await _httpClient.GetByteArrayAsync(baseUrl + thumb.Url);
                    using var ms = new MemoryStream(imgData);
                    img.Source = new Avalonia.Media.Imaging.Bitmap(ms);
                }
                catch { }

                var label = new TextBlock
                {
                    Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}",
                    Foreground = Brushes.GreenYellow,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                double seekTo = thumb.Timestamp;
                img.PointerPressed += (_, _) => _mainWindow.JumpFromTimeline(seekTo);

                panel.Children.Add(img);
                panel.Children.Add(label);
                ThumbnailPanel.Children.Add(panel);
            }

            _loadedCount = thumbInfos.Count;

            if (genStatus == "generating")
            {
                StatusText.Text = $"{thumbInfos.Count} thumbnails (generating... auto-refreshing)";
                _ = AutoRefreshUntilComplete(version, interval);
            }
            else
            {
                StatusText.Text = $"{thumbInfos.Count} thumbnails — click to jump";
            }
        }
        catch (Exception ex)
        {
            LogTimeline($"API failed: {ex.Message}. Trying local ffmpeg fallback.");
            StatusText.Text = $"Server can't reach file — trying local ffmpeg...";
            try
            {
                await GenerateLocalAsync(version, interval);
            }
            catch (Exception localEx)
            {
                StatusText.Text = $"Failed ({_remotePath}): server 404, local: {localEx.Message}";
                LogTimeline($"Local fallback failed: {localEx.Message}");
            }
        }

        // generation complete
    }

    private static void LogTimeline(string message)
    {
        try
        {
            var logDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MultiPlayerAll");
            System.IO.Directory.CreateDirectory(logDir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "crash.log"),
                $"{DateTime.Now} Timeline: {message}\n");
        }
        catch { }
    }

    private async Task GenerateLocalAsync(int version, int interval)
    {
        // Locate the actual video file. If the player resolved a network share path,
        // _remotePath should already be that path. If not, try direct access.
        var videoPath = _remotePath;
        if (!File.Exists(videoPath))
        {
            LogTimeline($"Local file not accessible: {videoPath}");
            StatusText.Text = $"File not accessible locally: {videoPath}";
            return;
        }

        // Find bundled ffmpeg next to the exe, fall back to PATH
        var appDir = AppContext.BaseDirectory;
        var bundledFfmpeg = OperatingSystem.IsWindows()
            ? Path.Combine(appDir, "ffmpeg.exe")
            : Path.Combine(appDir, "ffmpeg");
        var ffmpegPath = File.Exists(bundledFfmpeg) ? bundledFfmpeg : "ffmpeg";

        // Cache dir per video (hash the path)
        var hash = Math.Abs(_remotePath.GetHashCode()).ToString("X8");
        var cacheDir = Path.Combine(Path.GetTempPath(), "MultiPlayerAll", "thumbnails",
            $"{hash}_{interval}s_{_thumbWidth}x{_thumbHeight}");
        Directory.CreateDirectory(cacheDir);

        StatusText.Text = $"Generating locally — 0 thumbnails";
        LogTimeline($"Local generation starting");
        LogTimeline($"  ffmpeg path: {ffmpegPath}");
        LogTimeline($"  ffmpeg exists: {File.Exists(ffmpegPath)}");
        LogTimeline($"  video path: {videoPath}");
        LogTimeline($"  video exists: {File.Exists(videoPath)}");
        LogTimeline($"  cache dir: {cacheDir}");
        ThumbnailPanel.Children.Clear();
        int displayed = 0;

        void AddThumbnail(string file, int index)
        {
            var timestamp = index * interval;
            var ts = TimeSpan.FromSeconds(timestamp);
            var panel = new StackPanel { Margin = new Thickness(4) };
            try
            {
                var img = new Image
                {
                    Width = _thumbWidth,
                    Height = _thumbHeight,
                    Stretch = Stretch.Uniform,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Source = new Avalonia.Media.Imaging.Bitmap(file)
                };
                double seekTo = timestamp;
                img.PointerPressed += (_, _) => _mainWindow.JumpFromTimeline(seekTo);
                panel.Children.Add(img);
            }
            catch { return; }
            var label = new TextBlock
            {
                Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}",
                Foreground = Brushes.GreenYellow,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            panel.Children.Add(label);
            ThumbnailPanel.Children.Add(panel);
        }

        // Show any previously cached thumbnails immediately
        var existing = Directory.GetFiles(cacheDir, "thumb_*.jpg").OrderBy(f => f).ToArray();
        foreach (var f in existing)
        {
            AddThumbnail(f, displayed);
            displayed++;
        }
        StatusText.Text = $"Generating locally — {displayed} thumbnails";

        var startTime = DateTime.Now;
        var totalCount = (int)Math.Ceiling(_totalDuration / interval);
        LogTimeline($"  total duration: {_totalDuration:F1}s, generating {totalCount} thumbs at {interval}s intervals");

        // Build list of timestamps needing generation (skip those already cached)
        var needed = new List<int>();
        for (int i = 0; i < totalCount; i++)
        {
            var outFile = Path.Combine(cacheDir, $"thumb_{i:D5}.jpg");
            if (!File.Exists(outFile)) needed.Add(i);
        }
        LogTimeline($"  {needed.Count} thumbs need to be generated, {totalCount - needed.Count} already cached");

        // Track which thumbnails have been displayed so far
        var displayedSet = new HashSet<int>();
        for (int i = 0; i < totalCount; i++)
        {
            var outFile = Path.Combine(cacheDir, $"thumb_{i:D5}.jpg");
            if (File.Exists(outFile))
            {
                AddThumbnail(outFile, i);
                displayedSet.Add(i);
            }
        }
        displayed = displayedSet.Count;
        StatusText.Text = $"Generating locally — {displayed}/{totalCount} thumbnails";

        // Launch ffmpeg processes in parallel for each needed timestamp
        int completed = 0;
        int failed = 0;
        var sem = new System.Threading.SemaphoreSlim(Math.Max(2, Environment.ProcessorCount));
        var tasks = new List<Task>();

        foreach (var idx in needed)
        {
            var ts = idx * interval;
            var outFile = Path.Combine(cacheDir, $"thumb_{idx:D5}.jpg");
            await sem.WaitAsync();
            if (version != _generationVersion) { sem.Release(); break; }
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    // Fast seek before -i (keyframe granularity, <100ms)
                    psi.ArgumentList.Add("-ss");
                    psi.ArgumentList.Add(ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(videoPath);
                    psi.ArgumentList.Add("-vframes");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-vf");
                    psi.ArgumentList.Add($"scale={_thumbWidth}:{_thumbHeight}");
                    psi.ArgumentList.Add("-q:v");
                    psi.ArgumentList.Add("4");
                    psi.ArgumentList.Add("-y");
                    psi.ArgumentList.Add(outFile);

                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p == null) { System.Threading.Interlocked.Increment(ref failed); return; }
                    _ = p.StandardError.ReadToEndAsync();
                    _ = p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    if (p.ExitCode != 0 || !File.Exists(outFile))
                        System.Threading.Interlocked.Increment(ref failed);
                    else
                        System.Threading.Interlocked.Increment(ref completed);
                }
                finally
                {
                    sem.Release();
                }
            }));
        }

        // Poll: show thumbnails as they're generated
        while (version == _generationVersion)
        {
            // Add any newly completed thumbs to UI in order
            for (int i = 0; i < totalCount; i++)
            {
                if (displayedSet.Contains(i)) continue;
                var outFile = Path.Combine(cacheDir, $"thumb_{i:D5}.jpg");
                if (File.Exists(outFile))
                {
                    AddThumbnail(outFile, i);
                    displayedSet.Add(i);
                }
                else
                {
                    break; // stop at first gap to keep order
                }
            }
            displayed = displayedSet.Count;
            var elapsed = (DateTime.Now - startTime).TotalSeconds;
            var done = completed + failed;
            StatusText.Text = $"Generating — {displayed}/{totalCount} thumbnails  ({done}/{needed.Count} ffmpeg done, {elapsed:F0}s)";

            if (tasks.All(t => t.IsCompleted)) break;
            await Task.Delay(500);
        }

        try { await Task.WhenAll(tasks); } catch { }
        LogTimeline($"  completed={completed} failed={failed} total_time={(DateTime.Now - startTime).TotalSeconds:F1}s");

        // Pickup any stragglers
        for (int i = 0; i < totalCount; i++)
        {
            if (displayedSet.Contains(i)) continue;
            var outFile = Path.Combine(cacheDir, $"thumb_{i:D5}.jpg");
            if (File.Exists(outFile))
            {
                AddThumbnail(outFile, i);
                displayedSet.Add(i);
            }
        }

        if (version != _generationVersion) return;

        _loadedCount = displayed;
        StatusText.Text = $"{displayed}/{totalCount} thumbnails ({completed} generated, {failed} failed) — click to jump";
        LogTimeline($"Local generation complete: displayed={displayed}");
    }

    private async Task AutoRefreshUntilComplete(int version, int interval)
    {
        while (version == _generationVersion)
        {
            await Task.Delay(5000);
            if (version != _generationVersion) return;

            try
            {
                var url = $"{_apiBaseUrl}/thumbnails?path={Uri.EscapeDataString(_remotePath)}&interval={interval}&width=240&height=135";
                var json = await _httpClient.GetStringAsync(url);

                List<ThumbnailApiInfo>? thumbInfos = null;
                string? genStatus = null;

                try
                {
                    thumbInfos = System.Text.Json.JsonSerializer.Deserialize<List<ThumbnailApiInfo>>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    var resp = System.Text.Json.JsonSerializer.Deserialize<ThumbnailApiResponse>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    thumbInfos = resp?.Thumbnails;
                    genStatus = resp?.Status;
                }

                if (version != _generationVersion || thumbInfos == null) return;

                // Append only new thumbnails
                var baseUrl = _apiBaseUrl.Replace("/api/VideoArchive", "");
                var newThumbs = thumbInfos.Skip(_loadedCount).ToList();

                foreach (var thumb in newThumbs)
                {
                    var ts = TimeSpan.FromSeconds(thumb.Timestamp);
                    var panel = new StackPanel { Margin = new Thickness(4) };
                    var img = new Image
                    {
                        Width = _thumbWidth,
                        Height = _thumbHeight,
                        Stretch = Stretch.Uniform,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                    };

                    try
                    {
                        var imgData = await _httpClient.GetByteArrayAsync(baseUrl + thumb.Url);
                        using var ms = new MemoryStream(imgData);
                        img.Source = new Avalonia.Media.Imaging.Bitmap(ms);
                    }
                    catch { }

                    var label = new TextBlock
                    {
                        Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}",
                        Foreground = Brushes.GreenYellow,
                        FontSize = 12,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 2, 0, 0)
                    };

                    double seekTo = thumb.Timestamp;
                    img.PointerPressed += (_, _) => _mainWindow.JumpFromTimeline(seekTo);

                    panel.Children.Add(img);
                    panel.Children.Add(label);
                    ThumbnailPanel.Children.Add(panel);
                }

                _loadedCount = thumbInfos.Count;

                if (genStatus == "generating")
                {
                    StatusText.Text = $"{thumbInfos.Count} thumbnails (generating... auto-refreshing)";
                }
                else
                {
                    StatusText.Text = $"{thumbInfos.Count} thumbnails — click to jump";
                    return; // Done, stop refreshing
                }
            }
            catch
            {
                return; // Stop on error
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _httpClient.Dispose();
        base.OnClosing(e);
    }
}
