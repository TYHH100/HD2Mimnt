using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public sealed class ModelPreviewViewport : OpenGlControlBase
{
    public static readonly StyledProperty<ModelPreviewScene?> SceneProperty =
        AvaloniaProperty.Register<ModelPreviewViewport, ModelPreviewScene?>(nameof(Scene));
    public static readonly StyledProperty<bool> WireframeProperty =
        AvaloniaProperty.Register<ModelPreviewViewport, bool>(nameof(Wireframe));
    public static readonly StyledProperty<bool> UseTexturesProperty =
        AvaloniaProperty.Register<ModelPreviewViewport, bool>(nameof(UseTextures), true);
    public static readonly StyledProperty<bool> UseAlphaProperty =
        AvaloniaProperty.Register<ModelPreviewViewport, bool>(nameof(UseAlpha));
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<ModelPreviewViewport, bool>(nameof(ShowGrid), true);

    private const float FieldOfView = MathF.PI / 4;
    private const float DefaultYaw = MathF.PI + 0.45f;
    private const float DefaultPitch = 0.22f;
    private readonly List<GpuMesh> _meshes = [];
    private readonly Dictionary<ModelPreviewMesh, GpuMesh> _meshCache = [];
    private readonly Dictionary<ulong, GpuTexture> _textures = [];
    private IReadOnlyDictionary<ulong, TexturePreviewData>? _uploadedTextures;
    private GlApi? _api;
    private GpuMesh? _grid;
    private int _program;
    private int _depthBuffer;
    private int _depthWidth;
    private int _depthHeight;
    private int _modelLocation;
    private int _viewLocation;
    private int _projectionLocation;
    private int _normalLocation;
    private int _texturedLocation;
    private int _alphaPassLocation;
    private int _unlitLocation;
    private int _emissiveTexturedLocation;
    private int _colorLocation;
    private bool _sceneDirty = true;
    private bool _failed;
    private bool _cameraFitted;
    private bool _resetPending;
    private Vector3 _boundsCenter;
    private float _boundsRadius = 1;
    private float _groundY;
    private Matrix4x4 _model = Matrix4x4.Identity;
    private Matrix4x4 _normal = Matrix4x4.Identity;
    private Matrix4x4 _gridModel = Matrix4x4.Identity;
    private Vector3 _target;
    private float _distance = 4;
    private float _yaw = DefaultYaw;
    private float _pitch = DefaultPitch;
    private IPointer? _dragPointer;
    private Point _lastPointerPosition;
    private bool _panning;

    public ModelPreviewScene? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public bool Wireframe { get => GetValue(WireframeProperty); set => SetValue(WireframeProperty, value); }
    public bool UseTextures { get => GetValue(UseTexturesProperty); set => SetValue(UseTexturesProperty, value); }
    public bool UseAlpha { get => GetValue(UseAlphaProperty); set => SetValue(UseAlphaProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public event Action<string>? RenderFailed;

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        base.Render(context);
        // 组合器中的 GL 表面不生成控件命中区域。
        context.FillRectangle(Avalonia.Media.Brushes.Transparent, new Rect(Bounds.Size));
    }

    public void ResetCamera()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ResetCamera);
            return;
        }

        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        _resetPending = true;
        if (!_sceneDirty && _cameraFitted)
        {
            FitCamera();
            _resetPending = false;
        }
        RequestNextFrameRendering();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            _sceneDirty = true;
            _failed = false;
            if (Scene is null)
                _cameraFitted = false;
            RequestNextFrameRendering();
        }
        else if (change.Property == WireframeProperty || change.Property == UseTexturesProperty ||
                 change.Property == UseAlphaProperty || change.Property == ShowGridProperty ||
                 change.Property == BoundsProperty)
        {
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        ForgetGpuResources();
        _failed = false;
        _sceneDirty = true;
        try
        {
            if (GlVersion.Major < 3)
                throw new NotSupportedException(gl.Version);
            _api = new GlApi(gl);
        }
        catch (Exception ex)
        {
            _failed = true;
            ReportFailure(ex);
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_api is not { } api)
            return;

        GlState? state = null;
        var attachedDepth = false;
        try
        {
            state = new GlState(gl, api);
            gl.BindFramebuffer(Gl.Framebuffer, fb);
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
            gl.Viewport(0, 0, width, height);
            gl.Disable(Gl.ScissorTest);
            gl.Disable(Gl.StencilTest);
            gl.Disable(Gl.CullFace);
            gl.Disable(Gl.Blend);
            gl.Disable(Gl.RasterizerDiscard);
            gl.Disable(Gl.PolygonOffsetFill);
            api.ColorMask(1, 1, 1, 1);
            api.LineWidth(1);
            api.BlendEquationSeparate(Gl.FuncAdd, Gl.FuncAdd);
            gl.DepthMask(1);
            gl.ClearColor(0, 0, 0, 0);
            gl.Clear(Gl.ColorBufferBit);

            if (Scene is not { } scene)
            {
                DeleteGpuResources(gl);
                _sceneDirty = false;
                return;
            }
            if (_failed)
                return;

            if (_program == 0)
                CreateProgram(gl, api);
            if (_sceneDirty)
            {
                UploadScene(gl, api, scene);
                _sceneDirty = false;
            }
            if (_meshes.Count == 0)
                return;

            attachedDepth = AttachDepthBuffer(gl, api, width, height);
            if (gl.CheckFramebufferStatus(Gl.Framebuffer) != Gl.FramebufferComplete)
                throw new InvalidOperationException(nameof(Gl.FramebufferComplete));
            gl.Enable(Gl.DepthTest);
            gl.DepthFunc(Gl.Lequal);
            gl.ClearDepth(1);
            gl.Clear(Gl.DepthBufferBit);

            var eye = _target + OrbitDirection() * _distance;
            var view = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY);
            var centerDepth = -Vector3.Transform(_boundsCenter, view).Z;
            var near = MathF.Max(_boundsRadius * 0.001f, centerDepth - _boundsRadius * 1.25f);
            var far = MathF.Max(near + _boundsRadius, centerDepth + _boundsRadius * 4);
            var projection = CreateProjection((float)width / height, near, far);
            gl.UseProgram(_program);
            api.SetMatrix(_viewLocation, view);
            api.SetMatrix(_projectionLocation, projection);
            gl.ActiveTexture(Gl.Texture0);

            if (ShowGrid && _grid is not null)
            {
                api.SetMatrix(_modelLocation, _gridModel);
                api.SetMatrix(_normalLocation, Matrix4x4.Identity);
                gl.Uniform1i(_texturedLocation, 0);
                gl.Uniform1i(_alphaPassLocation, 0);
                gl.Uniform1i(_unlitLocation, 1);
                api.Uniform3f(_colorLocation, 0.25f, 0.28f, 0.32f);
                DrawGeometry(gl, _grid, false, Gl.Lines);
            }

            api.SetMatrix(_modelLocation, _model);
            api.SetMatrix(_normalLocation, _normal);
            api.Uniform3f(_colorLocation, 0.72f, 0.75f, 0.8f);
            // 不透明/受光照分趟：Emissive 材质只进加色趟，Translucent 材质只进混合趟，
            // 避免它们在不透明趟里以全强度写入颜色和深度（玻璃/发光件会因此挡住内部结构）。
            foreach (var mesh in _meshes.Where(IsBasePass))
                DrawMesh(gl, mesh, 1);

            if (UseTextures)
            {
                var blended = _meshes.Where(IsBlended)
                    .OrderBy(mesh => Vector3.Transform(mesh.Center, view).Z)
                    .ToList();
                if (blended.Count > 0)
                {
                    gl.Enable(Gl.Blend);
                    api.BlendFuncSeparate(Gl.SrcAlpha, Gl.OneMinusSrcAlpha, Gl.One, Gl.OneMinusSrcAlpha);
                    gl.DepthMask(0);
                    foreach (var mesh in blended)
                        DrawMesh(gl, mesh, 2);
                }

                // 发光材质用加色混合近似游戏的 additive pass：黑色纹素贡献为零，
                // 与绘制次序无关，因此不排序。
                var emissive = _meshes.Where(IsEmissive).ToList();
                if (emissive.Count > 0)
                {
                    gl.Enable(Gl.Blend);
                    api.BlendFuncSeparate(Gl.One, Gl.One, Gl.One, Gl.One);
                    gl.DepthMask(0);
                    foreach (var mesh in emissive)
                        DrawMesh(gl, mesh, 3);
                }
            }
            api.ThrowIfError();
        }
        catch (Exception ex)
        {
            _failed = true;
            if (attachedDepth)
            {
                gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, 0);
                attachedDepth = false;
            }
            DeleteGpuResources(gl);
            ReportFailure(ex);
        }
        finally
        {
            if (attachedDepth)
                gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, 0);
            state?.Restore(gl, api);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try
        {
            DeleteGpuResources(gl);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            ForgetGpuResources();
            _sceneDirty = true;
        }
    }

    protected override void OnOpenGlLost()
    {
        // 丢失的上下文不能再调用 GL；下次 Init 从 CPU scene 重建。
        ForgetGpuResources();
        _sceneDirty = true;
        _failed = false;
        base.OnOpenGlLost();
        Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    private void ReportFailure(Exception exception) =>
        Dispatcher.UIThread.Post(() => RenderFailed?.Invoke(exception.Message));

    private void CreateProgram(GlInterface gl, GlApi api)
    {
        var header = GlVersion.Type == GlProfileType.OpenGLES
            ? "#version 300 es\nprecision highp float;\nprecision highp int;\n"
            : "#version 130\n";
        var vertex = 0;
        var fragment = 0;
        try
        {
            vertex = gl.CreateShader(Gl.VertexShader);
            fragment = gl.CreateShader(Gl.FragmentShader);
            if (vertex == 0 || fragment == 0)
                throw new InvalidOperationException(nameof(gl.CreateShader));
            var error = gl.CompileShaderAndGetError(vertex, header + VertexShader);
            if (error is not null)
                throw new InvalidOperationException(error);
            error = gl.CompileShaderAndGetError(fragment, header + FragmentShader);
            if (error is not null)
                throw new InvalidOperationException(error);
            _program = gl.CreateProgram();
            if (_program == 0)
                throw new InvalidOperationException(nameof(gl.CreateProgram));
            gl.AttachShader(_program, vertex);
            gl.AttachShader(_program, fragment);
            gl.BindAttribLocationString(_program, 0, "aPosition");
            gl.BindAttribLocationString(_program, 1, "aNormal");
            gl.BindAttribLocationString(_program, 2, "aUv");
            error = gl.LinkProgramAndGetError(_program);
            if (error is not null)
                throw new InvalidOperationException(error);
            _modelLocation = gl.GetUniformLocationString(_program, "uModel");
            _viewLocation = gl.GetUniformLocationString(_program, "uView");
            _projectionLocation = gl.GetUniformLocationString(_program, "uProjection");
            _normalLocation = gl.GetUniformLocationString(_program, "uNormal");
            _texturedLocation = gl.GetUniformLocationString(_program, "uTextured");
            _alphaPassLocation = gl.GetUniformLocationString(_program, "uAlphaPass");
            _unlitLocation = gl.GetUniformLocationString(_program, "uUnlit");
            _emissiveTexturedLocation = gl.GetUniformLocationString(_program, "uEmissiveTextured");
            _colorLocation = gl.GetUniformLocationString(_program, "uColor");
            gl.UseProgram(_program);
            gl.Uniform1i(gl.GetUniformLocationString(_program, "uTexture"), 0);
            gl.Uniform1i(gl.GetUniformLocationString(_program, "uEmissive"), 1);
            api.ThrowIfError();
        }
        finally
        {
            if (vertex != 0) gl.DeleteShader(vertex);
            if (fragment != 0) gl.DeleteShader(fragment);
        }
    }

    private void UploadScene(GlInterface gl, GlApi api, ModelPreviewScene scene)
    {
        if (!ReferenceEquals(_uploadedTextures, scene.Textures) || _model != scene.PresentationTransform)
            DeleteSceneResources(gl);
        _uploadedTextures = scene.Textures;
        _meshes.Clear();
        _model = scene.PresentationTransform;
        if (!IsFinite(_model) || !Matrix4x4.Invert(_model, out var inverse))
            throw new InvalidDataException(nameof(scene.PresentationTransform));
        _normal = Matrix4x4.Transpose(inverse);
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var maxTextureSize = api.GetInteger(Gl.MaxTextureSize);

        foreach (var source in scene.Meshes)
        {
            if (source.Positions.Length == 0 || source.TriangleIndices.Length == 0)
                continue;
            if (!_meshCache.TryGetValue(source, out var mesh))
            {
                var vertices = PrepareVertices(source, out var hasUv);
                var meshMin = new Vector3(float.PositiveInfinity);
                var meshMax = new Vector3(float.NegativeInfinity);
                // 未引用顶点不应撑大相机适配范围。
                foreach (var index in source.TriangleIndices)
                {
                    var point = Vector3.Transform(ReadVector(source.Positions, index), _model);
                    if (!IsFinite(point)) throw new InvalidDataException(nameof(source.Positions));
                    meshMin = Vector3.Min(meshMin, point);
                    meshMax = Vector3.Max(meshMax, point);
                }
                mesh = UploadGeometry(gl, vertices, source.TriangleIndices, true);
                _meshCache.Add(source, mesh);
                mesh.BoundsMin = meshMin;
                mesh.BoundsMax = meshMax;
                mesh.Center = meshMin * 0.5f + meshMax * 0.5f;
                mesh.RenderClass = source.RenderClass;
                if (hasUv)
                {
                    mesh.Texture = source.ColorTextureId is { } colorTextureId
                        ? ResolveTexture(gl, api, scene, colorTextureId, maxTextureSize)
                        : null;
                    mesh.EmissiveTexture = source.MaterialTextures
                        .Get(ModelPreviewTextureRole.Emissive)
                        .Select(textureId => ResolveTexture(gl, api, scene, textureId, maxTextureSize))
                        .FirstOrDefault(static texture => texture is not null);
                }
            }
            _meshes.Add(mesh);
            min = Vector3.Min(min, mesh.BoundsMin);
            max = Vector3.Max(max, mesh.BoundsMax);
        }

        if (_meshes.Count == 0)
            return;
        var center = min * 0.5f + max * 0.5f;
        var radius = MathF.Max((max * 0.5f - min * 0.5f).Length(), 0.001f);
        if (!IsFinite(center) || !float.IsFinite(radius))
            throw new InvalidDataException(nameof(scene.Meshes));
        var boundsChanged = Vector3.Distance(center, _boundsCenter) > radius * 0.001f ||
                            MathF.Abs(radius - _boundsRadius) > radius * 0.001f;
        _boundsCenter = center;
        _boundsRadius = radius;
        _groundY = min.Y - radius * 0.005f;
        _gridModel = Matrix4x4.CreateScale(radius * 1.5f) *
                     Matrix4x4.CreateTranslation(center.X, _groundY, center.Z);
        if (_grid is null) UploadGrid(gl);
        api.ThrowIfError();
        if (!_cameraFitted || boundsChanged || _resetPending)
            FitCamera();
        _cameraFitted = true;
        _resetPending = false;
    }

    private static float[] PrepareVertices(ModelPreviewMesh mesh, out bool hasUv)
    {
        var positions = mesh.Positions;
        var indices = mesh.TriangleIndices;
        if (positions.Length % 3 != 0 || indices.Length % 3 != 0)
            throw new InvalidDataException(nameof(mesh.TriangleIndices));
        var count = positions.Length / 3;
        foreach (var index in indices)
            if ((uint)index >= (uint)count)
                throw new InvalidDataException(nameof(mesh.TriangleIndices));
        var normals = mesh.Normals;
        var generated = normals is null || normals.Length != positions.Length;
        if (generated)
        {
            normals = new float[positions.Length];
            for (var i = 0; i < indices.Length; i += 3)
            {
                var a = ReadVector(positions, indices[i]);
                var b = ReadVector(positions, indices[i + 1]);
                var c = ReadVector(positions, indices[i + 2]);
                var normal = Vector3.Cross(b - a, c - a);
                for (var j = 0; j < 3; j++)
                {
                    var offset = indices[i + j] * 3;
                    normals[offset] += normal.X;
                    normals[offset + 1] += normal.Y;
                    normals[offset + 2] += normal.Z;
                }
            }
        }
        hasUv = mesh.TextureCoordinates is { } uv && uv.Length == checked(count * 2);
        var data = new float[checked(count * 8)];
        for (var i = 0; i < count; i++)
        {
            var position = ReadVector(positions, i);
            if (!IsFinite(position))
                throw new InvalidDataException(nameof(mesh.Positions));
            var normal = ReadVector(normals!, i);
            var length = normal.Length();
            normal = IsFinite(normal) && float.IsFinite(length) && length > 1e-12f
                ? normal / length : Vector3.UnitY;
            var offset = i * 8;
            data[offset] = position.X;
            data[offset + 1] = position.Y;
            data[offset + 2] = position.Z;
            data[offset + 3] = normal.X;
            data[offset + 4] = normal.Y;
            data[offset + 5] = normal.Z;
            if (hasUv)
            {
                var u = mesh.TextureCoordinates![i * 2];
                var v = mesh.TextureCoordinates[i * 2 + 1];
                if (!float.IsFinite(u) || !float.IsFinite(v))
                    throw new InvalidDataException(nameof(mesh.TextureCoordinates));
                data[offset + 6] = u;
                data[offset + 7] = v;
            }
        }
        return data;
    }

    private static GpuMesh UploadGeometry(GlInterface gl, float[] vertices, int[] indices, bool edges)
    {
        var mesh = new GpuMesh { IndexCount = indices.Length };
        try
        {
            mesh.VertexArray = gl.GenVertexArray();
            mesh.VertexBuffer = gl.GenBuffer();
            mesh.IndexBuffer = gl.GenBuffer();
            if (mesh.VertexArray == 0 || mesh.VertexBuffer == 0 || mesh.IndexBuffer == 0)
                throw new InvalidOperationException(nameof(UploadGeometry));
            gl.BindVertexArray(mesh.VertexArray);
            UploadBuffer(gl, Gl.ArrayBuffer, mesh.VertexBuffer, vertices, sizeof(float));
            UploadBuffer(gl, Gl.ElementArrayBuffer, mesh.IndexBuffer, indices, sizeof(int));
            gl.VertexAttribPointer(0, 3, Gl.Float, 0, 8 * sizeof(float), 0);
            gl.VertexAttribPointer(1, 3, Gl.Float, 0, 8 * sizeof(float), 3 * sizeof(float));
            gl.VertexAttribPointer(2, 2, Gl.Float, 0, 8 * sizeof(float), 6 * sizeof(float));
            gl.EnableVertexAttribArray(0);
            gl.EnableVertexAttribArray(1);
            gl.EnableVertexAttribArray(2);
            if (edges)
            {
                var lines = new int[checked(indices.Length * 2)];
                for (var i = 0; i < indices.Length; i += 3)
                {
                    var offset = i * 2;
                    lines[offset] = indices[i];
                    lines[offset + 1] = indices[i + 1];
                    lines[offset + 2] = indices[i + 1];
                    lines[offset + 3] = indices[i + 2];
                    lines[offset + 4] = indices[i + 2];
                    lines[offset + 5] = indices[i];
                }
                mesh.EdgeBuffer = gl.GenBuffer();
                if (mesh.EdgeBuffer == 0)
                    throw new InvalidOperationException(nameof(UploadGeometry));
                mesh.EdgeCount = lines.Length;
                UploadBuffer(gl, Gl.ElementArrayBuffer, mesh.EdgeBuffer, lines, sizeof(int));
            }
            return mesh;
        }
        catch
        {
            DeleteGeometry(gl, mesh);
            throw;
        }
    }

    private static void UploadBuffer(GlInterface gl, int target, int buffer, Array data, int elementSize)
    {
        gl.BindBuffer(target, buffer);
        WithPinned(data, pointer => gl.BufferData(target, checked((nint)data.Length * elementSize), pointer, Gl.StaticDraw));
    }

    /// <summary>按 ID 解析并上传场景贴图，超限或缺失时返回 null（跳过比整场失败更安全）。</summary>
    private GpuTexture? ResolveTexture(GlInterface gl, GlApi api, ModelPreviewScene scene, ulong textureId, int maxTextureSize)
    {
        if (!scene.Textures.TryGetValue(textureId, out var texture) || texture.BgraPixels is null)
            return null;
        // 原始分辨率可能超过 GPU 上限，跳过该贴图比让整场渲染失败更安全。
        if (texture.Width > maxTextureSize || texture.Height > maxTextureSize)
        {
            System.Diagnostics.Trace.TraceWarning("Texture {0:x16} {1}x{2} exceeds max texture size {3}; skipped.",
                textureId, texture.Width, texture.Height, maxTextureSize);
            return null;
        }
        if (!_textures.TryGetValue(textureId, out var gpuTexture))
        {
            gpuTexture = UploadTexture(gl, api, texture, maxTextureSize);
            _textures.Add(textureId, gpuTexture);
        }
        return gpuTexture;
    }

    private static GpuTexture UploadTexture(GlInterface gl, GlApi api, TexturePreviewData texture, int maxSize)
    {
        if (texture.Width <= 0 || texture.Height <= 0 || texture.Width > maxSize || texture.Height > maxSize)
            throw new InvalidDataException(nameof(texture.Width));
        var length = checked(texture.Width * texture.Height * 4);
        var bgra = texture.BgraPixels!;
        if (bgra.Length < length)
            throw new InvalidDataException(nameof(texture.BgraPixels));
        var rgba = new byte[length];
        var hasAlpha = false;
        for (var i = 0; i < length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
            hasAlpha |= bgra[i + 3] != byte.MaxValue;
        }
        var id = gl.GenTexture();
        if (id == 0)
            throw new InvalidOperationException(nameof(gl.GenTexture));
        try
        {
            gl.ActiveTexture(Gl.Texture0);
            gl.BindTexture(Gl.Texture2D, id);
            gl.BindBuffer(Gl.PixelUnpackBuffer, 0);
            api.PixelStorei(Gl.UnpackAlignment, 1);
            api.PixelStorei(Gl.UnpackRowLength, 0);
            api.PixelStorei(Gl.UnpackSkipRows, 0);
            api.PixelStorei(Gl.UnpackSkipPixels, 0);
            gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.Linear);
            gl.TexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Linear);
            gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapS, Gl.Repeat);
            gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapT, Gl.Repeat);
            WithPinned(rgba, pointer => gl.TexImage2D(Gl.Texture2D, 0, Gl.Rgba8, texture.Width,
                texture.Height, 0, Gl.Rgba, Gl.UnsignedByte, pointer));
            api.ThrowIfError();
            return new GpuTexture(id, hasAlpha);
        }
        catch
        {
            gl.DeleteTexture(id);
            throw;
        }
    }

    private void UploadGrid(GlInterface gl)
    {
        const int divisions = 20;
        var vertices = new float[(divisions + 1) * 4 * 8];
        var indices = new int[(divisions + 1) * 4];
        for (var i = 0; i <= divisions; i++)
        {
            var value = -1f + 2f * i / divisions;
            SetGridVertex(i * 4, new Vector3(-1, 0, value));
            SetGridVertex(i * 4 + 1, new Vector3(1, 0, value));
            SetGridVertex(i * 4 + 2, new Vector3(value, 0, -1));
            SetGridVertex(i * 4 + 3, new Vector3(value, 0, 1));
        }
        _grid = UploadGeometry(gl, vertices, indices, false);
        return;

        void SetGridVertex(int index, Vector3 position)
        {
            var offset = index * 8;
            vertices[offset] = position.X;
            vertices[offset + 1] = position.Y;
            vertices[offset + 2] = position.Z;
            vertices[offset + 4] = 1;
            indices[index] = index;
        }
    }

    private bool AttachDepthBuffer(GlInterface gl, GlApi api, int width, int height)
    {
        api.GetFramebufferAttachmentParameteriv(Gl.Framebuffer, Gl.DepthAttachment,
            Gl.FramebufferAttachmentObjectType, out var attachment);
        if (attachment != 0)
            return false;
        if (_depthBuffer == 0)
            _depthBuffer = gl.GenRenderbuffer();
        if (_depthBuffer == 0)
            throw new InvalidOperationException(nameof(gl.GenRenderbuffer));
        gl.BindRenderbuffer(Gl.Renderbuffer, _depthBuffer);
        if (_depthWidth != width || _depthHeight != height)
        {
            gl.RenderbufferStorage(Gl.Renderbuffer, Gl.DepthComponent24, width, height);
            _depthWidth = width;
            _depthHeight = height;
        }
        gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, _depthBuffer);
        return true;
    }

    /// <summary>不透明基趟：发光与半透明材质除外（纹理开关关闭时半透明材质回退到基趟）。</summary>
    private bool IsBasePass(GpuMesh mesh) =>
        !IsEmissive(mesh) && !(UseTextures && mesh.RenderClass == ModelPreviewMaterialRenderClass.Translucent);

    private bool IsEmissive(GpuMesh mesh) =>
        UseTextures && mesh.RenderClass == ModelPreviewMaterialRenderClass.Emissive;

    /// <summary>混合趟：Translucent 材质始终参与（需纹理才有实际观感），带 alpha 通道
    /// 颜色贴图的其余材质沿用旧的软边混合行为。</summary>
    private bool IsBlended(GpuMesh mesh) =>
        (UseTextures && mesh.RenderClass == ModelPreviewMaterialRenderClass.Translucent) ||
        (UseAlpha && UseTextures &&
         mesh.RenderClass != ModelPreviewMaterialRenderClass.Emissive &&
         mesh.Texture is { HasAlpha: true });

    private void DrawMesh(GlInterface gl, GpuMesh mesh, int alphaPass)
    {
        var textured = UseTextures && mesh.Texture is not null;
        gl.ActiveTexture(Gl.Texture0);
        gl.BindTexture(Gl.Texture2D, textured ? mesh.Texture!.Id : 0);
        gl.Uniform1i(_texturedLocation, textured ? 1 : 0);
        // 自发光贴图对所有渲染类别生效：护甲的局部发光件与纯发光材质共用同一条叠加路径。
        var emissiveTextured = UseTextures && mesh.EmissiveTexture is not null;
        gl.ActiveTexture(Gl.Texture1);
        gl.BindTexture(Gl.Texture2D, emissiveTextured ? mesh.EmissiveTexture!.Id : 0);
        gl.Uniform1i(_emissiveTexturedLocation, emissiveTextured ? 1 : 0);
        gl.Uniform1i(_alphaPassLocation, alphaPass);
        gl.Uniform1i(_unlitLocation, Wireframe || mesh.RenderClass != ModelPreviewMaterialRenderClass.Opaque ? 1 : 0);
        DrawGeometry(gl, mesh, Wireframe, Gl.Triangles);
    }

    private static void DrawGeometry(GlInterface gl, GpuMesh mesh, bool wireframe, int mode)
    {
        gl.BindVertexArray(mesh.VertexArray);
        gl.BindBuffer(Gl.ElementArrayBuffer, wireframe ? mesh.EdgeBuffer : mesh.IndexBuffer);
        gl.DrawElements(wireframe ? Gl.Lines : mode, wireframe ? mesh.EdgeCount : mesh.IndexCount,
            Gl.UnsignedInt, 0);
    }

    private void DeleteSceneResources(GlInterface gl)
    {
        foreach (var mesh in _meshCache.Values)
            DeleteGeometry(gl, mesh);
        _meshCache.Clear();
        _uploadedTextures = null;
        _meshes.Clear();
        if (_grid is not null)
            DeleteGeometry(gl, _grid);
        _grid = null;
        foreach (var texture in _textures.Values)
            gl.DeleteTexture(texture.Id);
        _textures.Clear();
    }

    private static void DeleteGeometry(GlInterface gl, GpuMesh mesh)
    {
        if (mesh.VertexArray != 0) gl.DeleteVertexArray(mesh.VertexArray);
        if (mesh.VertexBuffer != 0) gl.DeleteBuffer(mesh.VertexBuffer);
        if (mesh.IndexBuffer != 0) gl.DeleteBuffer(mesh.IndexBuffer);
        if (mesh.EdgeBuffer != 0) gl.DeleteBuffer(mesh.EdgeBuffer);
    }

    private void DeleteGpuResources(GlInterface gl)
    {
        DeleteSceneResources(gl);
        if (_program != 0) gl.DeleteProgram(_program);
        if (_depthBuffer != 0) gl.DeleteRenderbuffer(_depthBuffer);
        _program = _depthBuffer = _depthWidth = _depthHeight = 0;
    }

    private void ForgetGpuResources()
    {
        _meshCache.Clear();
        _uploadedTextures = null;
        _meshes.Clear();
        _textures.Clear();
        _grid = null;
        _program = _depthBuffer = _depthWidth = _depthHeight = 0;
        _api = null;
    }

    private void FitCamera()
    {
        var aspect = (float)(Math.Max(Bounds.Width, 1) / Math.Max(Bounds.Height, 1));
        var halfAngle = MathF.Min(FieldOfView / 2, MathF.Atan(MathF.Tan(FieldOfView / 2) * aspect));
        _target = _boundsCenter;
        _distance = _boundsRadius * 1.1f / MathF.Sin(MathF.Max(halfAngle, 0.01f));
    }

    private Vector3 OrbitDirection() => new(
        MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));

    private static Matrix4x4 CreateProjection(float aspect, float near, float far)
    {
        var f = 1 / MathF.Tan(FieldOfView / 2);
        // System.Numerics 的默认透视深度为 0..1；这里直接构造 OpenGL 的 -1..1 投影。
        return new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            ReleasePointer();
            ResetCamera();
            e.Handled = true;
            return;
        }
        if (_dragPointer is not null ||
            !(properties.IsLeftButtonPressed || properties.IsRightButtonPressed || properties.IsMiddleButtonPressed))
            return;
        _dragPointer = e.Pointer;
        _panning = properties.IsRightButtonPressed || properties.IsMiddleButtonPressed;
        _lastPointerPosition = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragPointer != e.Pointer)
            return;
        var point = e.GetPosition(this);
        var delta = point - _lastPointerPosition;
        _lastPointerPosition = point;
        if (_panning)
        {
            var direction = OrbitDirection();
            var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
            var up = Vector3.Cross(direction, right);
            var factor = 2 * _distance * MathF.Tan(FieldOfView / 2) / (float)Math.Max(Bounds.Height, 1);
            _target += (-right * (float)delta.X + up * (float)delta.Y) * factor;
        }
        else
        {
            _yaw = MathF.IEEERemainder(_yaw - (float)delta.X * 0.008f, MathF.Tau);
            _pitch = Math.Clamp(_pitch + (float)delta.Y * 0.008f, -1.5f, 1.5f);
        }
        e.Handled = true;
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragPointer != e.Pointer)
            return;
        ReleasePointer();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragPointer = null;
        _panning = false;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!double.IsFinite(e.Delta.Y))
            return;
        _distance = Math.Clamp(_distance * MathF.Exp((float)Math.Clamp(-e.Delta.Y * 0.12, -4, 4)),
            _boundsRadius * 0.02f, _boundsRadius * 200);
        e.Handled = true;
        RequestNextFrameRendering();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleasePointer();
        base.OnDetachedFromVisualTree(e);
    }

    private void ReleasePointer()
    {
        var pointer = _dragPointer;
        _dragPointer = null;
        _panning = false;
        pointer?.Capture(null);
    }

    private static Vector3 ReadVector(float[] values, int index) =>
        new(values[index * 3], values[index * 3 + 1], values[index * 3 + 2]);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value) =>
        IsFinite(new Vector3(value.M11, value.M12, value.M13)) && float.IsFinite(value.M14) &&
        IsFinite(new Vector3(value.M21, value.M22, value.M23)) && float.IsFinite(value.M24) &&
        IsFinite(new Vector3(value.M31, value.M32, value.M33)) && float.IsFinite(value.M34) &&
        IsFinite(new Vector3(value.M41, value.M42, value.M43)) && float.IsFinite(value.M44);

    private static void WithPinned(Array data, Action<nint> action)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { action(handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
    }

    private sealed class GpuMesh
    {
        public int VertexArray;
        public int VertexBuffer;
        public int IndexBuffer;
        public int EdgeBuffer;
        public int IndexCount;
        public int EdgeCount;
        public Vector3 Center;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public GpuTexture? Texture;
        public GpuTexture? EmissiveTexture;
        public ModelPreviewMaterialRenderClass RenderClass;
    }

    private sealed record GpuTexture(int Id, bool HasAlpha);

    private sealed class GlApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void MatrixDelegate(int location, int count, byte transpose, nint values);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void Vector3Delegate(int location, float x, float y, float z);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void IntPairDelegate(int name, int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void FloatDelegate(float value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void BlendDelegate(int sourceRgb, int destRgb, int sourceAlpha, int destAlpha);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void GetValuesDelegate(int name, nint values);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void AttachmentDelegate(int target, int attachment, int name, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate byte IsEnabledDelegate(int capability);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void ColorMaskDelegate(byte red, byte green, byte blue, byte alpha);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int GetErrorDelegate();

        public readonly MatrixDelegate UniformMatrix4fv;
        public readonly Vector3Delegate Uniform3f;
        public readonly IntPairDelegate PixelStorei;
        public readonly IntPairDelegate BlendEquationSeparate;
        public readonly FloatDelegate LineWidth;
        public readonly BlendDelegate BlendFuncSeparate;
        public readonly AttachmentDelegate GetFramebufferAttachmentParameteriv;
        public readonly IsEnabledDelegate IsEnabled;
        public readonly ColorMaskDelegate ColorMask;
        private readonly GetValuesDelegate _getIntegerv;
        private readonly GetValuesDelegate _getFloatv;
        private readonly GetErrorDelegate _getError;

        public GlApi(GlInterface gl)
        {
            UniformMatrix4fv = Load<MatrixDelegate>(gl, "glUniformMatrix4fv");
            Uniform3f = Load<Vector3Delegate>(gl, "glUniform3f");
            PixelStorei = Load<IntPairDelegate>(gl, "glPixelStorei");
            BlendEquationSeparate = Load<IntPairDelegate>(gl, "glBlendEquationSeparate");
            LineWidth = Load<FloatDelegate>(gl, "glLineWidth");
            BlendFuncSeparate = Load<BlendDelegate>(gl, "glBlendFuncSeparate");
            GetFramebufferAttachmentParameteriv = Load<AttachmentDelegate>(gl, "glGetFramebufferAttachmentParameteriv");
            IsEnabled = Load<IsEnabledDelegate>(gl, "glIsEnabled");
            ColorMask = Load<ColorMaskDelegate>(gl, "glColorMask");
            _getIntegerv = Load<GetValuesDelegate>(gl, "glGetIntegerv");
            _getFloatv = Load<GetValuesDelegate>(gl, "glGetFloatv");
            _getError = Load<GetErrorDelegate>(gl, "glGetError");
        }

        private static T Load<T>(GlInterface gl, string name) where T : Delegate
        {
            var pointer = gl.GetProcAddress(name);
            if (pointer == 0 || pointer == 1 || pointer == 2 || pointer == 3 || pointer == -1)
                throw new NotSupportedException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        public int GetInteger(int name) => GetIntegers(name, 1)[0];

        public int[] GetIntegers(int name, int count)
        {
            var values = new int[count];
            WithPinned(values, pointer => _getIntegerv(name, pointer));
            return values;
        }

        public float[] GetFloats(int name, int count)
        {
            var values = new float[count];
            WithPinned(values, pointer => _getFloatv(name, pointer));
            return values;
        }

        public void SetMatrix(int location, Matrix4x4 value)
        {
            // 行向量矩阵原样传入列主序 GL，等效于转置；GLES 要求 transpose=false。
            float[] values = [value.M11, value.M12, value.M13, value.M14,
                value.M21, value.M22, value.M23, value.M24,
                value.M31, value.M32, value.M33, value.M34,
                value.M41, value.M42, value.M43, value.M44];
            WithPinned(values, pointer => UniformMatrix4fv(location, 1, 0, pointer));
        }

        public void ThrowIfError()
        {
            var error = _getError();
            if (error != 0)
                throw new InvalidOperationException($"GL 0x{error:X4}");
        }
    }

    private sealed class GlState
    {
        private static readonly int[] Capabilities = [Gl.DepthTest, Gl.Blend, Gl.CullFace, Gl.ScissorTest,
            Gl.StencilTest, Gl.RasterizerDiscard, Gl.PolygonOffsetFill];
        private static readonly int[] PixelStoreNames = [Gl.UnpackAlignment, Gl.UnpackRowLength,
            Gl.UnpackSkipRows, Gl.UnpackSkipPixels];
        private readonly bool[] _enabled;
        private readonly int[] _pixelStore;
        private readonly int[] _viewport;
        private readonly int[] _colorMask;
        private readonly float[] _clearColor;
        private readonly float _clearDepth;
        private readonly float _lineWidth;
        private readonly int _drawFramebuffer;
        private readonly int _readFramebuffer;
        private readonly int _program;
        private readonly int _vertexArray;
        private readonly int _arrayBuffer;
        private readonly int _unpackBuffer;
        private readonly int _renderbuffer;
        private readonly int _activeTexture;
        private readonly int _texture;
        private readonly int _depthMask;
        private readonly int _depthFunc;
        private readonly int _sourceRgb;
        private readonly int _destRgb;
        private readonly int _sourceAlpha;
        private readonly int _destAlpha;
        private readonly int _equationRgb;
        private readonly int _equationAlpha;

        public GlState(GlInterface gl, GlApi api)
        {
            _drawFramebuffer = api.GetInteger(0x8CA6);
            _readFramebuffer = api.GetInteger(0x8CAA);
            _program = api.GetInteger(0x8B8D);
            _vertexArray = api.GetInteger(0x85B5);
            _arrayBuffer = api.GetInteger(0x8894);
            _unpackBuffer = api.GetInteger(0x88EF);
            _renderbuffer = api.GetInteger(0x8CA7);
            _activeTexture = api.GetInteger(0x84E0);
            _viewport = api.GetIntegers(0x0BA2, 4);
            _colorMask = api.GetIntegers(0x0C23, 4);
            _clearColor = api.GetFloats(0x0C22, 4);
            _clearDepth = api.GetFloats(0x0B73, 1)[0];
            _lineWidth = api.GetFloats(0x0B21, 1)[0];
            _depthMask = api.GetInteger(0x0B72);
            _depthFunc = api.GetInteger(0x0B74);
            _sourceRgb = api.GetInteger(0x80C9);
            _destRgb = api.GetInteger(0x80C8);
            _sourceAlpha = api.GetInteger(0x80CB);
            _destAlpha = api.GetInteger(0x80CA);
            _equationRgb = api.GetInteger(0x8009);
            _equationAlpha = api.GetInteger(0x883D);
            _enabled = Capabilities.Select(capability => api.IsEnabled(capability) != 0).ToArray();
            _pixelStore = PixelStoreNames.Select(api.GetInteger).ToArray();
            gl.ActiveTexture(Gl.Texture0);
            _texture = api.GetInteger(0x8069);
        }

        public void Restore(GlInterface gl, GlApi api)
        {
            gl.BindVertexArray(_vertexArray);
            gl.BindBuffer(Gl.ArrayBuffer, _arrayBuffer);
            gl.BindBuffer(Gl.PixelUnpackBuffer, _unpackBuffer);
            gl.BindRenderbuffer(Gl.Renderbuffer, _renderbuffer);
            gl.ActiveTexture(Gl.Texture0);
            gl.BindTexture(Gl.Texture2D, _texture);
            gl.ActiveTexture(_activeTexture);
            gl.UseProgram(_program);
            gl.DepthMask(_depthMask);
            gl.DepthFunc(_depthFunc);
            api.ColorMask((byte)_colorMask[0], (byte)_colorMask[1], (byte)_colorMask[2], (byte)_colorMask[3]);
            api.BlendFuncSeparate(_sourceRgb, _destRgb, _sourceAlpha, _destAlpha);
            api.BlendEquationSeparate(_equationRgb, _equationAlpha);
            api.LineWidth(_lineWidth);
            gl.ClearColor(_clearColor[0], _clearColor[1], _clearColor[2], _clearColor[3]);
            gl.ClearDepth(_clearDepth);
            for (var i = 0; i < PixelStoreNames.Length; i++)
                api.PixelStorei(PixelStoreNames[i], _pixelStore[i]);
            for (var i = 0; i < Capabilities.Length; i++)
            {
                if (_enabled[i]) gl.Enable(Capabilities[i]);
                else gl.Disable(Capabilities[i]);
            }
            gl.Viewport(_viewport[0], _viewport[1], _viewport[2], _viewport[3]);
            gl.BindFramebuffer(Gl.DrawFramebuffer, _drawFramebuffer);
            gl.BindFramebuffer(Gl.ReadFramebuffer, _readFramebuffer);
        }
    }

    private static class Gl
    {
        public const int Framebuffer = 0x8D40, Renderbuffer = 0x8D41, FramebufferComplete = 0x8CD5;
        public const int DrawFramebuffer = 0x8CA9, ReadFramebuffer = 0x8CA8, FuncAdd = 0x8006;
        public const int DepthAttachment = 0x8D00, FramebufferAttachmentObjectType = 0x8CD0, DepthComponent24 = 0x81A6;
        public const int ColorBufferBit = 0x4000, DepthBufferBit = 0x0100;
        public const int DepthTest = 0x0B71, Blend = 0x0BE2, CullFace = 0x0B44, ScissorTest = 0x0C11;
        public const int StencilTest = 0x0B90, RasterizerDiscard = 0x8C89, PolygonOffsetFill = 0x8037, Lequal = 0x0203;
        public const int ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893, PixelUnpackBuffer = 0x88EC, StaticDraw = 0x88E4;
        public const int Float = 0x1406, UnsignedInt = 0x1405, UnsignedByte = 0x1401, Triangles = 4, Lines = 1;
        public const int Texture0 = 0x84C0, Texture1 = 0x84C1, Texture2D = 0x0DE1, Rgba = 0x1908, Rgba8 = 0x8058, MaxTextureSize = 0x0D33;
        public const int TextureMinFilter = 0x2801, TextureMagFilter = 0x2800, TextureWrapS = 0x2802, TextureWrapT = 0x2803;
        public const int Linear = 0x2601, Repeat = 0x2901, UnpackAlignment = 0x0CF5, UnpackRowLength = 0x0CF2;
        public const int UnpackSkipRows = 0x0CF3, UnpackSkipPixels = 0x0CF4;
        public const int VertexShader = 0x8B31, FragmentShader = 0x8B30;
        public const int SrcAlpha = 0x0302, OneMinusSrcAlpha = 0x0303, One = 1;
    }

    private const string VertexShader = """
        in vec3 aPosition;
        in vec3 aNormal;
        in vec2 aUv;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;
        uniform mat4 uNormal;
        out vec3 vNormal;
        out vec2 vUv;
        void main()
        {
            vNormal = mat3(uNormal) * aNormal;
            vUv = aUv;
            gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShader = """
        in vec3 vNormal;
        in vec2 vUv;
        uniform sampler2D uTexture;
        uniform sampler2D uEmissive;
        uniform int uTextured;
        uniform int uEmissiveTextured;
        uniform int uAlphaPass;
        uniform int uUnlit;
        uniform vec3 uColor;
        out vec4 fragColor;
        void main()
        {
            vec4 color = uTextured != 0 ? texture(uTexture, vUv) : vec4(uColor, 1.0);
            vec3 emissive = uEmissiveTextured != 0 ? texture(uEmissive, vUv).rgb : vec3(0.0);
            if (uAlphaPass == 1 && color.a < 0.999) discard;
            if (uAlphaPass == 2 && (color.a >= 0.999 || color.a <= 0.001)) discard;
            vec3 normal = vNormal / max(length(vNormal), 0.00001);
            float key = abs(dot(normal, normalize(vec3(0.4, 0.8, 0.6))));
            float fill = abs(dot(normal, normalize(vec3(-0.7, 0.25, -0.4))));
            float light = uUnlit != 0 ? 1.0 : 0.32 + 0.5 * key + 0.18 * fill;
            fragColor = vec4(color.rgb * light + emissive, uAlphaPass == 2 ? color.a : 1.0);
        }
        """;
}
