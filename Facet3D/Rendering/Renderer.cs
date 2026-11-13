using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Windows.UI.Xaml.Controls;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3DDevice = SharpDX.Direct3D11.Device;
using D3DDevice1 = SharpDX.Direct3D11.Device1;

namespace Facet3D
{
    [StructLayout(LayoutKind.Sequential)]
    struct FrameConstants
    {
        public Matrix4x4 World;
        public Matrix4x4 ViewProj;
        public Vector4 Tint;
        public Vector4 LightDir;
        public Vector4 Params;
    }

    /// <summary>Growable dynamic vertex buffer for per-frame geometry (lines, markers, overlays).</summary>
    sealed class DynamicVB : IDisposable
    {
        readonly D3DDevice _device;
        public D3DBuffer Buffer;
        int _capacityFloats;

        public DynamicVB(D3DDevice device) { _device = device; }

        public int Upload(DeviceContext ctx, float[] data, int floatCount)
        {
            if (floatCount == 0) return 0;
            if (Buffer == null || floatCount > _capacityFloats)
            {
                if (Buffer != null) Buffer.Dispose();
                _capacityFloats = Math.Max(floatCount, Math.Max(4096, _capacityFloats * 2));
                Buffer = new D3DBuffer(_device, _capacityFloats * 4, ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
            }
            SharpDX.DataStream stream;
            ctx.MapSubresource(Buffer, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out stream);
            stream.WriteRange(data, 0, floatCount);
            ctx.UnmapSubresource(Buffer, 0);
            stream.Dispose();
            return floatCount / Renderer.FloatsPerVertex;
        }

        public void Dispose() { if (Buffer != null) { Buffer.Dispose(); Buffer = null; } }
    }

    sealed class GpuMesh : IDisposable
    {
        public Mesh MeshRef;
        public int Version = -1, MatVersion = -1;
        public Shading Shading;
        public readonly List<D3DBuffer> Buffers = new List<D3DBuffer>();
        public readonly List<int> Counts = new List<int>();

        public void Dispose()
        {
            foreach (var b in Buffers) b.Dispose();
            Buffers.Clear(); Counts.Clear();
        }
    }

    /// <summary>Simple float list that avoids List&lt;float&gt; boxing-free growth costs.</summary>
    sealed class FloatBuilder
    {
        public float[] Data = new float[4096];
        public int Count;

        public void Clear() { Count = 0; }

        void Ensure(int extra)
        {
            if (Count + extra <= Data.Length) return;
            int n = Data.Length * 2;
            while (n < Count + extra) n *= 2;
            Array.Resize(ref Data, n);
        }

        public void Vertex(Vector3 p, Vector3 n, Vector4 c)
        {
            Ensure(10);
            var d = Data; int i = Count;
            d[i] = p.X; d[i + 1] = p.Y; d[i + 2] = p.Z;
            d[i + 3] = n.X; d[i + 4] = n.Y; d[i + 5] = n.Z;
            d[i + 6] = c.X; d[i + 7] = c.Y; d[i + 8] = c.Z; d[i + 9] = c.W;
            Count += 10;
        }

        public void Line(Vector3 a, Vector3 b, Vector4 c)
        {
            Vertex(a, Vector3.Zero, c);
            Vertex(b, Vector3.Zero, c);
        }
    }

    public sealed class Renderer : IDisposable
    {
        public const int FloatsPerVertex = 10;
        const int Stride = FloatsPerVertex * 4;
        const int MaxVertsPerBuffer = 900000;

        D3DDevice1 _device;
        DeviceContext _ctx;
        SwapChain1 _swapChain;
        RenderTargetView _rtv;
        Texture2D _depthTex;
        DepthStencilView _dsv;
        VertexShader _vs;
        PixelShader _ps;
        InputLayout _layout;
        D3DBuffer _cb;
        RasterizerState _rsMesh, _rsFlat;
        DepthStencilState _dsDefault, _dsNoWrite, _dsOff;
        BlendState _blendAlpha, _blendOpaque;
        D3DBuffer _gridVB;
        int _gridCount;
        DynamicVB _lines, _overlay, _markers;
        readonly FloatBuilder _fb = new FloatBuilder();
        readonly Dictionary<SceneObject, GpuMesh> _cache = new Dictionary<SceneObject, GpuMesh>();

