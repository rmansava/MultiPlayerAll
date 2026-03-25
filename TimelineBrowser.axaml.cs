using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private const int ThumbW = 240;
    private const int ThumbH = 135;

    private readonly string _videoPath;
    private readonly double _totalDuration;
    private readonly MainWindow _mainWindow;
    private int _generationVersion;

    public TimelineBrowser(string videoPath, double totalDuration, MainWindow mainWindow)
    {
        InitializeComponent();
        _videoPath = videoPath;
        _totalDuration = totalDuration;
        _mainWindow = mainWindow;

        // Auto-generate on open with default interval
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(100);
            await Generate();
        });
    }

    private void GenerateButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = Generate();
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
        var count = Math.Max(1, (int)(_totalDuration / interval));
        var version = Interlocked.Increment(ref _generationVersion);

        GenerateButton.IsEnabled = false;
        StatusText.Text = $"Generating {count} thumbnails (every {interval}s)...";
        ThumbnailPanel.Children.Clear();

        var thumbs = await Task.Run(() => GenerateThumbnails(interval, count, version));

        if (version != _generationVersion) return;

        for (int i = 0; i < thumbs.Count; i++)
        {
            var (bitmap, seconds) = thumbs[i];
            var ts = TimeSpan.FromSeconds(seconds);

            var panel = new StackPanel { Margin = new Thickness(4) };

            var img = new Image
            {
                Source = bitmap,
                Width = 230,
                Height = 130,
                Stretch = Stretch.Uniform,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };

            var label = new TextBlock
            {
                Text = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}",
                Foreground = Brushes.GreenYellow,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            double seekTo = seconds;
            img.PointerPressed += (_, _) =>
            {
                _mainWindow.JumpFromTimeline(seekTo);
            };

            panel.Children.Add(img);
            panel.Children.Add(label);
            ThumbnailPanel.Children.Add(panel);
        }

        GenerateButton.IsEnabled = true;
        StatusText.Text = $"{thumbs.Count} thumbnails — click to jump";
    }

    private List<(WriteableBitmap bitmap, double seconds)> GenerateThumbnails(int intervalSec, int count, int version)
    {
        var results = new List<(WriteableBitmap, double)>();

        var probe = new MpvPlayer();
        try
        {
            probe.SetOption("vo", "libmpv");
            probe.SetOption("ao", "null");
            probe.SetOption("pause", "yes");
            probe.SetOption("hr-seek", "yes");
            probe.Initialize();

            IntPtr renderCtx;
            unsafe
            {
                var apiTypeStr = Marshal.StringToCoTaskMemAnsi("sw");
                int advCtrl = 0;
                var createParams = stackalloc MpvInterop.MpvRenderParam[3];
                createParams[0] = new MpvInterop.MpvRenderParam { Type = MpvInterop.MPV_RENDER_PARAM_API_TYPE, Data = apiTypeStr };
                createParams[1] = new MpvInterop.MpvRenderParam { Type = MpvInterop.MPV_RENDER_PARAM_ADVANCED_CONTROL, Data = (IntPtr)(&advCtrl) };
                createParams[2] = new MpvInterop.MpvRenderParam { Type = MpvInterop.MPV_RENDER_PARAM_INVALID, Data = IntPtr.Zero };
                int err = MpvInterop.mpv_render_context_create(out renderCtx, probe.Handle, createParams);
                Marshal.FreeCoTaskMem(apiTypeStr);
                if (err < 0) return results;
            }

            probe.LoadFile(_videoPath);
            Thread.Sleep(500);

            var buffer = Marshal.AllocHGlobal(ThumbW * ThumbH * 4);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (version != _generationVersion) break;

                    double seekPos = i * intervalSec;
                    if (seekPos >= _totalDuration) break;

                    probe.Seek(seekPos);
                    Thread.Sleep(60);

                    unsafe
                    {
                        int stride = ThumbW * 4;
                        var size = stackalloc int[2];
                        size[0] = ThumbW;
                        size[1] = ThumbH;
                        uint strideVal = (uint)stride;
                        var formatStr = Marshal.StringToCoTaskMemAnsi("bgra");

                        var renderParams = stackalloc MpvInterop.MpvRenderParam[5];
                        renderParams[0] = new MpvInterop.MpvRenderParam { Type = 17, Data = (IntPtr)size };
                        renderParams[1] = new MpvInterop.MpvRenderParam { Type = 18, Data = formatStr };
                        renderParams[2] = new MpvInterop.MpvRenderParam { Type = 19, Data = (IntPtr)(&strideVal) };
                        renderParams[3] = new MpvInterop.MpvRenderParam { Type = 20, Data = buffer };
                        renderParams[4] = new MpvInterop.MpvRenderParam { Type = MpvInterop.MPV_RENDER_PARAM_INVALID, Data = IntPtr.Zero };

                        int renderErr = MpvInterop.mpv_render_context_render(renderCtx, renderParams);
                        Marshal.FreeCoTaskMem(formatStr);

                        if (renderErr >= 0)
                        {
                            var bitmap = new WriteableBitmap(
                                new PixelSize(ThumbW, ThumbH),
                                new Vector(96, 96),
                                Avalonia.Platform.PixelFormat.Bgra8888,
                                Avalonia.Platform.AlphaFormat.Premul);

                            using (var fb = bitmap.Lock())
                            {
                                Buffer.MemoryCopy((void*)buffer, (void*)fb.Address,
                                    fb.RowBytes * ThumbH, stride * ThumbH);
                            }
                            results.Add((bitmap, seekPos));
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                MpvInterop.mpv_render_context_free(renderCtx);
            }
        }
        finally
        {
            probe.Dispose();
        }

        return results;
    }
}
