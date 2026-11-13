using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>glTF 2.0 (.gltf with embedded or external buffers, and binary .glb). Meshes, materials, node hierarchy, TRS animation.</summary>
    public sealed class GltfFormat : IModelFormat
    {
        readonly bool _binary;

        public GltfFormat(bool binary) { _binary = binary; }

        public string Name { get { return _binary ? "glTF Binary (.glb) - best for projects" : "glTF 2.0 (.gltf)"; } }
        public string[] Extensions { get { return _binary ? new[] { ".glb" } : new[] { ".gltf" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        // ------------------------------------------------------------------ import

        sealed class Ctx
        {
            public object Root;
            public List<byte[]> Buffers = new List<byte[]>();
            public List<object> Views;
            public List<object> Accessors;
        }

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            string json = null; byte[] glbBin = null;
            if (data.Length >= 12 && data[0] == (byte)'g' && data[1] == (byte)'l' && data[2] == (byte)'T' && data[3] == (byte)'F')
            {
                int length = Math.Min(BitConverter.ToInt32(data, 8), data.Length);
                int off = 12;
                while (off + 8 <= length)
                {
                    int clen = BitConverter.ToInt32(data, off);
                    uint ctype = BitConverter.ToUInt32(data, off + 4);
                    off += 8;
                    if (clen < 0 || off + clen > data.Length) break;
                    if (ctype == 0x4E4F534A) json = Encoding.UTF8.GetString(data, off, clen);
                    else if (ctype == 0x004E4942) { glbBin = new byte[clen]; Array.Copy(data, off, glbBin, 0, clen); }
                    off += clen;
                }
                if (json == null) throw new InvalidDataException("GLB has no JSON chunk");
            }
            else json = FmtUtil.Text(data);

            var ctx = new Ctx { Root = Json.Parse(json) };
            var root = ctx.Root;
            var bufs = Json.Arr(root, "buffers") ?? new List<object>();
            foreach (var b in bufs)
            {
                string uri = Json.Str(b, "uri", null);
                if (uri == null) ctx.Buffers.Add(glbBin ?? new byte[0]);
                else if (uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    int comma = uri.IndexOf(',');
                    ctx.Buffers.Add(Convert.FromBase64String(uri.Substring(comma + 1)));
                }
                else
                {
                    byte[] ext = null;
                    if (openSibling != null) ext = FmtUtil.ReadAll(openSibling(Uri.UnescapeDataString(uri)));
                    if (ext == null) throw new FileNotFoundException("Missing glTF buffer file '" + uri + "'. Copy it next to the .gltf on the USB drive.");
                    ctx.Buffers.Add(ext);
                }
            }
            ctx.Views = Json.Arr(root, "bufferViews") ?? new List<object>();
            ctx.Accessors = Json.Arr(root, "accessors") ?? new List<object>();

            var scene = new Scene();
            scene.Materials.Clear();
            var mats = Json.Arr(root, "materials") ?? new List<object>();
            var textures = Json.Arr(root, "textures");
            var images = Json.Arr(root, "images");
            foreach (var mo in mats)
            {
                var mat = new Material { Name = Json.Str(mo, "name", "Material" + scene.Materials.Count) };
                var pbr = Json.Obj(mo, "pbrMetallicRoughness");
                var c = Json.Floats(pbr, "baseColorFactor");
                if (c != null && c.Length >= 4) mat.Color = new Vector4(c[0], c[1], c[2], c[3]);
                else mat.Color = Vector4.One;
                mat.Metallic = (float)Json.Num(pbr, "metallicFactor", 1.0);
                mat.Roughness = (float)Json.Num(pbr, "roughnessFactor", 1.0);
                var bct = Json.Obj(pbr, "baseColorTexture");
                if (bct != null && textures != null && images != null)
                {
                    int ti = Json.Int(bct, "index", -1);
                    if (ti >= 0 && ti < textures.Count)
                    {
                        int si = Json.Int(textures[ti], "source", -1);
                        if (si >= 0 && si < images.Count) mat.Texture = Json.Str(images[si], "uri", Json.Str(images[si], "name", null));
                    }
                }
                scene.Materials.Add(mat);
            }
            int defaultMat = -1;
            Func<int> getDefault = () =>
            {
                if (defaultMat < 0) { defaultMat = scene.Materials.Count; scene.Materials.Add(new Material { Name = "Default" }); }
                return defaultMat;
            };

            var meshes = Json.Arr(root, "meshes") ?? new List<object>();
            var meshCache = new Dictionary<int, Mesh>();
            Func<int, Mesh> getMesh = mi =>
            {
                Mesh cached;
                if (meshCache.TryGetValue(mi, out cached)) return cached.Clone();
                var built = BuildMesh(ctx, meshes[mi], getDefault, scene.Materials.Count);
                meshCache[mi] = built;
                return built.Clone();
            };

            var nodes = Json.Arr(root, "nodes") ?? new List<object>();
            var roots = new List<int>();
            var scenes = Json.Arr(root, "scenes");
            int sceneIdx = Json.Int(root, "scene", 0);
            if (scenes != null && sceneIdx < scenes.Count && Json.Arr(scenes[sceneIdx], "nodes") != null)
                foreach (var n in Json.Arr(scenes[sceneIdx], "nodes")) roots.Add((int)(double)n);
            else
            {
                var isChild = new bool[nodes.Count];
                foreach (var n in nodes)
                {
                    var ch = Json.Arr(n, "children");
                    if (ch != null) foreach (var c in ch) { int ci = (int)(double)c; if (ci < isChild.Length) isChild[ci] = true; }
                }
                for (int i = 0; i < nodes.Count; i++) if (!isChild[i]) roots.Add(i);
            }

            var nodeObj = new Dictionary<int, SceneObject>();
            var nodeParent = new Dictionary<int, Matrix4x4>();
            var stack = new Stack<KeyValuePair<int, Matrix4x4>>();
            for (int i = roots.Count - 1; i >= 0; i--) stack.Push(new KeyValuePair<int, Matrix4x4>(roots[i], Matrix4x4.Identity));
            var visited = new HashSet<int>();
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                int ni = item.Key;
                if (ni < 0 || ni >= nodes.Count || !visited.Add(ni)) continue;
                var node = nodes[ni];
                var world = LocalMatrix(node) * item.Value;
                if (Json.Has(node, "mesh"))
                {
                    int mi = Json.Int(node, "mesh", 0);
                    if (mi >= 0 && mi < meshes.Count)
                    {
                        var o = new SceneObject { Name = Json.Str(node, "name", Json.Str(meshes[mi], "name", "Mesh" + mi)), Mesh = getMesh(mi) };
                        o.SetWorld(world);
                        if (o.Mesh.Normals.Count == 0) o.Shading = Shading.Flat;
                        scene.Objects.Add(o);
                        nodeObj[ni] = o;
                        nodeParent[ni] = item.Value;
                    }
                }
                var children = Json.Arr(node, "children");
                if (children != null)
                    for (int c = children.Count - 1; c >= 0; c--)
                        stack.Push(new KeyValuePair<int, Matrix4x4>((int)(double)children[c], world));
            }

            ImportAnimation(ctx, nodes, nodeObj, nodeParent, scene);
            if (scene.Materials.Count == 0) getDefault();
            return scene;
        }

        static Matrix4x4 LocalMatrix(object node)
        {
            var mx = Json.Floats(node, "matrix");
            if (mx != null && mx.Length == 16)
                return new Matrix4x4(mx[0], mx[1], mx[2], mx[3], mx[4], mx[5], mx[6], mx[7],
                                     mx[8], mx[9], mx[10], mx[11], mx[12], mx[13], mx[14], mx[15]);
            Vector3 t, s; Quaternion r;
            NodeTrs(node, out t, out r, out s);
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }

        static void NodeTrs(object node, out Vector3 t, out Quaternion r, out Vector3 s)
        {
            var mx = Json.Floats(node, "matrix");
            if (mx != null && mx.Length == 16)
            {
                var m = new Matrix4x4(mx[0], mx[1], mx[2], mx[3], mx[4], mx[5], mx[6], mx[7], mx[8], mx[9], mx[10], mx[11], mx[12], mx[13], mx[14], mx[15]);
                if (Matrix4x4.Decompose(m, out s, out r, out t)) return;
            }
            var tt = Json.Floats(node, "translation");
            var rr = Json.Floats(node, "rotation");
            var ss = Json.Floats(node, "scale");
            t = tt != null && tt.Length == 3 ? new Vector3(tt[0], tt[1], tt[2]) : Vector3.Zero;
            r = rr != null && rr.Length == 4 ? new Quaternion(rr[0], rr[1], rr[2], rr[3]) : Quaternion.Identity;
            s = ss != null && ss.Length == 3 ? new Vector3(ss[0], ss[1], ss[2]) : Vector3.One;
        }

        static int Comps(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                case "MAT2": return 4;
                case "MAT3": return 9;
                case "MAT4": return 16;
            }
            return 1;
        }

        static int CompSize(int ct)
        {
            switch (ct) { case 5120: case 5121: return 1; case 5122: case 5123: return 2; default: return 4; }
        }

        static double ReadComp(byte[] b, int p, int ct, bool norm)
        {
            switch (ct)
            {
                case 5120: { double v = (sbyte)b[p]; return norm ? Math.Max(v / 127.0, -1.0) : v; }
                case 5121: { double v = b[p]; return norm ? v / 255.0 : v; }
                case 5122: { double v = BitConverter.ToInt16(b, p); return norm ? Math.Max(v / 32767.0, -1.0) : v; }
                case 5123: { double v = BitConverter.ToUInt16(b, p); return norm ? v / 65535.0 : v; }
                case 5125: return BitConverter.ToUInt32(b, p);
                default: return BitConverter.ToSingle(b, p);
            }
        }

        static double[] ReadAccessor(Ctx ctx, int index, out int comps)
        {
            var acc = ctx.Accessors[index];
            int count = Json.Int(acc, "count", 0);
            comps = Comps(Json.Str(acc, "type", "SCALAR"));
            int ct = Json.Int(acc, "componentType", 5126);
            object nv; bool norm = false;
            var accD = acc as Dictionary<string, object>;
            if (accD != null && accD.TryGetValue("normalized", out nv) && nv is bool) norm = (bool)nv;
            var result = new double[count * comps];
            if (Json.Has(acc, "bufferView"))
            {
                var bv = ctx.Views[Json.Int(acc, "bufferView", 0)];
                var buf = ctx.Buffers[Json.Int(bv, "buffer", 0)];
                int off = Json.Int(bv, "byteOffset", 0) + Json.Int(acc, "byteOffset", 0);
                int csize = CompSize(ct);
                int stride = Json.Int(bv, "byteStride", 0);
                if (stride == 0) stride = csize * comps;
                for (int i = 0; i < count; i++)
                    for (int c = 0; c < comps; c++)
                    {
                        int p = off + i * stride + c * csize;
                        if (p + csize <= buf.Length) result[i * comps + c] = ReadComp(buf, p, ct, norm);
                    }
            }
            var sparse = Json.Obj(acc, "sparse");
            if (sparse != null)
            {
                int scount = Json.Int(sparse, "count", 0);
                var si = Json.Obj(sparse, "indices"); var sv = Json.Obj(sparse, "values");
                var ibv = ctx.Views[Json.Int(si, "bufferView", 0)];
                var vbv = ctx.Views[Json.Int(sv, "bufferView", 0)];
                var ibuf = ctx.Buffers[Json.Int(ibv, "buffer", 0)];
                var vbuf = ctx.Buffers[Json.Int(vbv, "buffer", 0)];
                int ioff = Json.Int(ibv, "byteOffset", 0) + Json.Int(si, "byteOffset", 0);
                int voff = Json.Int(vbv, "byteOffset", 0) + Json.Int(sv, "byteOffset", 0);
                int ict = Json.Int(si, "componentType", 5125);
                int vsz = CompSize(ct);
                for (int i = 0; i < scount; i++)
                {
                    int target = (int)ReadComp(ibuf, ioff + i * CompSize(ict), ict, false);
                    for (int c = 0; c < comps; c++)
                        if (target * comps + c < result.Length)
                            result[target * comps + c] = ReadComp(vbuf, voff + (i * comps + c) * vsz, ct, norm);
                }
            }
            return result;
        }

        static Mesh BuildMesh(Ctx ctx, object meshObj, Func<int> getDefault, int materialCount)
        {
            var m = new Mesh();
            var prims = Json.Arr(meshObj, "primitives") ?? new List<object>();
            foreach (var prim in prims)
            {
                int mode = Json.Int(prim, "mode", 4);
                if (mode != 4 && mode != 5 && mode != 6) continue;
                var attrs = Json.Obj(prim, "attributes");
                if (!Json.Has(attrs, "POSITION")) continue;
                int nc;
                var P = ReadAccessor(ctx, Json.Int(attrs, "POSITION", 0), out nc);
                int vcount = P.Length / Math.Max(1, nc);
                int baseV = m.Positions.Count;
                for (int i = 0; i < vcount; i++) m.Positions.Add(new Vector3((float)P[i * nc], (float)P[i * nc + 1], (float)P[i * nc + 2]));

                int baseN = m.Normals.Count; bool hasN = false;
                if (Json.Has(attrs, "NORMAL"))
                {
                    var N = ReadAccessor(ctx, Json.Int(attrs, "NORMAL", 0), out nc);
                    if (N.Length / nc == vcount)
                    {
                        hasN = true;
                        for (int i = 0; i < vcount; i++) m.Normals.Add(new Vector3((float)N[i * nc], (float)N[i * nc + 1], (float)N[i * nc + 2]));
                    }
                }
                int baseT = m.UVs.Count; bool hasT = false;
                if (Json.Has(attrs, "TEXCOORD_0"))
                {
                    var T = ReadAccessor(ctx, Json.Int(attrs, "TEXCOORD_0", 0), out nc);
                    if (T.Length / nc == vcount)
                    {
                        hasT = true;
                        for (int i = 0; i < vcount; i++) m.UVs.Add(new Vector2((float)T[i * nc], 1f - (float)T[i * nc + 1]));
                    }
                }
                if (Json.Has(attrs, "COLOR_0"))
                {
                    var C = ReadAccessor(ctx, Json.Int(attrs, "COLOR_0", 0), out nc);
                    if (m.Colors == null) { m.Colors = new List<Vector4>(); for (int i = 0; i < baseV; i++) m.Colors.Add(Vector4.One); }
                    for (int i = 0; i < vcount; i++)
                    {
                        if (i * nc + 2 < C.Length)
                            m.Colors.Add(new Vector4((float)C[i * nc], (float)C[i * nc + 1], (float)C[i * nc + 2], nc == 4 ? (float)C[i * nc + 3] : 1f));
                        else m.Colors.Add(Vector4.One);
                    }
                }
                else if (m.Colors != null) for (int i = 0; i < vcount; i++) m.Colors.Add(Vector4.One);

                int[] idx;
                if (Json.Has(prim, "indices"))
                {
                    int ic;
                    var I = ReadAccessor(ctx, Json.Int(prim, "indices", 0), out ic);
                    idx = new int[I.Length];
                    for (int i = 0; i < I.Length; i++) idx[i] = (int)I[i];
                }
                else { idx = new int[vcount]; for (int i = 0; i < vcount; i++) idx[i] = i; }

                int mat = Json.Has(prim, "material") ? Json.Int(prim, "material", 0) : getDefault();
                if (mat < 0) mat = getDefault();
                Action<int, int, int> add = (a, b, c) =>
                {
                    if (a == b || b == c || a == c) return;
                    if (a < 0 || b < 0 || c < 0 || a >= vcount || b >= vcount || c >= vcount) return;
                    var f = new Face(new[] { baseV + a, baseV + b, baseV + c }, mat);
                    if (hasN) f.N = new[] { baseN + a, baseN + b, baseN + c };
                    if (hasT) f.T = new[] { baseT + a, baseT + b, baseT + c };
                    m.Faces.Add(f);
                };
                if (mode == 4) for (int i = 0; i + 2 < idx.Length; i += 3) add(idx[i], idx[i + 1], idx[i + 2]);
                else if (mode == 5) for (int i = 0; i + 2 < idx.Length; i++) { if ((i & 1) == 0) add(idx[i], idx[i + 1], idx[i + 2]); else add(idx[i + 1], idx[i], idx[i + 2]); }
                else for (int i = 1; i + 1 < idx.Length; i++) add(idx[0], idx[i], idx[i + 1]);
            }
            return m;
        }

        static void ImportAnimation(Ctx ctx, List<object> nodes, Dictionary<int, SceneObject> nodeObj, Dictionary<int, Matrix4x4> nodeParent, Scene scene)
        {
            var anims = Json.Arr(ctx.Root, "animations");
            if (anims == null || anims.Count == 0) return;
            var anim = anims[0];
            var samplers = Json.Arr(anim, "samplers") ?? new List<object>();
            var channels = Json.Arr(anim, "channels") ?? new List<object>();
            const float fps = 24f;
            var byNode = new Dictionary<int, List<object>>();
            foreach (var ch in channels)
            {
                var target = Json.Obj(ch, "target");
                int node = Json.Int(target, "node", -1);
                if (!nodeObj.ContainsKey(node)) continue;
                List<object> l;
                if (!byNode.TryGetValue(node, out l)) { l = new List<object>(); byNode[node] = l; }
                l.Add(ch);
            }
            int maxFrame = 0;
            foreach (var kv in byNode)
            {
                var o = nodeObj[kv.Key];
                Vector3 bt, bs; Quaternion br;
                NodeTrs(nodes[kv.Key], out bt, out br, out bs);
                var tracks = new List<Track>();
                var frames = new SortedSet<int>();
                foreach (var ch in kv.Value)
                {
                    int si = Json.Int(ch, "sampler", -1);
                    if (si < 0 || si >= samplers.Count) continue;
                    var smp = samplers[si];
                    int c1, c2;
                    var input = ReadAccessor(ctx, Json.Int(smp, "input", 0), out c1);
                    var output = ReadAccessor(ctx, Json.Int(smp, "output", 0), out c2);
                    var tr = new Track
                    {
                        Path = Json.Str(Json.Obj(ch, "target"), "path", ""),
                        Times = input, Values = output, Comps = c2,
                        Interp = Json.Str(smp, "interpolation", "LINEAR")
                    };
                    if (tr.Path != "translation" && tr.Path != "rotation" && tr.Path != "scale") continue;
                    tracks.Add(tr);
                    foreach (var t in input) frames.Add((int)Math.Round(t * fps));
                }
                if (tracks.Count == 0) continue;
                var parent = nodeParent[kv.Key];
                foreach (int f in frames)
                {
                    float time = f / fps;
                    Vector3 t = bt, s = bs; Quaternion r = br;
                    foreach (var tr in tracks)
                    {
                        var v = tr.Sample(time);
                        if (tr.Path == "translation") t = new Vector3(v[0], v[1], v[2]);
                        else if (tr.Path == "scale") s = new Vector3(v[0], v[1], v[2]);
                        else r = Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));
                    }
                    var w = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t) * parent;
                    Vector3 ws, wt; Quaternion wr;
                    if (!Matrix4x4.Decompose(w, out ws, out wr, out wt)) continue;
                    o.Keys.Add(new Keyframe { Frame = Math.Max(0, f), Position = wt, Rotation = wr, Scale = ws });
                    maxFrame = Math.Max(maxFrame, f);
                }
                o.Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                o.Evaluate(0);
            }
            scene.Fps = fps;
            if (maxFrame > 0) scene.FrameEnd = maxFrame;
        }

        sealed class Track
        {
            public string Path, Interp;
            public double[] Times, Values;
            public int Comps;

            float[] ValueAt(int key)
            {
                var r = new float[Comps];
                int stride = Interp == "CUBICSPLINE" ? Comps * 3 : Comps;
                int off = key * stride + (Interp == "CUBICSPLINE" ? Comps : 0);
                for (int c = 0; c < Comps; c++) r[c] = off + c < Values.Length ? (float)Values[off + c] : 0f;
                return r;
            }

            public float[] Sample(float t)
            {
                int n = Times.Length;
                if (n == 0) return new float[Comps];
                if (t <= Times[0]) return ValueAt(0);
                if (t >= Times[n - 1]) return ValueAt(n - 1);
                int k = 0;
                while (k < n - 2 && Times[k + 1] < t) k++;
                var a = ValueAt(k); var b = ValueAt(k + 1);
                if (Interp == "STEP") return a;
                float u = (float)((t - Times[k]) / Math.Max(1e-9, Times[k + 1] - Times[k]));
                if (Path == "rotation" && Comps == 4)
                {
                    var q = Quaternion.Slerp(new Quaternion(a[0], a[1], a[2], a[3]), new Quaternion(b[0], b[1], b[2], b[3]), u);
                    return new[] { q.X, q.Y, q.Z, q.W };
                }
                var r = new float[Comps];
                for (int c = 0; c < Comps; c++) r[c] = a[c] + (b[c] - a[c]) * u;
                return r;
            }
        }

        // ------------------------------------------------------------------ export

        struct VKey : IEquatable<VKey>
        {
            public int V, T; public long N;
            public bool Equals(VKey o) { return V == o.V && T == o.T && N == o.N; }
            public override bool Equals(object obj) { return obj is VKey && Equals((VKey)obj); }
            public override int GetHashCode() { unchecked { return (V * 397) ^ (T * 7919) ^ N.GetHashCode(); } }
        }

        sealed class BinBuilder
        {
            public readonly MemoryStream Bin = new MemoryStream();
            public readonly List<object> Views = new List<object>();
            public readonly List<object> Accessors = new List<object>();

            int AddView(byte[] bytes, int target)
            {
                while (Bin.Length % 4 != 0) Bin.WriteByte(0);
                var v = new Dictionary<string, object>();
                v["buffer"] = 0;
                v["byteOffset"] = (int)Bin.Length;
                v["byteLength"] = bytes.Length;
                if (target != 0) v["target"] = target;
                Bin.Write(bytes, 0, bytes.Length);
                Views.Add(v);
                return Views.Count - 1;
            }

            public int AddFloats(List<float> data, int comps, string type, int target, bool minMax)
            {
                var bytes = new byte[data.Count * 4];
                for (int i = 0; i < data.Count; i++) Array.Copy(BitConverter.GetBytes(data[i]), 0, bytes, i * 4, 4);
                int view = AddView(bytes, target);
                var a = new Dictionary<string, object>();
                a["bufferView"] = view;
                a["componentType"] = 5126;
                a["count"] = data.Count / comps;
                a["type"] = type;
                if (minMax)
                {
                    var mn = new double[comps]; var mx = new double[comps];
                    for (int c = 0; c < comps; c++) { mn[c] = double.MaxValue; mx[c] = double.MinValue; }
                    for (int i = 0; i < data.Count; i++) { int c = i % comps; mn[c] = Math.Min(mn[c], data[i]); mx[c] = Math.Max(mx[c], data[i]); }
                    a["min"] = mn; a["max"] = mx;
                }
                Accessors.Add(a);
                return Accessors.Count - 1;
            }

            public int AddIndices(List<uint> idx)
            {
                var bytes = new byte[idx.Count * 4];
                for (int i = 0; i < idx.Count; i++) Array.Copy(BitConverter.GetBytes(idx[i]), 0, bytes, i * 4, 4);
                int view = AddView(bytes, 34963);
                var a = new Dictionary<string, object>();
                a["bufferView"] = view;
                a["componentType"] = 5125;
                a["count"] = idx.Count;
                a["type"] = "SCALAR";
                Accessors.Add(a);
                return Accessors.Count - 1;
            }
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            var bb = new BinBuilder();
            var nodes = new List<object>();
            var meshes = new List<object>();
            var rootNodes = new List<object>();

            foreach (var o in scene.Objects)
            {
                var m = o.Mesh;
                var node = new Dictionary<string, object>();
                node["name"] = o.Name;
                node["translation"] = new[] { o.Position.X, o.Position.Y, o.Position.Z };
                node["rotation"] = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W };
                node["scale"] = new[] { o.Scale.X, o.Scale.Y, o.Scale.Z };

                var smooth = o.Shading == Shading.Smooth ? m.ComputeVertexNormals() : null;
                var groups = new SortedDictionary<int, List<int>>();
                for (int fi = 0; fi < m.Faces.Count; fi++)
                {
                    if (!m.IsValidFace(m.Faces[fi])) continue;
                    List<int> g;
                    int mat = Math.Max(0, Math.Min(scene.Materials.Count - 1, m.Faces[fi].Material));
                    if (!groups.TryGetValue(mat, out g)) { g = new List<int>(); groups[mat] = g; }
                    g.Add(fi);
                }
                bool anyT = m.UVs.Count > 0 && m.Faces.Any(f => f.T != null);
                var prims = new List<object>();
                foreach (var kv in groups)
                {
                    var map = new Dictionary<VKey, uint>();
                    var P = new List<float>(); var N = new List<float>(); var T = new List<float>(); var C = new List<float>();
                    var I = new List<uint>();
                    foreach (int fi in kv.Value)
                    {
                        var f = m.Faces[fi];
                        var flat = m.FaceNormal(f);
                        for (int k = 1; k < f.V.Length - 1; k++)
                        {
                            foreach (int corner in new[] { 0, k, k + 1 })
                            {
                                int v = f.V[corner];
                                int t = anyT && f.T != null && corner < f.T.Length && f.T[corner] >= 0 && f.T[corner] < m.UVs.Count ? f.T[corner] : -1;
                                long nk;
                                if (o.Shading == Shading.Smooth) nk = -1;
                                else if (o.Shading == Shading.Auto && f.N != null && corner < f.N.Length && f.N[corner] >= 0 && f.N[corner] < m.Normals.Count) nk = f.N[corner];
                                else nk = -2 - fi;
                                var key = new VKey { V = v, T = t, N = nk };
                                uint idx;
                                if (!map.TryGetValue(key, out idx))
                                {
                                    idx = (uint)(P.Count / 3);
                                    map[key] = idx;
                                    var p = m.Positions[v];
                                    P.Add(p.X); P.Add(p.Y); P.Add(p.Z);
                                    var n = FmtUtil.CornerNormal(m, f, corner, o.Shading, smooth, flat);
                                    N.Add(n.X); N.Add(n.Y); N.Add(n.Z);
                                    if (anyT)
                                    {
                                        var uv = t >= 0 ? m.UVs[t] : Vector2.Zero;
                                        T.Add(uv.X); T.Add(1f - uv.Y);
                                    }
                                    if (m.Colors != null)
                                    {
                                        var c = v < m.Colors.Count ? m.Colors[v] : Vector4.One;
                                        C.Add(c.X); C.Add(c.Y); C.Add(c.Z); C.Add(c.W);
                                    }
                                }
                                I.Add(idx);
                            }
                        }
                    }
                    if (I.Count == 0) continue;
                    var attrs = new Dictionary<string, object>();
                    attrs["POSITION"] = bb.AddFloats(P, 3, "VEC3", 34962, true);
                    attrs["NORMAL"] = bb.AddFloats(N, 3, "VEC3", 34962, false);
                    if (anyT) attrs["TEXCOORD_0"] = bb.AddFloats(T, 2, "VEC2", 34962, false);
                    if (m.Colors != null) attrs["COLOR_0"] = bb.AddFloats(C, 4, "VEC4", 34962, false);
                    var prim = new Dictionary<string, object>();
                    prim["attributes"] = attrs;
                    prim["indices"] = bb.AddIndices(I);
                    prim["material"] = kv.Key;
                    prim["mode"] = 4;
                    prims.Add(prim);
                }
                if (prims.Count > 0)
                {
                    var mesh = new Dictionary<string, object>();
                    mesh["name"] = o.Name;
                    mesh["primitives"] = prims;
                    meshes.Add(mesh);
                    node["mesh"] = meshes.Count - 1;
                }
                rootNodes.Add(nodes.Count);
                nodes.Add(node);
            }

            var materials = new List<object>();
            foreach (var mat in scene.Materials)
            {
                var pbr = new Dictionary<string, object>();
                pbr["baseColorFactor"] = new[] { mat.Color.X, mat.Color.Y, mat.Color.Z, mat.Color.W };
                pbr["metallicFactor"] = mat.Metallic;
                pbr["roughnessFactor"] = mat.Roughness;
                var md = new Dictionary<string, object>();
                md["name"] = mat.Name;
                md["pbrMetallicRoughness"] = pbr;
                md["doubleSided"] = true;
                if (mat.Color.W < 0.999f) md["alphaMode"] = "BLEND";
                materials.Add(md);
            }

            // Animation (TRS keyframes, linear).
            var samplers = new List<object>();
            var channels = new List<object>();
            float fps = scene.Fps > 0 ? scene.Fps : 24f;
            for (int i = 0; i < scene.Objects.Count; i++)
            {
                var o = scene.Objects[i];
                if (o.Keys.Count == 0) continue;
                var times = o.Keys.Select(k => k.Frame / fps).ToList();
                int input = bb.AddFloats(times, 1, "SCALAR", 0, true);
                var tl = new List<float>(); var rl = new List<float>(); var sl = new List<float>();
                foreach (var k in o.Keys)
                {
                    tl.Add(k.Position.X); tl.Add(k.Position.Y); tl.Add(k.Position.Z);
                    rl.Add(k.Rotation.X); rl.Add(k.Rotation.Y); rl.Add(k.Rotation.Z); rl.Add(k.Rotation.W);
                    sl.Add(k.Scale.X); sl.Add(k.Scale.Y); sl.Add(k.Scale.Z);
                }
                AddChannel(samplers, channels, input, bb.AddFloats(tl, 3, "VEC3", 0, false), i, "translation");
                AddChannel(samplers, channels, input, bb.AddFloats(rl, 4, "VEC4", 0, false), i, "rotation");
                AddChannel(samplers, channels, input, bb.AddFloats(sl, 3, "VEC3", 0, false), i, "scale");
            }

            while (bb.Bin.Length % 4 != 0) bb.Bin.WriteByte(0);
            var binBytes = bb.Bin.ToArray();

            var root = new Dictionary<string, object>();
            var asset = new Dictionary<string, object>();
            asset["version"] = "2.0";
            asset["generator"] = "Facet3D for Xbox";
            root["asset"] = asset;
            root["scene"] = 0;
            var sc = new Dictionary<string, object>();
            sc["name"] = "Scene";
            sc["nodes"] = rootNodes;
            root["scenes"] = new List<object> { sc };
            root["nodes"] = nodes;
            if (meshes.Count > 0) root["meshes"] = meshes;
            root["materials"] = materials;
            if (bb.Accessors.Count > 0)
            {
                root["accessors"] = bb.Accessors;
                root["bufferViews"] = bb.Views;
                var buffer = new Dictionary<string, object>();
                buffer["byteLength"] = binBytes.Length;
                if (!_binary) buffer["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(binBytes);
                root["buffers"] = new List<object> { buffer };
            }
            if (channels.Count > 0)
            {
                var anim = new Dictionary<string, object>();
                anim["name"] = "Animation";
                anim["samplers"] = samplers;
                anim["channels"] = channels;
                root["animations"] = new List<object> { anim };
            }

            string json = Json.Write(root);
            string name = baseName + (_binary ? ".glb" : ".gltf");
            using (var s = createFile(name))
            {
                if (!_binary)
                {
                    var tb = new UTF8Encoding(false).GetBytes(json);
                    s.Write(tb, 0, tb.Length);
                }
                else
                {
                    var jb = new UTF8Encoding(false).GetBytes(json);
                    int jlen = (jb.Length + 3) & ~3;
                    int blen = binBytes.Length;
                    bool hasBin = bb.Accessors.Count > 0;
                    int total = 12 + 8 + jlen + (hasBin ? 8 + blen : 0);
                    using (var w = new BinaryWriter(s))
                    {
                        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
                        w.Write((uint)jlen); w.Write(0x4E4F534Au);
                        w.Write(jb);
                        for (int i = jb.Length; i < jlen; i++) w.Write((byte)0x20);
                        if (hasBin)
                        {
                            w.Write((uint)blen); w.Write(0x004E4942u);
                            w.Write(binBytes);
                        }
                        w.Flush();
                    }
                }
            }
            return name;
        }

        static void AddChannel(List<object> samplers, List<object> channels, int input, int output, int node, string path)
        {
            var smp = new Dictionary<string, object>();
            smp["input"] = input; smp["output"] = output; smp["interpolation"] = "LINEAR";
            samplers.Add(smp);
            var target = new Dictionary<string, object>();
            target["node"] = node; target["path"] = path;
            var ch = new Dictionary<string, object>();
            ch["sampler"] = samplers.Count - 1; ch["target"] = target;
            channels.Add(ch);
        }
    }
}