        // Edge cache for the active object in edit mode.
        Mesh _edgeMesh; int _edgeVersion = -1; List<long> _edges;

        int _width, _height;
        float _scaleX = 1, _scaleY = 1;
        SwapChainPanel _panel;

        public bool IsReady { get { return _rtv != null; } }
        public string Status = "";

        public void Initialize(SwapChainPanel panel)
        {
            _panel = panel;
            using (var dev = new D3DDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0))
            {
                _device = dev.QueryInterface<D3DDevice1>();
            }
            _ctx = _device.ImmediateContext;
            Status = "Direct3D feature level " + _device.FeatureLevel;

            LoadShaders();
            _cb = new D3DBuffer(_device, Marshal.SizeOf(typeof(FrameConstants)), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

            var rs = RasterizerStateDescription.Default();
            rs.CullMode = CullMode.None;
            rs.DepthBias = 40;
            rs.SlopeScaledDepthBias = 1.5f;
            rs.DepthBiasClamp = 0f;
            _rsMesh = new RasterizerState(_device, rs);
            var rs2 = RasterizerStateDescription.Default();
            rs2.CullMode = CullMode.None;
            _rsFlat = new RasterizerState(_device, rs2);

            var ds = DepthStencilStateDescription.Default();
            ds.DepthComparison = Comparison.LessEqual;
            _dsDefault = new DepthStencilState(_device, ds);
            ds.DepthWriteMask = DepthWriteMask.Zero;
            _dsNoWrite = new DepthStencilState(_device, ds);
            var ds3 = DepthStencilStateDescription.Default();
            ds3.IsDepthEnabled = false;
            ds3.DepthWriteMask = DepthWriteMask.Zero;
            _dsOff = new DepthStencilState(_device, ds3);

            var bd = BlendStateDescription.Default();
            _blendOpaque = new BlendState(_device, bd);
            bd.RenderTarget[0].IsBlendEnabled = true;
            bd.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
            bd.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
            bd.RenderTarget[0].BlendOperation = BlendOperation.Add;
            bd.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
            bd.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
            bd.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
            bd.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
            _blendAlpha = new BlendState(_device, bd);

            _lines = new DynamicVB(_device);
            _overlay = new DynamicVB(_device);
            _markers = new DynamicVB(_device);
            BuildGrid();
        }

        void LoadShaders()
        {
            string root = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            byte[] vsCode = TryRead(Path.Combine(root, "Shaders", "Scene_vs.cso"));
            byte[] psCode = TryRead(Path.Combine(root, "Shaders", "Scene_ps.cso"));
            if (vsCode == null || psCode == null)
            {
                // Fallback: compile at runtime (needs d3dcompiler_47, present on Windows; precompiled .cso is preferred on Xbox).
                string src = File.ReadAllText(Path.Combine(root, "Shaders", "Scene.hlsl"));
                using (var r = SharpDX.D3DCompiler.ShaderBytecode.Compile(src, "VS", "vs_4_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3))
                    vsCode = r.Bytecode.Data;
                using (var r = SharpDX.D3DCompiler.ShaderBytecode.Compile(src, "PS", "ps_4_0", SharpDX.D3DCompiler.ShaderFlags.OptimizationLevel3))
                    psCode = r.Bytecode.Data;
                Status += " (runtime shaders)";
            }
            _vs = new VertexShader(_device, vsCode);
            _ps = new PixelShader(_device, psCode);
            _layout = new InputLayout(_device, vsCode, new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 24, 0),
            });
        }

        static byte[] TryRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch { return null; }
        }

        void BuildGrid()
        {
            _fb.Clear();
            const int n = 20;
            var minor = new Vector4(0.30f, 0.31f, 0.34f, 1f);
            var major = new Vector4(0.40f, 0.41f, 0.45f, 1f);
            for (int i = -n; i <= n; i++)
            {
                var cx = i == 0 ? new Vector4(0.25f, 0.45f, 0.95f, 1f) : (i % 5 == 0 ? major : minor);
                var cz = i == 0 ? new Vector4(0.90f, 0.28f, 0.30f, 1f) : (i % 5 == 0 ? major : minor);
                _fb.Line(new Vector3(i, 0, -n), new Vector3(i, 0, n), cx);   // runs along Z (blue when x=0)
                _fb.Line(new Vector3(-n, 0, i), new Vector3(n, 0, i), cz);   // runs along X (red when z=0)
            }
            var arr = new float[_fb.Count];
            Array.Copy(_fb.Data, arr, _fb.Count);
            _gridVB = D3DBuffer.Create(_device, BindFlags.VertexBuffer, arr);
            _gridCount = _fb.Count / FloatsPerVertex;
        }

