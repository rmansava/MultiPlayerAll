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
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private int _generationVersion;

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

        GenerateButton.IsEnabled = false;
        StatusText.Text = "Requesting thumbnails from server...";
        ThumbnailPanel.Children.Clear();

        try
        {
            var url = $"{_apiBaseUrl}/thumbnails?path={Uri.EscapeDataString(_remotePath)}&interval={interval}&width=240&height=135";
            var thumbInfos = await _httpClient.GetFromJsonAsync<List<ThumbnailApiInfo>>(url);

            if (version != _generationVersion) return;

            ThumbnailPanel.Children.Clear();

            if (thumbInfos == null || thumbInfos.Count == 0)
            {
                StatusText.Text = "No thumbnails generated — is ffmpeg installed on the server?";
                GenerateButton.IsEnabled = true;
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
                    Width = 230,
                    Height = 130,
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

            StatusText.Text = $"{thumbInfos.Count} thumbnails — click to jump";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
        }

        GenerateButton.IsEnabled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _httpClient.Dispose();
        base.OnClosing(e);
    }
}
