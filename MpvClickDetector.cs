using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace MultiPlayerAll;

/// <summary>
/// Polls mouse state to detect clicks on mpv native windows.
/// Works regardless of how mpv handles input internally.
/// </summary>
public class MpvClickDetector : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    private readonly MpvVideoHost[] _hosts;
    private readonly int _maxWindows;
    private readonly DispatcherTimer _timer;
    private bool _leftWasDown;
    private bool _rightWasDown;
    private DateTime _lastLeftClickTime;
    private int _lastLeftClickIndex = -1;

    public event Action<int>? LeftClicked;    // index of clicked window
    public event Action<int>? RightClicked;
    public event Action<int>? DoubleClicked;

    public MpvClickDetector(MpvVideoHost[] hosts, int maxWindows)
    {
        _hosts = hosts;
        _maxWindows = maxWindows;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += Poll;
        _timer.Start();
    }

    private void Poll(object? sender, EventArgs e)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        bool leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool rightDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        // Detect left button release (click complete)
        if (_leftWasDown && !leftDown)
        {
            int idx = GetWindowUnderCursor();
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MultiPlayerAll", "crash.log"),
                $"{DateTime.Now} ClickDetector: left release, idx={idx}\n");
            if (idx >= 0)
            {
                var now = DateTime.UtcNow;
                if (_lastLeftClickIndex == idx && (now - _lastLeftClickTime).TotalMilliseconds <= 400)
                {
                    DoubleClicked?.Invoke(idx);
                    _lastLeftClickIndex = -1; // reset
                }
                else
                {
                    LeftClicked?.Invoke(idx);
                    _lastLeftClickIndex = idx;
                    _lastLeftClickTime = now;
                }
            }
        }

        // Detect right button release
        if (_rightWasDown && !rightDown)
        {
            int idx = GetWindowUnderCursor();
            if (idx >= 0)
                RightClicked?.Invoke(idx);
        }

        _leftWasDown = leftDown;
        _rightWasDown = rightDown;
    }

    private int GetWindowUnderCursor()
    {
        GetCursorPos(out POINT pt);
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return -1;

        for (int i = 0; i < _maxWindows; i++)
        {
            if (_hosts[i] == null || !_hosts[i].IsHandleReady)
                continue;

            var nativeHandle = _hosts[i].NativeHandle;
            if (nativeHandle == IntPtr.Zero)
                continue;

            // Check if cursor is over this host's window or any of its children
            if (hwnd == nativeHandle)
                return i;

            // Check parent chain
            var parent = GetParent(hwnd);
            if (parent == nativeHandle)
                return i;

            // Check grandparent (mpv may nest windows)
            if (parent != IntPtr.Zero)
            {
                var grandparent = GetParent(parent);
                if (grandparent == nativeHandle)
                    return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
