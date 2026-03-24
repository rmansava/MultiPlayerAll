using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MultiPlayerAll;

public partial class FrameIt : Window
{
    private const int Columns = 5;
    private const int Rows = 4;
    private const int TotalFrames = Columns * Rows; // 20
    private const double FrameInterval = 1.0 / 30.0; // 30fps

    private readonly string _videoPath;
    private readonly double _totalDuration;
    private double _gridStartPosition; // seconds
    private readonly Image[] _frameImages = new Image[TotalFrames];
    private readonly WriteableBitmap[] _frameBitmaps = new WriteableBitmap[TotalFrames];
    private int _expandedIndex = -1;

    // mpv for frame extraction (offscreen)
    private MpvPlayer? _extractPlayer;
    private IntPtr _renderContext;
    private MpvInterop.MpvOpenglGetProcAddressFn? _getProcAddressDelegate;

    // Software rendering buffer
    private IntPtr _swBuffer;
    private int _swWidth = 640;
    private int _swHeight = 360;

    public FrameIt(string videoPath, double totalDuration, double startPosition)
    {
        InitializeComponent();
        _videoPath = videoPath;
        _totalDuration = totalDuration;

        // Center the grid around the start position (10 frames before)
        _gridStartPosition = Math.Max(0, startPosition - (10 * FrameInterval));

        BuildGrid();
        InitExtractor();
    }

    private void BuildGrid()
    {
        FrameGrid.RowDefinitions.Clear();
        FrameGrid.ColumnDefinitions.Clear();

        for (int r = 0; r < Rows; r++)
            FrameGrid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        for (int c = 0; c < Columns; c++)
            FrameGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (int i = 0; i < TotalFrames; i++)
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Margin = new Thickness(1)
            };

            int idx = i;
            image.DoubleTapped += (s, e) => ToggleExpand(idx);