        public void Resize(double w, double h, float sx, float sy)
        {
            if (_device == null) return;
            int pw = Math.Max(8, (int)Math.Round(w * sx));
            int ph = Math.Max(8, (int)Math.Round(h * sy));
            if (pw == _width && ph == _height && _swapChain != null && sx == _scaleX && sy == _scaleY) return;
            _width = pw; _height = ph; _scaleX = sx; _scaleY = sy;

            Release(ref _rtv); Release(ref _dsv); Release(ref _depthTex);
            _ctx.ClearState();
            _ctx.Flush();

            if (_swapChain == null)
            {
                var desc = new SwapChainDescription1
                {
                    Width = _width,
                    Height = _height,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = SwapChainFlags.None
                };
                using (var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device2>())
                using (var adapter = dxgiDevice.Adapter)
                using (var factory = adapter.GetParent<Factory2>())
                {
                    _swapChain = new SwapChain1(factory, _device, ref desc, null);
                }
                using (var native = SharpDX.ComObject.As<ISwapChainPanelNative>(_panel))
                    native.SwapChain = _swapChain;
            }
            else
            {
                _swapChain.ResizeBuffers(2, _width, _height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            }
            using (var sc2 = _swapChain.QueryInterface<SwapChain2>())
                sc2.MatrixTransform = new RawMatrix3x2 { M11 = 1f / sx, M22 = 1f / sy };

            using (var back = _swapChain.GetBackBuffer<Texture2D>(0))
                _rtv = new RenderTargetView(_device, back);
            _depthTex = new Texture2D(_device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D24_UNorm_S8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });
            _dsv = new DepthStencilView(_device, _depthTex);
        }

        static void Release<T>(ref T obj) where T : class, IDisposable
        {
            if (obj != null) { obj.Dispose(); obj = null; }
        }

        void SetConstants(Matrix4x4 world, Matrix4x4 viewProj, Vector4 tint, bool lit, float alpha, Vector3 lightDir)
        {
            var c = new FrameConstants
            {
                World = world,
                ViewProj = viewProj,
                Tint = tint,
                LightDir = new Vector4(lightDir, lit ? 1f : 0f),
                Params = new Vector4(alpha, 0, 0, 0)
            };
            _ctx.UpdateSubresource(ref c, _cb);
        }

