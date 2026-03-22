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
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace MultiPlayerAll;

public partial class MainWindow : Window
{
    private const int MaxWindows = 9;
    private const int AudibleVolume = 100;
    private const int SilentVolume = 0;
    private readonly VideoView[] videoViews = new VideoView[MaxWindows];
    private readonly MediaPlayer[] mediaPlayers = new MediaPlayer[MaxWindows];
    private readonly TextBlock[] timeLabels = new TextBlock[MaxWindows];
    private readonly SemaphoreSlim loadSemaphore = new(1, 1);

    private LibVLC? libVLC;
    private Grid? activeVideoGrid;
    private DispatcherTimer? timer;
    private long totalDurationMs;
    private int numWindows = 4;
    private string selectedVideoFile = string.Empty;
    private bool isVideoLoaded;
    private int expandedIndex = -1;
    private int prevNumWindows = 1;
    private int currentWindowIndex;
    private bool isMuted = true;
    private bool isScrubbing;
    private bool isUpdatingSliderFromPlayback;
    private DateTime[] lastLeftClickTimes = new DateTime[MaxWindows];
    private int loadVersion;
    private long[] startPositions = new long[MaxWindows];

    // Download
    private CancellationTokenSource? downloadCts;
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "MultiPlayerAll");

    // API configuration
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string apiBaseUrl = "http://rmansava.mynetgear.com:9191/api/VideoArchive";

    public MainWindow()
    {
        InitializeComponent();

        Core.Initialize();
        libVLC = new LibVLC();

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += Timer_Tick;
        timer.Start();

        MuteButton.Content = "UnMute";
        NumWindowsComboBox.SelectedIndex = 2; // default to 4 windows

        VideoDataGrid.DoubleTapped += VideoDataGrid_DoubleTapped;
        PositionSlider.AddHandler(InputElement.PointerPressedEvent, PositionSlider_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        PositionSlider.AddHandler(InputElement.PointerReleasedEvent, PositionSlider_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        PositionSlider.AddHandler(InputElement.PointerCaptureLostEvent, PositionSlider_PointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        // Ensure cache directory exists
        Directory.CreateDirectory(CacheDir);

        TestApiConnection();
    }

    private async void TestApiConnection()
    {
        try
        {
            var response = await httpClient.GetAsync($"{apiBaseUrl}/search?videoName=test");
        }
        catch
        {
            // API not available - user can still open local files
        }
    }

    // ── Download & Play ─────────────────────────────────────────────────

    private async Task DownloadAndPlay(string remotePath)
    {
        // Check if already cached
        var fileName = Path.GetFileName(remotePath);
        var localPath = Path.Combine(CacheDir, SanitizeFileName(fileName));

        if (File.Exists(localPath))
        {
            // Already downloaded — play directly
            selectedVideoFile = localPath;
            await LoadVideoAsync(localPath);
            return;
        }

        // Show download overlay
        downloadCts = new CancellationTokenSource();
        DownloadOverlay.IsVisible = true;
        DownloadStatusText.Text = $"Downloading: {fileName}";
        DownloadProgressBar.Value = 0;
        DownloadDetailText.Text = "Connecting...";

        try
        {
            // Get file size first
            long totalBytes = 0;
            try
            {
                var sizeUrl = $"{apiBaseUrl}/filesize?path={Uri.EscapeDataString(remotePath)}";
                var sizeInfo = await httpClient.GetFromJsonAsync<FileSizeInfo>(sizeUrl, downloadCts.Token);
                if (sizeInfo != null) totalBytes = sizeInfo.Size;
            }
            catch
            {
                // Size unknown — we'll still download, just no percentage
            }

            // Download the file
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

                    // Update progress on UI thread
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

            // Rename temp to final
            File.Move(tempPath, localPath, overwrite: true);

            // Hide overlay and play
            DownloadOverlay.IsVisible = false;
            selectedVideoFile = localPath;
            await LoadVideoAsync(localPath);
        }
        catch (OperationCanceledException)
        {
            DownloadOverlay.IsVisible = false;
            DownloadDetailText.Text = "Download cancelled.";
            // Clean up temp file
            var tempPath = localPath + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            DownloadOverlay.IsVisible = false;
            DownloadDetailText.Text = $"Download failed: {ex.Message}";
        }
    }

    private void CancelDownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        downloadCts?.Cancel();
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── Timer ──────────────────────────────────────────────────────────

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (mediaPlayers[0] == null || !mediaPlayers[0].IsPlaying) return;

        var currentMs = mediaPlayers[0].Time;
        if (!isScrubbing)
        {
            isUpdatingSliderFromPlayback = true;
            PositionSlider.Value = currentMs;
            isUpdatingSliderFromPlayback = false;
        }

        for (int i = 0; i < numWindows; i++)
        {
            if (mediaPlayers[i] != null && timeLabels[i] != null)
            {
                var ts = TimeSpan.FromMilliseconds(mediaPlayers[i].Time);
                timeLabels[i].Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
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
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DownloadDetailText.Text = $"Open failed: {ex.Message}";
        }
    }

    private async Task LoadVideoAsync(string videoPath)
    {
        if (libVLC == null) return;
        await loadSemaphore.WaitAsync();
        var currentLoadVersion = Interlocked.Increment(ref loadVersion);

        try
        {
            isVideoLoaded = false;
            currentWindowIndex = 0;
            EnsureVideoGrid();
            EnsureVideoViews();
            DisposeMediaPlayers();

            // ── Parse duration ──
            var probMedia = new Media(libVLC, videoPath, FromType.FromPath);
            await probMedia.Parse(MediaParseOptions.ParseLocal);
            if (currentLoadVersion != loadVersion)
            {
                probMedia.Dispose();
                return;
            }
            totalDurationMs = probMedia.Duration;
            probMedia.Dispose();

            if (totalDurationMs <= 0) return;

            var segmentDurationMs = totalDurationMs / numWindows;
            CalculateStartPositions(segmentDurationMs);
            PositionSlider.Maximum = segmentDurationMs;
            PositionSlider.Value = 0;

            RefreshVideoLayout();

            for (int i = 0; i < numWindows; i++)
            {
                var media = new Media(libVLC, videoPath, FromType.FromPath);
                var player = new MediaPlayer(media) { Mute = true };
                player.Volume = AudibleVolume;
                player.EndReached += MediaPlayer_EndReached;
                mediaPlayers[i] = player;
                if (videoViews[i] != null)
                    videoViews[i].MediaPlayer = player;
            }

            if (numWindows > 0) SetCurrentWindow(0);

            // ── Play, seek, resume ──
            for (int i = 0; i < numWindows; i++)
                mediaPlayers[i]?.Play();

            await Task.Delay(500);
            if (currentLoadVersion != loadVersion)
                return;

            for (int i = 0; i < numWindows; i++)
            {
                if (mediaPlayers[i] != null)
                {
                    mediaPlayers[i].Pause();
                    mediaPlayers[i].Time = startPositions[i];
                }
                if (timeLabels[i] != null)
                {
                    var ts = TimeSpan.FromMilliseconds(startPositions[i]);
                    timeLabels[i].Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
                }
            }

            await Task.Delay(200);
            if (currentLoadVersion != loadVersion)
                return;

            for (int i = 0; i < numWindows; i++)
                mediaPlayers[i]?.Play();

            isVideoLoaded = true;
            ApplyAudioRouting();
            PlayPauseButton.Content = "Play/Pause";
        }
        catch (Exception ex)
        {
            File.AppendAllText(
                Path.Combine(CacheDir, "crash.log"),
                $"{DateTime.Now} LoadVideo: {ex}\n\n");
        }
        finally
        {
            loadSemaphore.Release();
        }
    }

    private void CalculateStartPositions(long segmentDurationMs)
    {
        for (int i = 0; i < MaxWindows; i++)
            startPositions[i] = 0;

        for (int i = 0; i < numWindows; i++)
            startPositions[i] = i * segmentDurationMs;
    }

    private void EnsureVideoGrid()
    {
        if (activeVideoGrid != null)
            return;

        activeVideoGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };
        VideoContainer.Children.Insert(0, activeVideoGrid);
    }

    private void EnsureVideoViews()
    {
        if (activeVideoGrid == null)
            return;

        for (int i = 0; i < MaxWindows; i++)
        {
            if (videoViews[i] != null)
                continue;

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

            int idx = i;
            var overlay = new Grid { Background = Brushes.Transparent };
            overlay.Children.Add(timeLabel);
            overlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            overlay.VerticalAlignment = VerticalAlignment.Stretch;

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
                    if (mediaPlayers[0]?.IsPlaying ?? false) PauseAll(); else PlayAll();
                    e.Handled = true;
                }
            };

            var view = new VideoView
            {
                Content = overlay,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ClipToBounds = true
            };

            videoViews[i] = view;
            activeVideoGrid.Children.Add(view);
        }
    }

    private void DisposeMediaPlayers()
    {
        for (int i = 0; i < MaxWindows; i++)
        {
            if (videoViews[i] != null)
                videoViews[i].MediaPlayer = null;

            if (mediaPlayers[i] != null)
            {
                mediaPlayers[i].EndReached -= MediaPlayer_EndReached;
                mediaPlayers[i].Stop();
                mediaPlayers[i].Dispose();
                mediaPlayers[i] = null!;
            }
        }
    }

    // ── Window Selection ───────────────────────────────────────────────

    private void SetCurrentWindow(int index)
    {
        if (index < 0 || index >= numWindows)
            return;

        currentWindowIndex = index;

        for (int i = 0; i < numWindows; i++)
        {
            if (timeLabels[i] != null)
                timeLabels[i].Foreground = Brushes.GreenYellow;
        }

        if (timeLabels[index] != null)
            timeLabels[index].Foreground = Brushes.Aquamarine;

        ApplyAudioRouting();
    }

    private void ApplyAudioRouting()
    {
        for (int i = 0; i < numWindows; i++)
        {
            if (mediaPlayers[i] == null)
                continue;

            var isAudible = !isMuted && i == currentWindowIndex;
            mediaPlayers[i].Mute = !isAudible;
            mediaPlayers[i].Volume = isAudible ? AudibleVolume : SilentVolume;
        }
    }

    private void ToggleExpandedWindow(int index)
    {
        if (index < 0 || index >= numWindows)
            return;

        expandedIndex = expandedIndex == index ? -1 : index;
        RefreshVideoLayout();
    }

    private void RefreshVideoLayout()
    {
        if (activeVideoGrid == null)
            return;

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
            if (videoViews[i] == null)
                continue;

            bool isVisible = i < numWindows;
            bool isExpandedCell = isExpanded && isVisible;
            videoViews[i].IsVisible = isVisible && (!isExpandedCell || i == expandedIndex);
            Grid.SetRowSpan(videoViews[i], 1);
            Grid.SetColumnSpan(videoViews[i], 1);

            if (!videoViews[i].IsVisible)
            {
                Grid.SetRow(videoViews[i], 0);
                Grid.SetColumn(videoViews[i], 0);
                continue;
            }

            if (isExpanded && i == expandedIndex)
            {
                Grid.SetRow(videoViews[i], 0);
                Grid.SetColumn(videoViews[i], 0);
                Grid.SetRowSpan(videoViews[i], rowCount);
                Grid.SetColumnSpan(videoViews[i], columnCount);
            }
            else
            {
                Grid.SetRow(videoViews[i], i / columns);
                Grid.SetColumn(videoViews[i], i % columns);
            }
        }

        activeVideoGrid.InvalidateMeasure();
        activeVideoGrid.InvalidateArrange();
    }



    // ── Playback Controls ──────────────────────────────────────────────

    private async void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        // If no video loaded, try to load from grid selection
        if (!isVideoLoaded || mediaPlayers[0] == null)
        {
            if (VideoDataGrid.SelectedItem is VideoInfo selected && !string.IsNullOrEmpty(selected.FullPath))
            {
                await DownloadAndPlay(selected.FullPath);
                return;
            }
            return;
        }

        if (mediaPlayers[0]?.IsPlaying ?? false)
            PauseAll();
        else
            PlayAll();
    }

    private void PlayAll()
    {
        for (int i = 0; i < numWindows; i++)
            mediaPlayers[i]?.Play();
    }

    private void PauseAll()
    {
        for (int i = 0; i < numWindows; i++)
            mediaPlayers[i]?.Pause();
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < numWindows; i++)
            mediaPlayers[i]?.Stop();
    }

    private void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        if (currentWindowIndex < 0 || currentWindowIndex >= numWindows)
            return;

        if (mediaPlayers[currentWindowIndex] == null)
            return;

        var syncTime = mediaPlayers[currentWindowIndex].Time;
        for (int i = 0; i < numWindows; i++)
        {
            if (mediaPlayers[i] == null || i == currentWindowIndex)
                continue;

            mediaPlayers[i].Time = syncTime;

            if (timeLabels[i] != null)
            {
                var ts = TimeSpan.FromMilliseconds(syncTime);
                timeLabels[i].Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }
        }
    }

    // ── Speed ──────────────────────────────────────────────────────────

    private void SpeedDownButton_Click(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < numWindows; i++)
            if (mediaPlayers[i] != null)
                mediaPlayers[i].SetRate(Math.Max(0.1f, mediaPlayers[i].Rate - 0.5f));
        UpdateSpeedLabel();
    }

    private void SpeedUpButton_Click(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < numWindows; i++)
            if (mediaPlayers[i] != null)
                mediaPlayers[i].SetRate(mediaPlayers[i].Rate + 0.5f);
        UpdateSpeedLabel();
    }

    private void UpdateSpeedLabel()
    {
        if (mediaPlayers[0] != null)
            SpeedLabel.Text = $"{mediaPlayers[0].Rate:F1}x";
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

    // ── Skip ───────────────────────────────────────────────────────────

    private void SkipButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tagStr && int.TryParse(tagStr, out int seconds))
        {
            for (int i = 0; i < numWindows; i++)
            {
                if (mediaPlayers[i] != null)
                {
                    var newTimeMs = mediaPlayers[i].Time + (seconds * 1000L);
                    if (newTimeMs < 0) newTimeMs = 0;
                    if (newTimeMs > totalDurationMs) newTimeMs = totalDurationMs;
                    mediaPlayers[i].Time = newTimeMs;
                }
            }
        }
    }

    // ── Slider ─────────────────────────────────────────────────────────

    private void PositionSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!isVideoLoaded || mediaPlayers[0] == null) return;
        if (isUpdatingSliderFromPlayback) return;

        ApplySliderSeek();
    }

    private void PositionSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!isVideoLoaded)
            return;

        isScrubbing = true;
    }

    private void PositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ApplySliderSeek();
        isScrubbing = false;
    }

    private void PositionSlider_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ApplySliderSeek();
        isScrubbing = false;
    }

    private void ApplySliderSeek()
    {
        if (!isVideoLoaded || mediaPlayers[0] == null)
            return;

        var sliderMs = Math.Clamp((long)PositionSlider.Value, 0, (long)PositionSlider.Maximum);
        var segmentDurationMs = totalDurationMs / numWindows;

        for (int i = 0; i < numWindows; i++)
        {
            if (mediaPlayers[i] == null)
                continue;

            var seekMs = (i * segmentDurationMs) + sliderMs;
            mediaPlayers[i].Time = seekMs;

            if (timeLabels[i] != null)
            {
                var ts = TimeSpan.FromMilliseconds(seekMs);
                timeLabels[i].Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }
        }
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ResetPlaybackToStart);
    }

    private void ResetPlaybackToStart()
    {
        PauseAll();
        PositionSlider.Value = 0;

        for (int i = 0; i < numWindows; i++)
        {
            if (mediaPlayers[i] != null)
            {
                mediaPlayers[i].Stop();
                mediaPlayers[i].Time = startPositions[i];
            }

            if (timeLabels[i] != null)
            {
                var ts = TimeSpan.FromMilliseconds(startPositions[i]);
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
            prevNumWindows = numWindows;
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
        if (string.IsNullOrEmpty(query))
        {
            VideoDataGrid.ItemsSource = null;
            return;
        }

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
        {
            await DownloadAndPlay(selected.FullPath);
        }
    }

    // ── Title Bar / Window Chrome ──────────────────────────────────────

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

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

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── Cleanup ────────────────────────────────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        timer?.Stop();
        downloadCts?.Cancel();
        Interlocked.Increment(ref loadVersion);

        for (int i = 0; i < MaxWindows; i++)
        {
            if (videoViews[i] != null)
            {
                videoViews[i].MediaPlayer = null;
                videoViews[i].Content = null;
            }
        }

        DisposeMediaPlayers();

        libVLC?.Dispose();
        httpClient.Dispose();
        base.OnClosing(e);
    }

    private string GetExternalOpenPath()
    {
        if (VideoDataGrid.SelectedItem is VideoInfo selected && !string.IsNullOrWhiteSpace(selected.FullPath))
        {
            var cachedPath = Path.Combine(CacheDir, SanitizeFileName(Path.GetFileName(selected.FullPath)));
            if (File.Exists(cachedPath))
                return cachedPath;

            if (File.Exists(selected.FullPath) || Directory.Exists(Path.GetDirectoryName(selected.FullPath) ?? string.Empty))
                return selected.FullPath;
        }

        if (!string.IsNullOrWhiteSpace(selectedVideoFile) && File.Exists(selectedVideoFile))
            return selectedVideoFile;

        return string.Empty;
    }
}

// Model for API response
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