            Grid.SetRow(image, i / Columns);
            Grid.SetColumn(image, i % Columns);
            FrameGrid.Children.Add(image);
            _frameImages[i] = image;
        }
    }

    private void InitExtractor()
    {
        try
        {
            _extractPlayer = new MpvPlayer();
            _extractPlayer.SetOption("vo", "libmpv");
            _extractPlayer.SetOption("ao", "null");
            _extractPlayer.SetOption("keep-open", "yes");
            _extractPlayer.SetOption("hr-seek", "yes");
            _extractPlayer.SetOption("osc", "no");
            _extractPlayer.SetOption("pause", "yes");
            _extractPlayer.Initialize();

            // Create software render context (no GL needed)
            CreateSwRenderContext();

            _extractPlayer.LoadFile(_videoPath);

            // Wait for load then extract frames
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Task.Delay(500);
                await ExtractFrames();
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private unsafe void CreateSwRenderContext()
    {
        if (_extractPlayer == null) return;

        var apiTypeStr = Marshal.StringToCoTaskMemAnsi("sw");
        int advCtrl = 0;

        try
        {
            var createParams = stackalloc MpvInterop.MpvRenderParam[3];
            createParams[0] = new MpvInterop.MpvRenderParam
            {
                Type = MpvInterop.MPV_RENDER_PARAM_API_TYPE,
                Data = apiTypeStr
            };
            createParams[1] = new MpvInterop.MpvRenderParam
            {
                Type = MpvInterop.MPV_RENDER_PARAM_ADVANCED_CONTROL,
                Data = (IntPtr)(&advCtrl)
            };
            createParams[2] = new MpvInterop.MpvRenderParam
            {
                Type = MpvInterop.MPV_RENDER_PARAM_INVALID,
                Data = IntPtr.Zero
            };

            int err = MpvInterop.mpv_render_context_create(out _renderContext, _extractPlayer.Handle, createParams);
            if (err < 0)
            {
                StatusText.Text = $"SW render context failed: {err}";
                _renderContext = IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiTypeStr);
        }

        // Allocate buffer for BGRA frames
        _swBuffer = Marshal.AllocHGlobal(_swWidth * _swHeight * 4);
    }

    private async Task ExtractFrames()
    {
        if (_extractPlayer == null || _renderContext == IntPtr.Zero)
            return;

        UpdateTimestamp();

        for (int i = 0; i < TotalFrames; i++)
        {
            double seekPos = _gridStartPosition + (i * FrameInterval);
            if (seekPos > _totalDuration) break;

            _extractPlayer.Seek(seekPos);
            await Task.Delay(80); // wait for seek + decode

            var bitmap = RenderFrame();
            if (bitmap != null)
            {
                _frameImages[i].Source = bitmap;
                _frameBitmaps[i] = bitmap;
            }
        }

        StatusText.Text = $"Showing {TotalFrames} frames starting at {FormatTime(_gridStartPosition)}";
    }

    private unsafe WriteableBitmap? RenderFrame()
    {
        if (_renderContext == IntPtr.Zero) return null;

        try
        {
            int stride = _swWidth * 4;
            var size = stackalloc int[2];
            size[0] = _swWidth;
            size[1] = _swHeight;

            uint strideVal = (uint)stride;
            var formatStr = Marshal.StringToCoTaskMemAnsi("bgra");

            try
            {
                var renderParams = stackalloc MpvInterop.MpvRenderParam[6];
                renderParams[0] = new MpvInterop.MpvRenderParam
                {
                    Type = 17, // MPV_RENDER_PARAM_SW_SIZE
                    Data = (IntPtr)size
                };
                renderParams[1] = new MpvInterop.MpvRenderParam
                {
                    Type = 18, // MPV_RENDER_PARAM_SW_FORMAT
                    Data = formatStr
                };
                renderParams[2] = new MpvInterop.MpvRenderParam
                {
                    Type = 19, // MPV_RENDER_PARAM_SW_STRIDE
                    Data = (IntPtr)(&strideVal)
                };
                renderParams[3] = new MpvInterop.MpvRenderParam
                {
                    Type = 20, // MPV_RENDER_PARAM_SW_POINTER
                    Data = _swBuffer
                };
                renderParams[4] = new MpvInterop.MpvRenderParam
                {
                    Type = MpvInterop.MPV_RENDER_PARAM_INVALID,
                    Data = IntPtr.Zero
                };

                int err = MpvInterop.mpv_render_context_render(_renderContext, renderParams);
                if (err < 0) return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatStr);
            }

            // Create WriteableBitmap from buffer
            var bitmap = new WriteableBitmap(
                new PixelSize(_swWidth, _swHeight),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using (var fb = bitmap.Lock())
            {
                unsafe
                {
                    Buffer.MemoryCopy(
                        (void*)_swBuffer,
                        (void*)fb.Address,
                        fb.RowBytes * _swHeight,
                        stride * _swHeight);
                }
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void PrevButton_Click(object? sender, RoutedEventArgs e)
    {
        _gridStartPosition = Math.Max(0, _gridStartPosition - (TotalFrames * FrameInterval));
        _expandedIndex = -1;
        RefreshLayout();
        _ = ExtractFrames();
    }

    private void NextButton_Click(object? sender, RoutedEventArgs e)
    {
        var newPos = _gridStartPosition + (TotalFrames * FrameInterval);
        if (newPos < _totalDuration)
        {
            _gridStartPosition = newPos;
            _expandedIndex = -1;
            RefreshLayout();
            _ = ExtractFrames();
        }
    }

    private void ToggleExpand(int index)
    {
        _expandedIndex = _expandedIndex == index ? -1 : index;
        RefreshLayout();
    }

    private void RefreshLayout()
    {
        if (_expandedIndex >= 0)
        {
            // Show only the expanded frame
            for (int i = 0; i < TotalFrames; i++)
            {
                _frameImages[i].IsVisible = i == _expandedIndex;
                if (i == _expandedIndex)
                {
                    Grid.SetRow(_frameImages[i], 0);
                    Grid.SetColumn(_frameImages[i], 0);
                    Grid.SetRowSpan(_frameImages[i], Rows);
                    Grid.SetColumnSpan(_frameImages[i], Columns);
                }
            }
        }
        else
        {
            // Restore grid
            for (int i = 0; i < TotalFrames; i++)
            {
                _frameImages[i].IsVisible = true;
                Grid.SetRow(_frameImages[i], i / Columns);
                Grid.SetColumn(_frameImages[i], i % Columns);
                Grid.SetRowSpan(_frameImages[i], 1);
                Grid.SetColumnSpan(_frameImages[i], 1);
            }
        }
    }

    private void UpdateTimestamp()
    {
        TimestampLabel.Text = FormatTime(_gridStartPosition);
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_renderContext != IntPtr.Zero)
        {
            MpvInterop.mpv_render_context_free(_renderContext);
            _renderContext = IntPtr.Zero;
        }
        _extractPlayer?.Dispose();
        if (_swBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_swBuffer);
            _swBuffer = IntPtr.Zero;
        }
        base.OnClosing(e);
    }
}
