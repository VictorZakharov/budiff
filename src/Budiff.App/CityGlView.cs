using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using static Avalonia.OpenGL.GlConsts;

namespace Budiff.App;

// The code city on the GPU: every plinth and building as triangles in world
// space, uploaded once per city (colours again when the search changes), and
// drawn with a depth buffer - so nothing is sorted or projected per frame,
// a frame only sends the camera. 4x multisampled where the driver allows,
// then copied into the control's surface. Thin dark edges come from the
// fragment shader (each face knows where its borders are), not extra lines.
//
// It sits under CodeCityView, which keeps the camera, input, picking and the
// overlays (labels, card, panel). Where OpenGL is not available (headless
// tests, an old driver) it never becomes ready and CodeCityView paints the
// city itself.
internal sealed unsafe class CityGlView : OpenGlControlBase
{
    // Per vertex: position (3), shade (1), face uv (2), edge weight (1).
    public const int Stride = 7;

    private const int GlDynamicDraw = 0x88E8;
    private const int GlLequal = 0x0203;
    private const int GlDepthStencilAttachment = 0x821A;
    private const int Samples = 4;

    private static readonly float[] Clear = { 0x1B / 255f, 0x1C / 255f, 0x1F / 255f };

    private float[] _vertices = Array.Empty<float>();
    private byte[] _colors = Array.Empty<byte>();
    private int _vertexCount;
    private bool _geometryDirty;
    private bool _colorsDirty;

    private int _program;
    private int _vao;
    private int _vbo;
    private int _cbo;
    private int _mvp;
    private int _fbo;
    private int _colorBuffer;
    private int _depthBuffer;
    private int _fboWidth;
    private int _fboHeight;
    private bool _multisample;
    private delegate* unmanaged<int, int, int, int, int, void> _storageMultisample;

    public CityGlView()
    {
        IsHitTestVisible = false;
    }

    // Where the camera comes from (and who to tell when the GPU is ready).
    public CodeCityView? Scene { get; set; }

    public bool IsReady { get; private set; }

    public string Renderer { get; private set; } = string.Empty;

    public double LastGpuSubmitMs { get; private set; }

    public void SetGeometry(float[] vertices, byte[] colors, int vertexCount)
    {
        (_vertices, _colors, _vertexCount) = (vertices, colors, vertexCount);
        _geometryDirty = _colorsDirty = true;
        RequestNextFrameRendering();
    }

    public void SetColors(byte[] colors)
    {
        _colors = colors;
        _colorsDirty = true;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        try
        {
            var es = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
            var header = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
            var vertex = gl.CreateShader(GL_VERTEX_SHADER);
            var fragment = gl.CreateShader(GL_FRAGMENT_SHADER);
            if (gl.CompileShaderAndGetError(vertex, header + VertexSource) is { } vertexError)
            {
                throw new InvalidOperationException("Vertex shader: " + vertexError);
            }

            if (gl.CompileShaderAndGetError(fragment, header + FragmentSource) is { } fragmentError)
            {
                throw new InvalidOperationException("Fragment shader: " + fragmentError);
            }

            _program = gl.CreateProgram();
            gl.AttachShader(_program, vertex);
            gl.AttachShader(_program, fragment);
            gl.BindAttribLocationString(_program, 0, "aPos");
            gl.BindAttribLocationString(_program, 1, "aShade");
            gl.BindAttribLocationString(_program, 2, "aUv");
            gl.BindAttribLocationString(_program, 3, "aEdge");
            gl.BindAttribLocationString(_program, 4, "aColor");
            if (gl.LinkProgramAndGetError(_program) is { } linkError)
            {
                throw new InvalidOperationException("Link: " + linkError);
            }

            gl.DeleteShader(vertex);
            gl.DeleteShader(fragment);
            _mvp = gl.GetUniformLocationString(_program, "uMvp");

            _vbo = gl.GenBuffer();
            _cbo = gl.GenBuffer();
            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);
            gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
            gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, Stride * 4, IntPtr.Zero);
            gl.VertexAttribPointer(1, 1, GL_FLOAT, 0, Stride * 4, 12);
            gl.VertexAttribPointer(2, 2, GL_FLOAT, 0, Stride * 4, 16);
            gl.VertexAttribPointer(3, 1, GL_FLOAT, 0, Stride * 4, 24);
            for (var i = 0; i < 4; i++)
            {
                gl.EnableVertexAttribArray(i);
            }

            gl.BindBuffer(GL_ARRAY_BUFFER, _cbo);
            gl.VertexAttribPointer(4, 4, GL_UNSIGNED_BYTE, 1, 4, IntPtr.Zero);
            gl.EnableVertexAttribArray(4);
            gl.BindVertexArray(0);
            gl.BindBuffer(GL_ARRAY_BUFFER, 0);

