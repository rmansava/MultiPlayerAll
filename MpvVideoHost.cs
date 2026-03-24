using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MultiPlayerAll;

public class MpvVideoHost : NativeControlHost
{
    private IntPtr _nativeHandle;
    private IntPtr _originalWndProc;
    private Win32WndProc? _wndProcDelegate; // prevent GC
    private bool _handleReady;

    // Win32 interop
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr Win32WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GWL_WNDPROC = -4;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONDBLCLK = 0x0203;

    private IntPtr _mpvChildHwnd;
    private IntPtr _mpvChildOrigWndProc;
    private Win32WndProc? _mpvChildWndProcDelegate;

    public IntPtr NativeHandle => _nativeHandle;
    public bool IsHandleReady => _handleReady;

    public event Action? HandleReady;
    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? DoubleClick;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        return SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _nativeHandle = CreateWindowExW(
                0, "STATIC", "",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, 800, 600,
                parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            // Subclass the window to intercept mouse events
            _wndProcDelegate = WndProc;
            var newWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _originalWndProc = SetWindowLongPtr(_nativeHandle, GWL_WNDPROC, newWndProc);

            _handleReady = true;
            HandleReady?.Invoke();
            return new PlatformHandle(_nativeHandle, "HWND");
        }

        _nativeHandle = parent.Handle;
        _handleReady = true;
        HandleReady?.Invoke();
        return base.CreateNativeControlCore(parent);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_LBUTTONUP:
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LeftClick?.Invoke());
                break;
            case WM_RBUTTONUP:
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RightClick?.Invoke());
                break;
            case WM_LBUTTONDBLCLK:
                Avalonia.Threading.Dispatcher.UIThread.Post(() => DoubleClick?.Invoke());
                break;
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Call this after mpv has started playing to find and subclass its render child window.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int GetClassNameW(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder lpClassName, int nMaxCount);

    public void InstallMouseHook()
    {
        // No-op. Child window subclassing is enough and avoids duplicate events.
    }

    public void SubclassMpvChild()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || _nativeHandle == IntPtr.Zero)
            return;

        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MultiPlayerAll", "crash.log");
        int childCount = 0;

        EnumChildWindows(_nativeHandle, (child, _) =>
        {
            childCount++;
            var sb = new System.Text.StringBuilder(256);
            GetClassNameW(child, sb, 256);
            System.IO.File.AppendAllText(logPath,
                $"{DateTime.Now} Found child HWND={child} class={sb} of parent={_nativeHandle}\n");

            if (child != IntPtr.Zero && child != _nativeHandle)
            {
                _mpvChildHwnd = child;
                _mpvChildWndProcDelegate = MpvChildWndProc;
                var newProc = Marshal.GetFunctionPointerForDelegate(_mpvChildWndProcDelegate);
                _mpvChildOrigWndProc = SetWindowLongPtr(child, GWL_WNDPROC, newProc);
                return false;
            }
            return true;
        }, IntPtr.Zero);

        System.IO.File.AppendAllText(logPath,
            $"{DateTime.Now} SubclassMpvChild: parent={_nativeHandle} found {childCount} children\n");
    }

    private IntPtr MpvChildWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_LBUTTONUP:
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LeftClick?.Invoke());
                break;
            case WM_RBUTTONUP:
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RightClick?.Invoke());
                break;
            case WM_LBUTTONDBLCLK:
                Avalonia.Threading.Dispatcher.UIThread.Post(() => DoubleClick?.Invoke());
                break;
        }
        return CallWindowProc(_mpvChildOrigWndProc, hWnd, msg, wParam, lParam);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _handleReady = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _nativeHandle != IntPtr.Zero)
        {
            if (_mpvChildHwnd != IntPtr.Zero && _mpvChildOrigWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_mpvChildHwnd, GWL_WNDPROC, _mpvChildOrigWndProc);
                _mpvChildOrigWndProc = IntPtr.Zero;
                _mpvChildHwnd = IntPtr.Zero;
            }

            if (_originalWndProc != IntPtr.Zero)
                SetWindowLongPtr(_nativeHandle, GWL_WNDPROC, _originalWndProc);

            DestroyWindow(_nativeHandle);
            _nativeHandle = IntPtr.Zero;
        }
        base.DestroyNativeControlCore(control);
    }
}
