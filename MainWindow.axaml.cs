using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace MultiPlayerAll;

public partial class MainWindow : Window
{
    private const int MaxWindows = 9;

    private readonly MpvVideoView[] videoViews = new MpvVideoView[MaxWindows];
    private readonly MpvPlayer?[] players = new MpvPlayer?[MaxWindows];
    private readonly TextBlock[] timeLabels = new TextBlock[MaxWindows];
    private readonly Control[] videoPanels = new Control[MaxWindows];
    private readonly SemaphoreSlim loadSemaphore = new(1, 1);

    private Grid? activeVideoGrid;
    private DispatcherTimer? timer;
    private double totalDurationSec;
    private int numWindows = 4;
    private string selectedVideoFile = string.Empty;
    private string selectedRemotePath = string.Empty; // original UNC/remote path
    private double _pendingSeekTime; // set by URL handler, applied after load
    private bool isVideoLoaded;
    private int expandedIndex = -1;
    private int currentWindowIndex;
    private bool isMuted = true;
    private bool isScrubbing;
    private bool isUpdatingSliderFromPlayback;
    private int currentVolume = 100;
    private readonly DateTime[] lastLeftClickTimes = new DateTime[MaxWindows];
    private int loadVersion;
    private double[] startPositionsSec = new double[MaxWindows];

