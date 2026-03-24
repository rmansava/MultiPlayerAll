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
using Avalonia.Threading;

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

    public MainWindow()
    {
        InitializeComponent();

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += Timer_Tick;
        timer.Start();

        MuteButton.Content = "UnMute";
        UpdateVolumeLabel();
        NumWindowsComboBox.SelectedIndex = 2; // default 4

        VideoDataGrid.DoubleTapped += VideoDataGrid_DoubleTapped;
        PositionSlider.AddHandler(InputElement.PointerPressedEvent, PositionSlider_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        PositionSlider.AddHandler(InputElement.PointerReleasedEvent, PositionSlider_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        PositionSlider.AddHandler(InputElement.PointerCaptureLostEvent, PositionSlider_PointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        Directory.CreateDirectory(CacheDir);
        TestApiConnection();
    }

    private async void TestApiConnection()
    {
        try { await httpClient.GetAsync($"{apiBaseUrl}/search?videoName=test"); }
        catch { }
    }

    // ── Download & Play ─────────────────────────────────────────────────

    private async Task DownloadAndPlay(string remotePath)
    {
        var fileName = Path.GetFileName(remotePath);
        var localPath = Path.Combine(CacheDir, SanitizeFileName(fileName));

        if (File.Exists(localPath))
        {
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
            selectedVideoFile = videoPath;
            currentWindowIndex = 0;
            expandedIndex = -1;

            EnsureVideoGrid();
            EnsureVideoPanels();
            DisposePlayers();

            // Probe duration with a temporary mpv instance
            File.AppendAllText(Path.Combine(CacheDir, "crash.log"), $"{DateTime.Now} Probing duration for {videoPath}\n");
            totalDurationSec = await ProbeDuration(videoPath);
            File.AppendAllText(Path.Combine(CacheDir, "crash.log"), $"{DateTime.Now} Duration={totalDurationSec}\n");
            if (currentLoadVersion != loadVersion) return;
            if (totalDurationSec <= 0)
            {
                File.AppendAllText(Path.Combine(CacheDir, "crash.log"), $"{DateTime.Now} Duration is 0, aborting\n");
                return;
            }

            var segmentDurationSec = totalDurationSec / numWindows;
            CalculateStartPositions(segmentDurationSec);
            PositionSlider.Maximum = segmentDurationSec;
            PositionSlider.Value = 0;

            RefreshVideoLayout();

            // Create players using render API (no native windows)
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
                player.LoadFile(videoPath);
                players[i] = player;

                // Attach player to GL view — it will create the render context
                videoViews[i]?.AttachPlayer(player);
            }

            // Wait for players to start
            await Task.Delay(800);
            if (currentLoadVersion != loadVersion) return;

            // Seek to start positions and pause
            for (int i = 0; i < numWindows; i++)
            {
                if (players[i] == null) continue;
                players[i]!.Pause();
                players[i]!.Seek(startPositionsSec[i]);
            }

            await Task.Delay(300);
            if (currentLoadVersion != loadVersion) return;

            // Resume playback
            for (int i = 0; i < numWindows; i++)
                players[i]?.Resume();

            isVideoLoaded = true;
            SetCurrentWindow(0);
            ApplyAudioRouting();
            PlayPauseButton.Content = "Play/Pause";
        }
        catch (Exception ex)
        {
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
                probe.Initialize();
                probe.LoadFile(videoPath);

                // Wait for duration to be available
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    Thread.Sleep(100);
                    var dur = probe.Duration;
                    if (dur > 0) return dur;
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
            numWindows = n;
            expandedIndex = -1;
            currentWindowIndex = 0;
            RefreshVideoLayout();

            if (!string.IsNullOrEmpty(selectedVideoFile) && isVideoLoaded)
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
            string url = $"{apiBaseUrl}/search?videoName={Uri.EscapeDataString(query)}";
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