        void Draw(D3DBuffer vb, int count, PrimitiveTopology topo)
        {
            if (vb == null || count <= 0) return;
            _ctx.InputAssembler.PrimitiveTopology = topo;
            _ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vb, Stride, 0));
            _ctx.Draw(count, 0);
        }

        public void Render(EditorController ed)
        {
            if (_rtv == null) return;
            var scene = ed.Scene;
            var cam = ed.Camera;

            _ctx.OutputMerger.SetRenderTargets(_dsv, _rtv);
            _ctx.ClearRenderTargetView(_rtv, new RawColor4(0.15f, 0.16f, 0.18f, 1f));
            _ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1f, 0);
            _ctx.Rasterizer.SetViewport(0, 0, _width, _height, 0f, 1f);
            _ctx.InputAssembler.InputLayout = _layout;
            _ctx.VertexShader.Set(_vs);
            _ctx.PixelShader.Set(_ps);
            _ctx.VertexShader.SetConstantBuffer(0, _cb);
            _ctx.PixelShader.SetConstantBuffer(0, _cb);

            float aspect = _width / (float)_height;
            var viewProj = cam.View * cam.Projection(aspect);
            var light = Vector3.Normalize(cam.Forward * 0.7f + cam.Right * 0.35f - cam.Up * 0.45f);
            var noTint = Vector4.Zero;

            // Grid.
            _ctx.Rasterizer.State = _rsFlat;
            _ctx.OutputMerger.SetDepthStencilState(_dsNoWrite);
            _ctx.OutputMerger.SetBlendState(_blendOpaque);
            if (ed.ShowGrid)
            {
                SetConstants(Matrix4x4.Identity, viewProj, noTint, false, 1f, light);
                Draw(_gridVB, _gridCount, PrimitiveTopology.LineList);
            }

            // Meshes.
            _ctx.Rasterizer.State = _rsMesh;
            _ctx.OutputMerger.SetDepthStencilState(_dsDefault);
            var alive = new HashSet<SceneObject>();
            foreach (var o in scene.Objects)
            {
                alive.Add(o);
                if (!o.Visible) continue;
                var gm = GetGpuMesh(o, scene);
                bool selected = ed.Sel.Objects.Contains(o) && ed.Mode == EditMode.Object;
                bool active = o == ed.Sel.Active && ed.Mode == EditMode.Object;
                var tint = selected ? new Vector4(1f, active ? 0.62f : 0.5f, 0.1f, active ? 0.28f : 0.18f) : noTint;
                if (ed.Mode != EditMode.Object && o != ed.Sel.Active) tint = new Vector4(0.15f, 0.16f, 0.18f, 0.35f);
                SetConstants(o.World, viewProj, tint, true, 1f, light);
                for (int i = 0; i < gm.Buffers.Count; i++) Draw(gm.Buffers[i], gm.Counts[i], PrimitiveTopology.TriangleList);
            }
            // Drop GPU data for deleted objects.
            if (_cache.Count > alive.Count)
            {
                var dead = new List<SceneObject>();
                foreach (var k in _cache.Keys) if (!alive.Contains(k)) dead.Add(k);
                foreach (var k in dead) { _cache[k].Dispose(); _cache.Remove(k); }
            }

            _ctx.Rasterizer.State = _rsFlat;
            var act = ed.Sel.Active;
            if (ed.Mode != EditMode.Object && act != null && act.Visible) DrawEditOverlay(ed, act, viewProj, light);
            else if (ed.Mode == EditMode.Object)
            {
                // Outline-ish: draw wire edges of selected objects faintly when the mesh is small.
                _fb.Clear();
                foreach (var o in ed.Sel.Objects)
                {
                    if (!o.Visible || o.Mesh.Positions.Count > 60000) continue;
                    var w = o.World;
                    var col = o == ed.Sel.Active ? new Vector4(1f, 0.7f, 0.2f, 0.9f) : new Vector4(0.9f, 0.5f, 0.15f, 0.7f);
                    foreach (var e in o.Mesh.UniqueEdges())
                    {
                        int a, b; Mesh.EdgeFromKey(e, out a, out b);
                        if (a >= o.Mesh.Positions.Count || b >= o.Mesh.Positions.Count) continue;
                        _fb.Line(Vector3.Transform(o.Mesh.Positions[a], w), Vector3.Transform(o.Mesh.Positions[b], w), col);
                    }
                }
                if (_fb.Count > 0)
                {
                    _ctx.OutputMerger.SetDepthStencilState(_dsNoWrite);
                    _ctx.OutputMerger.SetBlendState(_blendAlpha);
                    int n = _lines.Upload(_ctx, _fb.Data, _fb.Count);
                    SetConstants(Matrix4x4.Identity, viewProj, noTint, false, 1f, light);
                    Draw(_lines.Buffer, n, PrimitiveTopology.LineList);
                }
            }

            DrawGizmo(ed, viewProj, light);

            _swapChain.Present(1, PresentFlags.None);
        }

        GpuMesh GetGpuMesh(SceneObject o, Scene scene)
        {
            GpuMesh gm;
            if (!_cache.TryGetValue(o, out gm)) { gm = new GpuMesh(); _cache[o] = gm; }
            if (gm.MeshRef == o.Mesh && gm.Version == o.Mesh.Version && gm.MatVersion == scene.MaterialsVersion && gm.Shading == o.Shading) return gm;
            gm.Dispose();
            gm.MeshRef = o.Mesh; gm.Version = o.Mesh.Version; gm.MatVersion = scene.MaterialsVersion; gm.Shading = o.Shading;

            var m = o.Mesh;
            Vector3[] smooth = o.Shading == Shading.Smooth ? m.ComputeVertexNormals() : null;
            _fb.Clear();
            Action flush = () =>
            {
                if (_fb.Count == 0) return;
                var arr = new float[_fb.Count];
                Array.Copy(_fb.Data, arr, _fb.Count);
                gm.Buffers.Add(D3DBuffer.Create(_device, BindFlags.VertexBuffer, arr, 0, ResourceUsage.Immutable));
                gm.Counts.Add(_fb.Count / FloatsPerVertex);
                _fb.Clear();
            };
            foreach (var f in m.Faces)
            {
                if (!m.IsValidFace(f)) continue;
                var mc = scene.MaterialAt(f.Material).Color;
                mc.W = 1f;
                var flat = m.FaceNormal(f);
                for (int k = 1; k < f.V.Length - 1; k++)
                {
                    Corner(m, f, 0, o.Shading, smooth, flat, mc);
                    Corner(m, f, k, o.Shading, smooth, flat, mc);
                    Corner(m, f, k + 1, o.Shading, smooth, flat, mc);
                }
                if (_fb.Count >= MaxVertsPerBuffer * FloatsPerVertex) flush();
            }
            flush();
            return gm;
        }

        void Corner(Mesh m, Face f, int k, Shading shading, Vector3[] smooth, Vector3 flat, Vector4 mc)
        {
            int v = f.V[k];
            var n = FmtUtil.CornerNormal(m, f, k, shading, smooth, flat);
            var c = mc;
            if (m.Colors != null && v < m.Colors.Count) c *= m.Colors[v];
            c.W = 1f;
            _fb.Vertex(m.Positions[v], n, c);
        }

        void DrawEditOverlay(EditorController ed, SceneObject o, Matrix4x4 viewProj, Vector3 light)
        {
            var m = o.Mesh;
            var world = o.World;
            var orange = new Vector4(1f, 0.62f, 0.12f, 1f);

            // Selected faces (translucent fill).
            if (ed.Mode == EditMode.Face && ed.Sel.Faces.Count > 0)
            {
                _fb.Clear();
                var fill = new Vector4(1f, 0.55f, 0.1f, 0.45f);
                foreach (int fi in ed.Sel.Faces)
                {
                    if (fi < 0 || fi >= m.Faces.Count) continue;
                    var f = m.Faces[fi];
                    if (!m.IsValidFace(f)) continue;
                    for (int k = 1; k < f.V.Length - 1; k++)
                    {
                        _fb.Vertex(m.Positions[f.V[0]], Vector3.Zero, fill);
                        _fb.Vertex(m.Positions[f.V[k]], Vector3.Zero, fill);
                        _fb.Vertex(m.Positions[f.V[k + 1]], Vector3.Zero, fill);
                    }
                }
                _ctx.OutputMerger.SetDepthStencilState(_dsNoWrite);
                _ctx.OutputMerger.SetBlendState(_blendAlpha);
                int n = _overlay.Upload(_ctx, _fb.Data, _fb.Count);
                SetConstants(world, viewProj, Vector4.Zero, false, 1f, light);
                Draw(_overlay.Buffer, n, PrimitiveTopology.TriangleList);
            }

            // Wireframe.
            if (_edgeMesh != m || _edgeVersion != m.Version)
            {
                _edges = m.UniqueEdges();
                _edgeMesh = m; _edgeVersion = m.Version;
            }
            _fb.Clear();
            var wire = new Vector4(0.05f, 0.05f, 0.06f, 1f);
            HashSet<int> hot = ed.Mode == EditMode.Vertex ? ed.Sel.Verts : null;
            HashSet<int> faceVerts = null;
            if (ed.Mode == EditMode.Face && ed.Sel.Faces.Count > 0)
            {
                faceVerts = new HashSet<int>();
                foreach (int fi in ed.Sel.Faces) if (fi < m.Faces.Count) foreach (int v in m.Faces[fi].V) faceVerts.Add(v);
            }
            foreach (var e in _edges)
            {
                int a, b; Mesh.EdgeFromKey(e, out a, out b);
                if (a >= m.Positions.Count || b >= m.Positions.Count) continue;
                bool sel = (hot != null && hot.Contains(a) && hot.Contains(b)) || (faceVerts != null && faceVerts.Contains(a) && faceVerts.Contains(b));
                _fb.Line(m.Positions[a], m.Positions[b], sel ? orange : wire);
            }
            _ctx.OutputMerger.SetDepthStencilState(_dsNoWrite);
            _ctx.OutputMerger.SetBlendState(_blendOpaque);
            int ln = _lines.Upload(_ctx, _fb.Data, _fb.Count);
            SetConstants(world, viewProj, Vector4.Zero, false, 1f, light);
            Draw(_lines.Buffer, ln, PrimitiveTopology.LineList);

            // Vertex markers (screen-aligned quads built in clip space).
            if (ed.Mode == EditMode.Vertex)
            {
                _fb.Clear();
                var wvp = world * viewProj;
                bool all = m.Positions.Count <= 40000;
                float sx = 7f * _scaleX / _width, sy = 7f * _scaleY / _height;
                var dim = new Vector4(0.08f, 0.08f, 0.1f, 1f);
                for (int i = 0; i < m.Positions.Count; i++)
                {
                    bool s = ed.Sel.Verts.Contains(i);
                    if (!all && !s) continue;
                    var c = Vector4.Transform(new Vector4(m.Positions[i], 1f), wvp);
                    if (c.W <= 1e-5f) continue;
                    float x = c.X / c.W, y = c.Y / c.W, z = c.Z / c.W;
                    if (z < 0 || z > 1) continue;
                    float hx = s ? sx * 1.4f : sx, hy = s ? sy * 1.4f : sy;
                    var col = s ? orange : dim;
                    var p0 = new Vector3(x - hx, y - hy, 0); var p1 = new Vector3(x + hx, y - hy, 0);
                    var p2 = new Vector3(x + hx, y + hy, 0); var p3 = new Vector3(x - hx, y + hy, 0);
                    _fb.Vertex(p0, Vector3.Zero, col); _fb.Vertex(p1, Vector3.Zero, col); _fb.Vertex(p2, Vector3.Zero, col);
                    _fb.Vertex(p0, Vector3.Zero, col); _fb.Vertex(p2, Vector3.Zero, col); _fb.Vertex(p3, Vector3.Zero, col);
                }
                _ctx.OutputMerger.SetDepthStencilState(_dsOff);
                int mn = _markers.Upload(_ctx, _fb.Data, _fb.Count);
                SetConstants(Matrix4x4.Identity, Matrix4x4.Identity, Vector4.Zero, false, 1f, light);
                Draw(_markers.Buffer, mn, PrimitiveTopology.TriangleList);
            }
        }

        void DrawGizmo(EditorController ed, Matrix4x4 viewProj, Vector3 light)
        {
            Vector3 pivot;
            if (!ed.TryGetPivot(out pivot)) return;
            float len = ed.Camera.Distance * 0.14f;
            _fb.Clear();
            var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
            var cols = new[] { new Vector4(0.95f, 0.3f, 0.3f, 1f), new Vector4(0.4f, 0.9f, 0.35f, 1f), new Vector4(0.35f, 0.55f, 1f, 1f) };
            for (int i = 0; i < 3; i++)
            {
                bool locked = (int)ed.Axis == i + 1;
                bool dim = ed.Transforming && ed.Axis != AxisLock.Free && !locked;
                var c = dim ? cols[i] * new Vector4(0.45f, 0.45f, 0.45f, 1f) : cols[i];
                float l = locked ? len * 1.6f : len;
                _fb.Line(pivot, pivot + axes[i] * l, c);
                if (locked && ed.Transforming) _fb.Line(pivot - axes[i] * ed.Camera.Distance * 3f, pivot + axes[i] * ed.Camera.Distance * 3f, c * new Vector4(1, 1, 1, 0.6f));
            }
            _ctx.OutputMerger.SetDepthStencilState(_dsOff);
            _ctx.OutputMerger.SetBlendState(_blendAlpha);
            int n = _lines.Upload(_ctx, _fb.Data, _fb.Count);
            SetConstants(Matrix4x4.Identity, viewProj, Vector4.Zero, false, 1f, light);
            Draw(_lines.Buffer, n, PrimitiveTopology.LineList);
        }

        public void Dispose()
        {
            foreach (var g in _cache.Values) g.Dispose();
            _cache.Clear();
            if (_lines != null) _lines.Dispose();
            if (_overlay != null) _overlay.Dispose();
            if (_markers != null) _markers.Dispose();
            Release(ref _gridVB);
            Release(ref _rtv); Release(ref _dsv); Release(ref _depthTex);
            Release(ref _cb); Release(ref _layout); Release(ref _vs); Release(ref _ps);
            Release(ref _rsMesh); Release(ref _rsFlat);
            Release(ref _dsDefault); Release(ref _dsNoWrite); Release(ref _dsOff);
            Release(ref _blendAlpha); Release(ref _blendOpaque);
            Release(ref _swapChain);
            Release(ref _device);
        }
    }
}