    // Download
    private CancellationTokenSource? downloadCts;
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "MultiPlayerAll");

    // API
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string apiBaseUrl = "http://rmansava.mynetgear.com:9191/api/VideoArchive";

    // User preferences
    private static readonly string PrefsFile = Path.Combine(Path.GetTempPath(), "MultiPlayerAll", "prefs.json");

    private void SavePrefs()
    {
        try
        {
            var prefs = new Dictionary<string, string>
            {
                ["windows"] = numWindows.ToString(),
                ["mode"] = (StreamRadio?.IsChecked == true) ? "stream" : "download"
            };
            File.WriteAllText(PrefsFile, System.Text.Json.JsonSerializer.Serialize(prefs));
        }
        catch { }
    }

    private void LoadPrefs()
    {
        try
        {
            if (!File.Exists(PrefsFile)) return;
            var prefs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PrefsFile));
            if (prefs == null) return;

            if (prefs.TryGetValue("windows", out var w) && int.TryParse(w, out var wn))
            {
                var idx = wn switch { 1 => 0, 2 => 1, 4 => 2, 9 => 3, _ => 2 };
                NumWindowsComboBox.SelectedIndex = idx;
            }
            if (prefs.TryGetValue("mode", out var m))
            {
                if (m == "stream") StreamRadio.IsChecked = true;
                else DownloadRadio.IsChecked = true;
            }
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += Timer_Tick;
        timer.Start();

        MuteButton.Content = "UnMute";
        UpdateVolumeLabel();
        NumWindowsComboBox.SelectedIndex = 0; // default 1 window

        VideoDataGrid.DoubleTapped += VideoDataGrid_DoubleTapped;
        PositionSlider.AddHandler(InputElement.PointerPressedEvent, PositionSlider_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        PositionSlider.AddHandler(InputElement.PointerReleasedEvent, PositionSlider_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        PositionSlider.AddHandler(InputElement.PointerCaptureLostEvent, PositionSlider_PointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        Directory.CreateDirectory(CacheDir);
        LoadPrefs();

        // Save prefs when mode changes
        StreamRadio.Checked += (_, _) => SavePrefs();

        // When switching from Stream to Download while playing, re-download
        DownloadRadio.Checked += async (s, e) =>
        {
            SavePrefs();
            if (isVideoLoaded && selectedVideoFile.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(selectedRemotePath))
            {
                // Currently streaming — stop and download
                var currentPos = players[currentWindowIndex]?.Position ?? 0;
                var remotePath = selectedRemotePath;

                // Stop current playback
                for (int i = 0; i < MaxWindows; i++)
                    videoViews[i]?.DetachPlayer();
                DisposePlayers();
                isVideoLoaded = false;

                // Force download path
                await DownloadAndPlay(remotePath);
            }
        };

        TestApiConnection();

        // Handle command-line args: --path "..." --time 123
        ProcessCommandLineArgs();
    }

    private void SetStatus(string text, string color = "Gray")
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            StatusLabel.Text = text;
            StatusLabel.Foreground = color switch
            {
                "Green" => Brushes.GreenYellow,
                "Yellow" => Brushes.Yellow,
                "Red" => Brushes.OrangeRed,
                "Blue" => Brushes.DodgerBlue,
                _ => Brushes.Gray
            };

            // Show/hide the big loading overlay
            bool isLoading = color == "Blue" || color == "Yellow";
            LoadingOverlay.IsVisible = isLoading;
            if (isLoading)
            {
                LoadingStatusText.Text = text;
                LoadingDetailText.Text = "";
            }
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(text, color));
        }
    }

    private async void ProcessCommandLineArgs()
    {
        var args = Environment.GetCommandLineArgs();
        File.AppendAllText(Path.Combine(CacheDir, "crash.log"),
            $"{DateTime.Now} Args: [{string.Join("] [", args)}]\n");

        string? path = null;
        string? title = null;
        double time = 0;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--path" && i + 1 < args.Length)
                path = args[++i];
            else if (args[i] == "--title" && i + 1 < args.Length)
                title = args[++i];
            else if (args[i] == "--time" && i + 1 < args.Length)
                double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out time);
            else if (args[i].StartsWith("multiplayer://"))
            {
                // Parse multiplayer://play?title=...&path=...&time=...
                try
                {
                    var uri = new Uri(args[i]);
                    var queryParts = uri.Query.TrimStart('?').Split('&');
                    foreach (var part in queryParts)
                    {
                        var kv = part.Split('=', 2);
                        if (kv.Length == 2)
                        {
                            var key = Uri.UnescapeDataString(kv[0]);
                            var val = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                            if (key == "path") path = val;
                            else if (key == "title") title = val;
                            else if (key == "time")
                                double.TryParse(val, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out time);
                        }
                    }
                }
                catch { }
            }
        }

        _pendingSeekTime = time;

        if (!string.IsNullOrEmpty(path))
        {
            // Fix UNC paths that lost a leading backslash during URL encoding
            if (path.StartsWith("\\") && !path.StartsWith("\\\\"))
                path = "\\" + path;

            File.AppendAllText(Path.Combine(CacheDir, "crash.log"),
                $"{DateTime.Now} Resolved: path=[{path}] title=[{title}] time={time}\n");

            await Task.Delay(500);
            selectedRemotePath = path;

            // Show filename in search bar for context
            var displayName = Path.GetFileNameWithoutExtension(path);
            SearchTextBox.Text = displayName;

            await DownloadAndPlay(path);
        }
        else if (!string.IsNullOrEmpty(title))
        {
            await Task.Delay(500);
            // Search by title — user picks from results, pending seek applied after load
            SearchTextBox.Text = title;
            SearchButton_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        }
    }

    private void RegisterUrlHandler_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MultiPlayerAll.exe");

                // Write registry entries for multiplayer:// protocol
                var commands = new[]
                {
                    $"reg add HKCU\\Software\\Classes\\multiplayer /ve /d \"URL:MultiPlayerAll Protocol\" /f",
                    $"reg add HKCU\\Software\\Classes\\multiplayer /v \"URL Protocol\" /d \"\" /f",
                    $"reg add HKCU\\Software\\Classes\\multiplayer\\shell\\open\\command /ve /d \"\\\"{exePath}\\\" \\\"%1\\\"\" /f"
                };

                foreach (var cmd in commands)
                {
                    var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {cmd}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    proc?.WaitForExit(3000);
                }

                UrlStatusLabel.Text = "URL Handler Registered";
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS handles this via Info.plist CFBundleURLTypes (already set in .app bundle)
                UrlStatusLabel.Text = "Registered (via .app bundle)";
            }
            else
            {
                // Linux: create .desktop file
                var desktopEntry = $"""
                    [Desktop Entry]
                    Type=Application
                    Name=MultiPlayerAll
                    Exec={Environment.ProcessPath ?? "MultiPlayerAll"} %u
                    MimeType=x-scheme-handler/multiplayer;
                    NoDisplay=true
                    """;
                var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "applications", "multiplayer-handler.desktop");
                Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);
                File.WriteAllText(desktopPath, desktopEntry);

                System.Diagnostics.Process.Start("xdg-mime",
                    "default multiplayer-handler.desktop x-scheme-handler/multiplayer")?.WaitForExit(3000);

                UrlStatusLabel.Text = "URL Handler Registered";
            }
        }
        catch (Exception ex)
        {
            UrlStatusLabel.Text = $"Failed: {ex.Message}";
            UrlStatusLabel.Foreground = Brushes.OrangeRed;
        }
    }


    private async void TestApiConnection()
    {
        try { await httpClient.GetAsync($"{apiBaseUrl}/search?videoName=test"); }
        catch { }
    }

    // ── Download & Play ─────────────────────────────────────────────────

    private async Task DownloadAndPlay(string remotePath)
    {
        selectedRemotePath = remotePath;
        SetStatus($"Loading: {Path.GetFileName(remotePath)}", "Blue");
        LoadingDetailText.Text = remotePath;
        StatusLabel.Text = remotePath;

        // If file is on a local drive (not UNC/network), play directly
        if (!remotePath.StartsWith("\\\\") && File.Exists(remotePath))
        {
            selectedVideoFile = remotePath;
            await LoadVideoAsync(remotePath);
            return;
        }

        // Check local cache (use hash prefix to avoid filename collisions across paths)
        var fileName = Path.GetFileName(remotePath);
        var pathHash = remotePath.GetHashCode().ToString("X8");
        var localPath = Path.Combine(CacheDir, $"{pathHash}_{SanitizeFileName(fileName)}");

        // Stream mode: play directly from HTTP URL (instant start)
        bool useStream = StreamRadio?.IsChecked == true;
        if (useStream && !File.Exists(localPath))
        {
            SetStatus("Streaming...", "Blue");
            var streamUrl = $"{apiBaseUrl}/stream?path={Uri.EscapeDataString(remotePath)}";
            selectedVideoFile = streamUrl;
            await LoadVideoAsync(streamUrl);
            return;
        }

        if (File.Exists(localPath))
        {
            SetStatus("Playing from cache", "Green");
            selectedVideoFile = localPath;
            await LoadVideoAsync(localPath);
            return;
        }

        downloadCts = new CancellationTokenSource();
        DownloadOverlay.IsVisible = true;
        DownloadStatusText.Text = $"Downloading: {fileName}";
        DownloadProgressBar.Value = 0;
        DownloadDetailText.Text = "Connecting...";

        try
        {
            long totalBytes = 0;
            try
            {
                var sizeUrl = $"{apiBaseUrl}/filesize?path={Uri.EscapeDataString(remotePath)}";
                var sizeInfo = await httpClient.GetFromJsonAsync<FileSizeInfo>(sizeUrl, downloadCts.Token);
                if (sizeInfo != null) totalBytes = sizeInfo.Size;
            }
            catch { }

            var streamUrl = $"{apiBaseUrl}/stream?path={Uri.EscapeDataString(remotePath)}";
            using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await downloadClient.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token);
            response.EnsureSuccessStatusCode();

            if (totalBytes == 0 && response.Content.Headers.ContentLength.HasValue)
                totalBytes = response.Content.Headers.ContentLength.Value;

            var tempPath = localPath + ".tmp";
            long downloaded = 0;
            var sw = Stopwatch.StartNew();

            await using (var contentStream = await response.Content.ReadAsStreamAsync(downloadCts.Token))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, downloadCts.Token)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, downloadCts.Token);
                    downloaded += bytesRead;
                    var dl = downloaded;
                    var elapsed = sw.Elapsed;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (totalBytes > 0)
                        {
                            var pct = (double)dl / totalBytes * 100;
                            DownloadProgressBar.Value = pct;
                            var speedMbps = dl / elapsed.TotalSeconds / 1024 / 1024;
                            var remaining = TimeSpan.FromSeconds((totalBytes - dl) / (dl / elapsed.TotalSeconds));
                            DownloadDetailText.Text = $"{dl / 1024 / 1024} MB / {totalBytes / 1024 / 1024} MB  —  {speedMbps:F1} MB/s  —  {remaining:mm\\:ss} remaining";
                        }
                        else
                        {
                            DownloadDetailText.Text = $"{dl / 1024 / 1024} MB downloaded";
                        }
                    });
                }
            }

            File.Move(tempPath, localPath, overwrite: true);
            DownloadOverlay.IsVisible = false;
            selectedVideoFile = localPath;
            await LoadVideoAsync(localPath);
        }
        catch (OperationCanceledException)
        {
            DownloadOverlay.IsVisible = false;
            var tempPath = localPath + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            DownloadOverlay.IsVisible = false;
            DownloadDetailText.Text = $"Download failed: {ex.Message}";
        }
    }

    private void CancelDownloadButton_Click(object? sender, RoutedEventArgs e) => downloadCts?.Cancel();

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── Timer ──────────────────────────────────────────────────────────

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!isVideoLoaded || players[0] == null) return;

        var pos = players[0].Position;
        if (pos < 0) return;

        var segmentOffset = pos - startPositionsSec[0];
        if (!isScrubbing && segmentOffset >= 0)
        {
            isUpdatingSliderFromPlayback = true;
            PositionSlider.Value = segmentOffset;
            isUpdatingSliderFromPlayback = false;
        }

        for (int i = 0; i < numWindows; i++)
        {
            if (players[i] != null && timeLabels[i] != null)
            {
                var p = players[i]!.Position;
                if (p >= 0)
                {
                    var ts = TimeSpan.FromSeconds(p);
                    timeLabels[i].Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
                }
            }
        }
    }

    // ── Open / Load Video ──────────────────────────────────────────────

    private async void ChooseFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Video File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video Files")
                {
                    Patterns = new[] { "*.mp4", "*.avi", "*.mkv", "*.webm", "*.mov", "*.flv", "*.wmv", "*.m4v", "*.mpg", "*.mpeg" }
                }
            }
        });

        if (files.Count > 0)
        {
            selectedVideoFile = files[0].Path.LocalPath;
            await LoadVideoAsync(selectedVideoFile);
        }
    }

    private void OpenFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = GetExternalOpenPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    private async Task LoadVideoAsync(string videoPath)
    {
        await loadSemaphore.WaitAsync();
        var currentLoadVersion = Interlocked.Increment(ref loadVersion);

        try
        {
            isVideoLoaded = false;
            // timeline reset not needed — browser handles it
            // timeline strip removed — Timeline Browser handles this now
            selectedVideoFile = videoPath;
            currentWindowIndex = 0;
            expandedIndex = -1;

            EnsureVideoGrid();
            EnsureVideoPanels();

            // Detach views before disposing players to avoid stale render contexts
            for (int i = 0; i < MaxWindows; i++)
                videoViews[i]?.DetachPlayer();
            DisposePlayers();

            SetStatus("Loading video...", "Yellow");

            RefreshVideoLayout();

            // Create players and attach to GL views (creates render context)
            for (int i = 0; i < numWindows; i++)
            {
                var player = new MpvPlayer();
                player.SetOption("vo", "libmpv");
                player.SetOption("keep-open", "yes");
                player.SetOption("hr-seek", "yes");
                player.SetOption("osc", "no");
                player.SetOption("input-default-bindings", "no");
                player.SetOption("input-vo-keyboard", "no");
                player.Initialize();

                player.Volume = (i == currentWindowIndex && !isMuted) ? currentVolume : 0;
                players[i] = player;

                // Attach to GL view — render context created on next OnOpenGlRender
                videoViews[i]?.AttachPlayer(player);
            }

            // Wait for render contexts to be created (needs GL render cycle)
            await Task.Delay(200);
            // Force a render cycle
            for (int i = 0; i < numWindows; i++)
                videoViews[i]?.RequestNextFrameRendering();
            await Task.Delay(300);
            if (currentLoadVersion != loadVersion) return;

            // Load file into first player and get duration from it (no separate probe)
            File.AppendAllText(Path.Combine(CacheDir, "crash.log"), $"{DateTime.Now} Loading video: {videoPath}\n");
            for (int i = 0; i < numWindows; i++)
                players[i]?.LoadFile(videoPath);

            // Wait for duration from the actual player
            int maxWait = videoPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? 300 : 50;
            for (int attempt = 0; attempt < maxWait; attempt++)
            {
                await Task.Delay(100);
                if (players[0] != null)
                {
                    totalDurationSec = players[0]!.Duration;
                    if (totalDurationSec > 0) break;
                }
                if (attempt > 0 && attempt % 20 == 0)
                    File.AppendAllText(Path.Combine(CacheDir, "crash.log"),
                        $"{DateTime.Now} Waiting for duration: attempt {attempt}\n");
            }

            File.AppendAllText(Path.Combine(CacheDir, "crash.log"), $"{DateTime.Now} Duration={totalDurationSec}\n");
            if (currentLoadVersion != loadVersion) return;
            if (totalDurationSec <= 0)
            {
                SetStatus("Failed to load video", "Red");
                File.AppendAllText(Path.Combine(CacheDir, "crash.log"), $"{DateTime.Now} Duration is 0, aborting\n");
                return;
            }

            var segmentDurationSec = totalDurationSec / numWindows;
            CalculateStartPositions(segmentDurationSec);
            PositionSlider.Maximum = segmentDurationSec;
            PositionSlider.Value = 0;

            // Wait for players to start decoding
            await Task.Delay(500);
            if (currentLoadVersion != loadVersion) return;

            // Seek each player to its segment start position with retry
            for (int attempt = 0; attempt < 5; attempt++)
            {
                bool allGood = true;
                for (int i = 0; i < numWindows; i++)
                {
                    if (players[i] == null) continue;
                    var target = startPositionsSec[i];
                    var current = players[i]!.Position;

                    // Only seek if not already close to target
                    if (Math.Abs(current - target) > 2.0)
                    {
                        players[i]!.Command("seek", target.ToString("F1"), "absolute", "exact");
                        allGood = false;
                    }
                }

                if (allGood) break;
                await Task.Delay(500);
                if (currentLoadVersion != loadVersion) return;
            }

            // Log final positions
            for (int i = 0; i < numWindows; i++)
            {
                if (players[i] == null) continue;
                File.AppendAllText(Path.Combine(CacheDir, "crash.log"),
                    $"{DateTime.Now} Player {i} final pos={players[i]!.Position:F1} target={startPositionsSec[i]:F1}\n");
            }

            await Task.Delay(500);
            if (currentLoadVersion != loadVersion) return;

            isVideoLoaded = true;
            SetCurrentWindow(0);
            ApplyAudioRouting();
            PlayPauseButton.Content = "Play/Pause";
            var duration = TimeSpan.FromSeconds(totalDurationSec);
            var displayPath = selectedRemotePath ?? selectedVideoFile ?? "";
            SetStatus($"Playing — {duration:hh\\:mm\\:ss}  |  {displayPath}", "Green");

            // Apply pending seek from URL handler
            if (_pendingSeekTime > 0)
            {
                await Task.Delay(500);
                JumpToTimeline(_pendingSeekTime);
                _pendingSeekTime = 0;
            }

            // Pre-generate thumbnails in the background so they're ready when user clicks Timeline
            _ = PreGenerateThumbnails();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", "Red");
            File.AppendAllText(Path.Combine(CacheDir, "crash.log"), $"{DateTime.Now} LoadVideo: {ex}\n\n");
        }
        finally
        {
            loadSemaphore.Release();
        }
    }

    private async Task<double> ProbeDuration(string videoPath)
    {
        return await Task.Run(() =>
        {
            var probe = new MpvPlayer();
            try
            {
                probe.SetOption("vo", "null");
                probe.SetOption("ao", "null");
                probe.SetOption("ytdl", "no");
                probe.Initialize();
                probe.LoadFile(videoPath);

                // Wait for duration to be available (up to 30s for HTTP streams)
                int maxAttempts = videoPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? 300 : 50;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    Thread.Sleep(100);
                    var dur = probe.Duration;
                    if (dur > 0) return dur;

                    // Log progress every 2 seconds
                    if (attempt > 0 && attempt % 20 == 0)
                    {
                        var idle = probe.GetPropertyString("idle-active");
                        var path = probe.GetPropertyString("path");
                        File.AppendAllText(Path.Combine(CacheDir, "crash.log"),
                            $"{DateTime.Now} Probe attempt {attempt}: dur={dur} idle={idle} path={path}\n");
                    }
                }
                return 0;
            }
            finally
            {
                probe.Dispose();
            }
        });
    }

    private void CalculateStartPositions(double segmentDurationSec)
    {
        for (int i = 0; i < MaxWindows; i++)
            startPositionsSec[i] = 0;
        for (int i = 0; i < numWindows; i++)
            startPositionsSec[i] = i * segmentDurationSec;
    }

    private void EnsureVideoGrid()
    {
        if (activeVideoGrid != null) return;
        activeVideoGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };
        VideoContainer.Children.Insert(0, activeVideoGrid);
    }

    private void EnsureVideoPanels()
    {
        if (activeVideoGrid == null) return;

        for (int i = 0; i < MaxWindows; i++)
        {
            if (videoPanels[i] != null) continue;

            var timeLabel = new TextBlock
            {
                Text = "00:00:00",
                FontSize = 16,
                Foreground = Brushes.GreenYellow,
                Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                Padding = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            timeLabels[i] = timeLabel;

            var view = new MpvVideoView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            videoViews[i] = view;

            int idx = i;

            // Overlay sits on top of GL view — timestamps + click events
            var overlay = new Panel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent
            };
            overlay.Children.Add(timeLabel);

            overlay.PointerPressed += (s, e) =>
            {
                var props = e.GetCurrentPoint(overlay).Properties;
                if (props.IsLeftButtonPressed)
                {
                    SetCurrentWindow(idx);
                    var now = DateTime.UtcNow;
                    if ((now - lastLeftClickTimes[idx]).TotalMilliseconds <= 450)
                    {
                        ToggleExpandedWindow(idx);
                        e.Handled = true;
                    }
                    lastLeftClickTimes[idx] = now;
                }
                else if (props.IsRightButtonPressed)
                {
                    if (players[0] != null && !players[0].IsPaused) PauseAll(); else PlayAll();
                    e.Handled = true;
                }
            };

            var panel = new Panel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ClipToBounds = true
            };
            panel.Children.Add(view);    // GL view at bottom
            panel.Children.Add(overlay); // overlay on top

            videoPanels[i] = panel;
            activeVideoGrid.Children.Add(panel);
        }
    }

    private void DisposePlayers()
    {
        for (int i = 0; i < MaxWindows; i++)
        {
            if (players[i] != null)
            {
                players[i]!.Stop();
                players[i]!.Dispose();
                players[i] = null;
            }
        }

    }

    // ── Window Selection ───────────────────────────────────────────────

    private void SetCurrentWindow(int index)
    {
        if (index < 0 || index >= numWindows) return;
        currentWindowIndex = index;

        for (int i = 0; i < numWindows; i++)
            if (timeLabels[i] != null)
                timeLabels[i].Foreground = Brushes.GreenYellow;

        if (timeLabels[index] != null)
            timeLabels[index].Foreground = Brushes.Aquamarine;

        ApplyAudioRouting();
    }

    private void ApplyAudioRouting()
    {
        for (int i = 0; i < numWindows; i++)
        {
            if (players[i] == null) continue;

            if (i == currentWindowIndex && !isMuted)
                players[i]!.Volume = currentVolume;
            else
                players[i]!.Volume = 0;
        }
    }

    private void ToggleExpandedWindow(int index)
    {
        if (index < 0 || index >= numWindows) return;
        expandedIndex = expandedIndex == index ? -1 : index;
        RefreshVideoLayout();
    }

    private void RefreshVideoLayout()
    {
        if (activeVideoGrid == null) return;

        activeVideoGrid.RowDefinitions.Clear();
        activeVideoGrid.ColumnDefinitions.Clear();

        bool isExpanded = expandedIndex >= 0 && expandedIndex < numWindows;
        int columns = isExpanded ? 1 : (int)Math.Ceiling(Math.Sqrt(numWindows));
        int rows = isExpanded ? 1 : (int)Math.Ceiling((double)numWindows / columns);

        for (int r = 0; r < rows; r++)
            activeVideoGrid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        for (int c = 0; c < columns; c++)
            activeVideoGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        int rowCount = Math.Max(1, activeVideoGrid.RowDefinitions.Count);
        int columnCount = Math.Max(1, activeVideoGrid.ColumnDefinitions.Count);

        for (int i = 0; i < MaxWindows; i++)
        {
            if (videoPanels[i] == null) continue;

            bool isVisible = i < numWindows;
            bool isExpandedCell = isExpanded && isVisible;
            videoPanels[i].IsVisible = isVisible && (!isExpandedCell || i == expandedIndex);
            Grid.SetRowSpan(videoPanels[i], 1);
            Grid.SetColumnSpan(videoPanels[i], 1);

            if (!videoPanels[i].IsVisible)
            {
                Grid.SetRow(videoPanels[i], 0);
                Grid.SetColumn(videoPanels[i], 0);
                continue;
            }

            if (isExpanded && i == expandedIndex)
            {
                Grid.SetRow(videoPanels[i], 0);
                Grid.SetColumn(videoPanels[i], 0);
                Grid.SetRowSpan(videoPanels[i], rowCount);
                Grid.SetColumnSpan(videoPanels[i], columnCount);
            }
            else
            {
                Grid.SetRow(videoPanels[i], i / columns);
                Grid.SetColumn(videoPanels[i], i % columns);
            }
        }
    }

    // ── Playback Controls ──────────────────────────────────────────────

    private async void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!isVideoLoaded || players[0] == null)
        {
            if (VideoDataGrid.SelectedItem is VideoInfo selected && !string.IsNullOrEmpty(selected.FullPath))
            {
                await DownloadAndPlay(selected.FullPath);
                return;
            }
            return;
        }

        if (!players[0]!.IsPaused)
            PauseAll();
        else
            PlayAll();
    }

    private void PlayAll()
    {
        for (int i = 0; i < numWindows; i++)
            players[i]?.Resume();
        ApplyAudioRouting();
    }

    private void PauseAll()
    {
        for (int i = 0; i < numWindows; i++)
            players[i]?.Pause();
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < numWindows; i++)
            players[i]?.Stop();
    }

    private void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        if (currentWindowIndex < 0 || currentWindowIndex >= numWindows) return;
        if (players[currentWindowIndex] == null) return;

        var syncPos = players[currentWindowIndex]!.Position;
        for (int i = 0; i < numWindows; i++)
        {
            if (players[i] == null || i == currentWindowIndex) continue;
            players[i]!.Seek(syncPos);
        }
    }

    // ── Frame It ─────────────────────────────────────────────────────

    private void FrameItButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!isVideoLoaded || string.IsNullOrEmpty(selectedVideoFile)) return;

        var currentPos = players[currentWindowIndex]?.Position ?? 0;
        var frameWindow = new FrameIt(selectedVideoFile, totalDurationSec, currentPos);
        frameWindow.Show();
    }

    private async void ScreenshotButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!isVideoLoaded || players[currentWindowIndex] == null) return;

        try
        {
            var ts = TimeSpan.FromSeconds(players[currentWindowIndex]!.Position);
            var screenshotPath = Path.Combine(CacheDir, $"screenshot_{ts:hh\\-mm\\-ss}.png");

            // mpv's screenshot-to-file renders the current frame to a file
            players[currentWindowIndex]!.Command("screenshot-to-file", screenshotPath, "video");

            await Task.Delay(300);

            if (File.Exists(screenshotPath))
            {
                // On Windows, copy image to clipboard via powershell
                if (OperatingSystem.IsWindows())
                {
                    var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-Command \"Add-Type -Assembly System.Windows.Forms; [System.Windows.Forms.Clipboard]::SetImage([System.Drawing.Image]::FromFile('{screenshotPath.Replace("'", "''")}'))\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    ps?.WaitForExit(3000);
                    DownloadDetailText.Text = $"Screenshot copied to clipboard: {ts:hh\\:mm\\:ss}";
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // Mac: copy image to clipboard via osascript
                    var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "osascript",
                        Arguments = $"-e \"set the clipboard to (read (POSIX file \\\"{screenshotPath}\\\") as TIFF picture)\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    ps?.WaitForExit(3000);
                    DownloadDetailText.Text = $"Screenshot copied to clipboard: {ts:hh\\:mm\\:ss}";
                }
                else
                {
                    // Linux: copy image to clipboard via xclip
                    var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xclip",
                        Arguments = $"-selection clipboard -t image/png -i \"{screenshotPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    ps?.WaitForExit(3000);
                    DownloadDetailText.Text = ps?.ExitCode == 0
                        ? $"Screenshot copied to clipboard: {ts:hh\\:mm\\:ss}"
                        : $"Screenshot saved: {screenshotPath} (install xclip for clipboard support)";
                }
            }
            else
            {
                DownloadDetailText.Text = "Screenshot failed — file not created";
            }
        }
        catch (Exception ex)
        {
            DownloadDetailText.Text = $"Screenshot failed: {ex.Message}";
        }
    }

    // ── Enhance ─────────────────────────────────────────────────────────

    private async void EnhanceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!isVideoLoaded || players[currentWindowIndex] == null) return;

        try
        {
            var ts = TimeSpan.FromSeconds(players[currentWindowIndex]!.Position);
            var screenshotPath = Path.Combine(CacheDir, $"enhance_{ts:hh\\-mm\\-ss}.png");

            players[currentWindowIndex]!.Command("screenshot-to-file", screenshotPath, "video");
            await Task.Delay(300);

            if (File.Exists(screenshotPath))
            {
                var window = new EnhanceWindow(screenshotPath, players[currentWindowIndex]!.Position);
                window.Show();
            }
            else
            {
                DownloadDetailText.Text = "Enhance failed — could not capture frame";
            }
        }
        catch (Exception ex)
        {
            DownloadDetailText.Text = $"Enhance failed: {ex.Message}";
        }
    }

    // ── Timeline ────────────────────────────────────────────────────────

    private const int TimelineThumbnailCount = 40;
    private void TimelineButton_Click(object? sender, RoutedEventArgs e)
    {
        OpenTimelineBrowser();
    }

    private async Task PreGenerateThumbnails()
    {
        try
        {
            var remotePath = !string.IsNullOrEmpty(selectedRemotePath) ? selectedRemotePath : selectedVideoFile;
            if (string.IsNullOrEmpty(remotePath)) return;

            // Fire-and-forget request to server — warms cache at 5s intervals, 240x135 matches Timeline Browser default
            var url = $"{apiBaseUrl}/thumbnails?path={Uri.EscapeDataString(remotePath)}&interval=5&width=240&height=135";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await client.GetAsync(url);
        }
        catch
        {
            // Silent — this is just pre-caching
        }
    }

    public void JumpFromTimeline(double seconds) => JumpToTimeline(seconds);

    private async void JumpToTimeline(double seconds)
    {
        if (!isVideoLoaded || players[0] == null) return;

        // Re-split from this point: each window covers an equal segment from here to the end
        var remaining = totalDurationSec - seconds;
        if (remaining <= 0) return;

        var segmentDuration = remaining / numWindows;
        for (int i = 0; i < numWindows; i++)
            startPositionsSec[i] = seconds + (i * segmentDuration);

        PositionSlider.Maximum = segmentDuration;
        PositionSlider.Value = 0;

        // Pause all during seek to prevent flickering
        PauseAll();
        await Task.Delay(100);

        // Seek all players
        for (int i = 0; i < numWindows; i++)
        {
            if (players[i] == null) continue;
            players[i]!.Command("seek", startPositionsSec[i].ToString("F1", System.Globalization.CultureInfo.InvariantCulture), "absolute", "exact");
        }

        // Wait for seeks to settle
        await Task.Delay(500);

        // Update labels
        for (int i = 0; i < numWindows; i++)
        {
            if (timeLabels[i] != null)
            {
                var ts = TimeSpan.FromSeconds(startPositionsSec[i]);
                timeLabels[i].Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }
        }

        // Resume playback
        PlayAll();
    }

    private void OpenTimelineBrowser()
    {
        if (!isVideoLoaded) return;
        var remotePath = !string.IsNullOrEmpty(selectedRemotePath) ? selectedRemotePath : selectedVideoFile;
        var browser = new TimelineBrowser(remotePath, totalDurationSec, this, apiBaseUrl);
        browser.Show();
    }

    // ── Speed ──────────────────────────────────────────────────────────

    private void SpeedDownButton_Click(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < numWindows; i++)
            if (players[i] != null)
                players[i]!.Speed = Math.Max(0.1, players[i]!.Speed - 0.5);
        UpdateSpeedLabel();
    }

    private void SpeedUpButton_Click(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < numWindows; i++)
            if (players[i] != null)
                players[i]!.Speed = players[i]!.Speed + 0.5;
        UpdateSpeedLabel();
    }

    private void UpdateSpeedLabel()
    {
        if (players[0] != null)
            SpeedLabel.Text = $"{players[0]!.Speed:F1}x";
    }

    // ── Mute ───────────────────────────────────────────────────────────

    private void MuteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (isMuted)
        {
            isMuted = false;
            MuteButton.Content = "Mute";
        }
        else
        {
            isMuted = true;
            MuteButton.Content = "UnMute";
        }
        ApplyAudioRouting();
    }

    private void VolumeDownButton_Click(object? sender, RoutedEventArgs e) => SetVolume(currentVolume - 10);
    private void VolumeUpButton_Click(object? sender, RoutedEventArgs e) => SetVolume(currentVolume + 10);

    private void SetVolume(int volume)
    {
        currentVolume = Math.Clamp(volume, 0, 200);
        UpdateVolumeLabel();
        ApplyAudioRouting();
    }

    private void UpdateVolumeLabel()
    {
        VolumeLabel.Text = currentVolume.ToString();
    }

    // ── Skip ───────────────────────────────────────────────────────────

    private void SkipButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tagStr && int.TryParse(tagStr, out int seconds))
        {
            for (int i = 0; i < numWindows; i++)
            {
                if (players[i] != null)
                {
                    var newPos = players[i]!.Position + seconds;
                    if (newPos < 0) newPos = 0;
                    if (newPos > totalDurationSec) newPos = totalDurationSec;
                    players[i]!.Seek(newPos);
                }
            }
        }
    }

    // ── Slider ─────────────────────────────────────────────────────────

    private void PositionSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!isVideoLoaded || players[0] == null) return;
        if (isUpdatingSliderFromPlayback) return;
        ApplySliderSeek();
    }

    private void PositionSlider_PointerPressed(object? sender, PointerPressedEventArgs e) { if (isVideoLoaded) isScrubbing = true; }
    private void PositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e) { ApplySliderSeek(); isScrubbing = false; }
    private void PositionSlider_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) { ApplySliderSeek(); isScrubbing = false; }

    private void ApplySliderSeek()
    {
        if (!isVideoLoaded || players[0] == null) return;

        var sliderSec = Math.Clamp(PositionSlider.Value, 0, PositionSlider.Maximum);
        var segmentDurationSec = totalDurationSec / numWindows;

        for (int i = 0; i < numWindows; i++)
        {
            if (players[i] == null) continue;
            var seekPos = startPositionsSec[i] + sliderSec;
            players[i]!.Seek(seekPos);

            if (timeLabels[i] != null)
            {
                var ts = TimeSpan.FromSeconds(seekPos);
                timeLabels[i].Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }
        }
    }

    // ── Num Windows ────────────────────────────────────────────────────

    private async void NumWindowsComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NumWindowsComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content?.ToString(), out int n))
        {
            // Stop and detach all players before changing layout
            for (int i = 0; i < MaxWindows; i++)
                videoViews[i]?.DetachPlayer();
            DisposePlayers();
            isVideoLoaded = false;

            numWindows = n;
            SavePrefs();
            expandedIndex = -1;
            currentWindowIndex = 0;
            RefreshVideoLayout();

            if (!string.IsNullOrEmpty(selectedVideoFile))
                await LoadVideoAsync(selectedVideoFile);
        }
    }

    // ── Search / API ───────────────────────────────────────────────────

    private async void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        string query = SearchTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query)) { VideoDataGrid.ItemsSource = null; return; }

        try
        {
            string url = $"{apiBaseUrl}/quicksearch?videoName={Uri.EscapeDataString(query)}";
            var videos = await httpClient.GetFromJsonAsync<List<VideoInfo>>(url);
            VideoDataGrid.ItemsSource = videos;
        }
        catch (Exception ex)
        {
            VideoDataGrid.ItemsSource = new List<VideoInfo>
            {
                new() { FileName = $"Search error: {ex.Message}", FullPath = "" }
            };
        }
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SearchTextBox != null) SearchTextBox.Text = "";
        VideoDataGrid.ItemsSource = null;
    }

    private void SearchTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrEmpty(SearchTextBox?.Text))
            SearchButton_Click(this, new RoutedEventArgs());
    }

    private async void VideoDataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (VideoDataGrid.SelectedItem is VideoInfo selected && !string.IsNullOrEmpty(selected.FullPath))
            await DownloadAndPlay(selected.FullPath);
    }

    // ── Title Bar / Window Chrome ──────────────────────────────────────

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            WindowState = WindowState.Maximized;
            MaximizeRestoreButton.Content = "❐";
        }
        else
        {
            WindowState = WindowState.Normal;
            MaximizeRestoreButton.Content = "☐";
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Cleanup ────────────────────────────────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        timer?.Stop();
        for (int i = 0; i < MaxWindows; i++)
            videoViews[i]?.DetachPlayer();
        downloadCts?.Cancel();
        Interlocked.Increment(ref loadVersion);
        DisposePlayers();
        httpClient.Dispose();
        base.OnClosing(e);
    }

    private string GetExternalOpenPath()
    {
        if (VideoDataGrid.SelectedItem is VideoInfo selected && !string.IsNullOrWhiteSpace(selected.FullPath))
        {
            var cachedPath = Path.Combine(CacheDir, SanitizeFileName(Path.GetFileName(selected.FullPath)));
            if (File.Exists(cachedPath)) return cachedPath;
            if (File.Exists(selected.FullPath) || Directory.Exists(Path.GetDirectoryName(selected.FullPath) ?? string.Empty))
                return selected.FullPath;
        }
        if (!string.IsNullOrWhiteSpace(selectedVideoFile) && File.Exists(selectedVideoFile))
            return selectedVideoFile;
        return string.Empty;
    }
}

public class VideoInfo
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
}

public class FileSizeInfo
{
    public long Size { get; set; }
    public string FileName { get; set; } = "";
}

public class ThumbnailApiInfo
{
    public double Timestamp { get; set; }
    public string Url { get; set; } = "";
}

public class ThumbnailApiResponse
{
    public string? Status { get; set; }
    public List<ThumbnailApiInfo>? Thumbnails { get; set; }
}