            var storage = gl.GetProcAddress("glRenderbufferStorageMultisample");
            _storageMultisample = (delegate* unmanaged<int, int, int, int, int, void>)storage;
            Renderer = $"{gl.Renderer} ({gl.Version})";
            _geometryDirty = _colorsDirty = _vertexCount > 0;
            IsReady = true;
        }
        catch (Exception ex)
        {
            Renderer = "OpenGL unavailable: " + ex.Message;
            IsReady = false;
        }

        Dispatcher.UIThread.Post(() => Scene?.OnGpuChanged());
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        IsReady = false;
        DeleteTarget(gl);
        if (_program != 0)
        {
            gl.DeleteBuffer(_vbo);
            gl.DeleteBuffer(_cbo);
            gl.DeleteVertexArray(_vao);
            gl.DeleteProgram(_program);
            _program = 0;
        }

        base.OnOpenGlDeinit(gl);
        Dispatcher.UIThread.Post(() => Scene?.OnGpuChanged());
    }

    protected override void OnOpenGlLost()
    {
        IsReady = false;
        _fbo = 0;
        base.OnOpenGlLost();
        Dispatcher.UIThread.Post(() => Scene?.OnGpuChanged());
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!IsReady || Scene is not { } scene)
        {
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        Upload(gl);
        EnsureTarget(gl, width, height);

        gl.BindFramebuffer(GL_FRAMEBUFFER, _fbo != 0 ? _fbo : fb);
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(Clear[0], Clear[1], Clear[2], 1);
        gl.ClearDepth(1);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        if (_vertexCount > 0)
        {
            gl.Enable(GL_DEPTH_TEST);
            gl.DepthFunc(GlLequal);
            gl.DepthMask(1);
            gl.UseProgram(_program);
            var mvp = scene.ViewProjection((float)width / height);
            gl.UniformMatrix4fv(_mvp, 1, false, &mvp);
            gl.BindVertexArray(_vao);
            gl.DrawArrays(GL_TRIANGLES, 0, _vertexCount);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(GL_DEPTH_TEST);
        }

        if (_fbo != 0)
        {
            gl.BindFramebuffer(GL_READ_FRAMEBUFFER, _fbo);
            gl.BindFramebuffer(GL_DRAW_FRAMEBUFFER, fb);
            gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height, GL_COLOR_BUFFER_BIT, GL_NEAREST);
            gl.BindFramebuffer(GL_FRAMEBUFFER, fb);
        }

        LastGpuSubmitMs = clock.Elapsed.TotalMilliseconds;
    }

    private void Upload(GlInterface gl)
    {
        if (_geometryDirty)
        {
            _geometryDirty = false;
            gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
            fixed (float* data = _vertices)
            {
                gl.BufferData(GL_ARRAY_BUFFER, _vertexCount * Stride * 4, (IntPtr)data, GL_STATIC_DRAW);
            }
        }

        if (_colorsDirty)
        {
            _colorsDirty = false;
            gl.BindBuffer(GL_ARRAY_BUFFER, _cbo);
            fixed (byte* data = _colors)
            {
                gl.BufferData(GL_ARRAY_BUFFER, _vertexCount * 4, (IntPtr)data, GlDynamicDraw);
            }
        }

        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }

    // An offscreen target with a depth buffer, multisampled when the driver
    // has glRenderbufferStorageMultisample (GLES 3 / GL 3 always do).
    private void EnsureTarget(GlInterface gl, int width, int height)
    {
        if (_fbo != 0 && width == _fboWidth && height == _fboHeight)
        {
            return;
        }

        DeleteTarget(gl);
        _fbo = gl.GenFramebuffer();
        _colorBuffer = gl.GenRenderbuffer();
        _depthBuffer = gl.GenRenderbuffer();
        _multisample = _storageMultisample != null;
        gl.BindRenderbuffer(GL_RENDERBUFFER, _colorBuffer);
        Storage(gl, GL_RGBA8, width, height);
        gl.BindRenderbuffer(GL_RENDERBUFFER, _depthBuffer);
        Storage(gl, GL_DEPTH24_STENCIL8, width, height);
        gl.BindFramebuffer(GL_FRAMEBUFFER, _fbo);
        gl.FramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, _colorBuffer);
        gl.FramebufferRenderbuffer(GL_FRAMEBUFFER, GlDepthStencilAttachment, GL_RENDERBUFFER, _depthBuffer);
        if (gl.CheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE && _multisample)
        {
            // No multisampling after all: plain buffers.
            _storageMultisample = null;
            DeleteTarget(gl);
            EnsureTarget(gl, width, height);
            return;
        }

        gl.BindRenderbuffer(GL_RENDERBUFFER, 0);
        (_fboWidth, _fboHeight) = (width, height);
    }

    private void Storage(GlInterface gl, int format, int width, int height)
    {
        if (_multisample)
        {
            _storageMultisample(GL_RENDERBUFFER, Samples, format, width, height);
        }
        else
        {
            gl.RenderbufferStorage(GL_RENDERBUFFER, format, width, height);
        }
    }

    private void DeleteTarget(GlInterface gl)
    {
        if (_fbo == 0)
        {
            return;
        }

        gl.DeleteFramebuffer(_fbo);
        gl.DeleteRenderbuffer(_colorBuffer);
        gl.DeleteRenderbuffer(_depthBuffer);
        _fbo = _colorBuffer = _depthBuffer = 0;
    }

    private const string VertexSource = """
        in vec3 aPos;
        in float aShade;
        in vec2 aUv;
        in float aEdge;
        in vec4 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        out vec2 vUv;
        out float vEdge;
        void main()
        {
            gl_Position = uMvp * vec4(aPos, 1.0);
            vColor = aColor.rgb * aShade;
            vUv = aUv;
            vEdge = aEdge;
        }
        """;

    // A face darkens over its last pixel or so toward each border: crisp
    // outlines at any zoom, for the cost of one fwidth.
    private const string FragmentSource = """
        in vec3 vColor;
        in vec2 vUv;
        in float vEdge;
        out vec4 fragColor;
        void main()
        {
            vec2 w = max(fwidth(vUv), vec2(1e-6));
            vec2 d = min(vUv, vec2(1.0) - vUv) / w;
            float edge = 1.0 - clamp(min(d.x, d.y) - 0.25, 0.0, 1.0);
            fragColor = vec4(vColor * (1.0 - 0.32 * edge * vEdge), 1.0);
        }
        """;
}
