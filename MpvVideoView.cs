using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace MultiPlayerAll;

/// <summary>
/// Avalonia OpenGL control that renders mpv video via the render API.
/// No native windows — full Avalonia mouse/overlay support.
/// Pattern follows HanumanInstitute/LibMpv-OpenGL reference implementation.
/// </summary>
public class MpvVideoView : OpenGlControlBase
{
    private IntPtr _renderContext;
    private MpvInterop.MpvOpenglGetProcAddressFn? _getProcAddressDelegate;
    private MpvInterop.MpvRenderUpdateFn? _updateCallbackDelegate;
    private Func<string, IntPtr>? _glGetProcAddress;

    // The mpv player handle — set before GL init, or call AttachPlayer after
    private MpvPlayer? _player;

    public MpvPlayer? Player => _player;

    private static void Log(string msg)
    {
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MultiPlayerAll", "crash.log"),
            $"{DateTime.Now} [MpvVideoView] {msg}\n");
    }

    public void AttachPlayer(MpvPlayer player)
    {
        _player = player;
        // Render context must be created inside OnOpenGlRender where GL context is current
        RequestNextFrameRendering();
    }

    public void DetachPlayer()
    {
        DestroyRenderContext();
        _player = null;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _glGetProcAddress = gl.GetProcAddress;
        Log("OnOpenGlInit");
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_player == null || !_player.IsValid)
            return;

        // Create render context on first render (GL context is current here)
        if (_renderContext == IntPtr.Zero && _glGetProcAddress != null)
        {
            CreateRenderContext();
            if (_renderContext == IntPtr.Zero)
                return;
        }

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling));
        int h = Math.Max(1, (int)(Bounds.Height * scaling));

        // Use stackalloc + fixed for the FBO struct (matching reference impl)
        var fbo = new MpvInterop.MpvOpenglFbo { Fbo = fb, W = w, H = h, InternalFormat = 0 };
        int flipY = 1;

        var renderParams = stackalloc MpvInterop.MpvRenderParam[3];
        renderParams[0] = new MpvInterop.MpvRenderParam
        {
            Type = MpvInterop.MPV_RENDER_PARAM_OPENGL_FBO,
            Data = (IntPtr)(&fbo)
        };
        renderParams[1] = new MpvInterop.MpvRenderParam
        {
            Type = MpvInterop.MPV_RENDER_PARAM_FLIP_Y,
            Data = (IntPtr)(&flipY)
        };
        renderParams[2] = new MpvInterop.MpvRenderParam
        {
            Type = MpvInterop.MPV_RENDER_PARAM_INVALID,
            Data = IntPtr.Zero
        };

        MpvInterop.mpv_render_context_render(_renderContext, renderParams);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        DestroyRenderContext();
        _glGetProcAddress = null;
    }

    private unsafe void CreateRenderContext()
    {
        if (_player == null || _glGetProcAddress == null || _renderContext != IntPtr.Zero)
            return;

        Log("Creating render context...");

        // Must keep delegates alive to prevent GC
        _getProcAddressDelegate = (ctx, name) => _glGetProcAddress(name);
        _updateCallbackDelegate = OnMpvUpdateCallback;

        var initParams = new MpvInterop.MpvOpenglInitParams
        {
            GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProcAddressDelegate),
            GetProcAddressCtx = IntPtr.Zero
        };

        var apiTypeStr = Marshal.StringToCoTaskMemAnsi("opengl");
        var initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvInterop.MpvOpenglInitParams>());
        int advCtrl = 0; // reference uses 0

        try
        {
            Marshal.StructureToPtr(initParams, initParamsPtr, false);

            var createParams = stackalloc MpvInterop.MpvRenderParam[4];
            createParams[0] = new MpvInterop.MpvRenderParam
            {
                Type = MpvInterop.MPV_RENDER_PARAM_API_TYPE,
                Data = apiTypeStr
            };
            createParams[1] = new MpvInterop.MpvRenderParam
            {
                Type = MpvInterop.MPV_RENDER_PARAM_OPENGL_INIT_PARAMS,
                Data = initParamsPtr
            };
            createParams[2] = new MpvInterop.MpvRenderParam
            {
                Type = MpvInterop.MPV_RENDER_PARAM_ADVANCED_CONTROL,
                Data = (IntPtr)(&advCtrl)
            };
            createParams[3] = new MpvInterop.MpvRenderParam
            {
                Type = MpvInterop.MPV_RENDER_PARAM_INVALID,
                Data = IntPtr.Zero
            };

            int err = MpvInterop.mpv_render_context_create(out _renderContext, _player.Handle, createParams);
            Log($"mpv_render_context_create result: {err} ctx={_renderContext}");

            if (err < 0)
            {
                _renderContext = IntPtr.Zero;
                return;
            }

            // Set update callback — mpv calls this when a new frame is ready
            var cbPtr = Marshal.GetFunctionPointerForDelegate(_updateCallbackDelegate);
            MpvInterop.mpv_render_context_set_update_callback(_renderContext, cbPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiTypeStr);
            Marshal.FreeHGlobal(initParamsPtr);
        }
    }

    private void OnMpvUpdateCallback(IntPtr ctx)
    {
        // Called from mpv's thread when a new frame is decoded
        Dispatcher.UIThread.InvokeAsync(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    private void DestroyRenderContext()
    {
        if (_renderContext != IntPtr.Zero)
        {
            MpvInterop.mpv_render_context_free(_renderContext);
            _renderContext = IntPtr.Zero;
        }
    }
}
