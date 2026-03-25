using System;
using System.Runtime.InteropServices;

namespace MultiPlayerAll;

public static class MpvInterop
{
    private const string LibMpv = "libmpv-2";

    static MpvInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvInterop).Assembly, (name, assembly, path) =>
        {
            if (name != LibMpv) return IntPtr.Zero;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return NativeLibrary.Load("libmpv-2.dll");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Try common Homebrew paths
                foreach (var p in new[] { "/opt/homebrew/lib/libmpv.dylib", "/usr/local/lib/libmpv.dylib" })
                    if (File.Exists(p)) return NativeLibrary.Load(p);
                return NativeLibrary.Load("libmpv.dylib");
            }
            // Linux
            foreach (var p in new[] { "libmpv.so.2", "libmpv.so" })
            {
                if (NativeLibrary.TryLoad(p, out var handle)) return handle;
            }
            return NativeLibrary.Load("libmpv.so.2");
        });
    }

    public const int MPV_FORMAT_NONE = 0;
    public const int MPV_FORMAT_STRING = 1;
    public const int MPV_FORMAT_FLAG = 3;
    public const int MPV_FORMAT_INT64 = 4;
    public const int MPV_FORMAT_DOUBLE = 5;

    public const int MPV_EVENT_NONE = 0;
    public const int MPV_EVENT_CLIENT_MESSAGE = 16;

    // Render param types
    public const int MPV_RENDER_PARAM_INVALID = 0;
    public const int MPV_RENDER_PARAM_API_TYPE = 1;
    public const int MPV_RENDER_PARAM_OPENGL_INIT_PARAMS = 2;
    public const int MPV_RENDER_PARAM_OPENGL_FBO = 3;
    public const int MPV_RENDER_PARAM_FLIP_Y = 4;
    public const int MPV_RENDER_PARAM_ADVANCED_CONTROL = 10;
    public const int MPV_RENDER_UPDATE_FRAME = 1;

    // Core mpv
    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, out double data);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, out long data);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, out int data);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    // Render API
    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int mpv_render_context_create(out IntPtr res, IntPtr mpv, MpvRenderParam* @params);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_set_update_callback(IntPtr ctx, IntPtr callback, IntPtr callbackCtx);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_render_context_update(IntPtr ctx);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int mpv_render_context_render(IntPtr ctx, MpvRenderParam* @params);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_report_swap(IntPtr ctx);

    [DllImport(LibMpv, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_free(IntPtr ctx);

    // Structs
    [StructLayout(LayoutKind.Sequential)]
    public struct MpvRenderParam
    {
        public int Type;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvOpenglFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr MpvOpenglGetProcAddressFn(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvOpenglInitParams
    {
        public IntPtr GetProcAddress; // function pointer
        public IntPtr GetProcAddressCtx;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MpvRenderUpdateFn(IntPtr cbCtx);
}

/// <summary>
/// Wraps a single mpv player instance.
/// </summary>
public class MpvPlayer : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public IntPtr Handle => _handle;
    public bool IsValid => _handle != IntPtr.Zero;

    public MpvPlayer()
    {
        _handle = MpvInterop.mpv_create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create mpv instance");
    }

    public void SetOption(string name, string value) =>
        MpvInterop.mpv_set_option_string(_handle, name, value);

    public void Initialize()
    {
        var err = MpvInterop.mpv_initialize(_handle);
        if (err < 0)
            throw new InvalidOperationException($"mpv_initialize failed: {err}");
    }

    public void SetProperty(string name, string value) =>
        MpvInterop.mpv_set_property_string(_handle, name, value);

    public double GetPropertyDouble(string name)
    {
        var err = MpvInterop.mpv_get_property(_handle, name, MpvInterop.MPV_FORMAT_DOUBLE, out double value);
        return err >= 0 ? value : 0;
    }

    public long GetPropertyLong(string name)
    {
        var err = MpvInterop.mpv_get_property(_handle, name, MpvInterop.MPV_FORMAT_INT64, out long value);
        return err >= 0 ? value : 0;
    }

    public void Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            MpvInterop.mpv_command(_handle, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (ptrs[i] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(ptrs[i]);
        }
    }

    public double Duration => GetPropertyDouble("duration");
    public double Position => GetPropertyDouble("time-pos");
    public double Volume { get => GetPropertyDouble("volume"); set => SetProperty("volume", value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    public double Speed { get => GetPropertyDouble("speed"); set => SetProperty("speed", value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
    public bool IsPaused
    {
        get
        {
            var err = MpvInterop.mpv_get_property(_handle, "pause", MpvInterop.MPV_FORMAT_FLAG, out int flag);
            return err >= 0 && flag == 1;
        }
    }

    public void LoadFile(string path) => Command("loadfile", path);
    public void Seek(double seconds, string mode = "absolute") => Command("seek", seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), mode);
    public void Pause() => SetProperty("pause", "yes");
    public void Resume() => SetProperty("pause", "no");
    public void Stop() => Command("stop");

    public void Dispose()
    {
        if (!_disposed && _handle != IntPtr.Zero)
        {
            _disposed = true;
            MpvInterop.mpv_terminate_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
