using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>Autodesk FBX: binary and ASCII import (meshes, normals, UVs, materials, node transforms);
    /// binary FBX 7.4 export (meshes, UVs, materials, transforms).</summary>
    public sealed class FbxFormat : IModelFormat
    {
        public string Name { get { return "Autodesk FBX"; } }
        public string[] Extensions { get { return new[] { ".fbx" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        sealed class Node
        {
            public string Name;
            public List<object> Props = new List<object>();
            public List<Node> Children = new List<Node>();

            public Node Child(string name)
            {
                foreach (var c in Children) if (c.Name == name) return c;
                return null;
            }

            public IEnumerable<Node> All(string name) { return Children.Where(c => c.Name == name); }
        }

        // ------------------------------------------------------------------ parsing

        static readonly byte[] BinMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary");

        static List<Node> ParseBinary(byte[] d, out int version)
        {
            version = BitConverter.ToInt32(d, 23);
            int pos = 27;
            var top = new List<Node>();
            bool wide = version >= 7500;
            while (pos < d.Length)
            {
                var n = ReadNode(d, ref pos, wide);
                if (n == null) break;
                top.Add(n);
            }
            return top;
        }

        static Node ReadNode(byte[] d, ref int pos, bool wide)
        {
            int headerSize = wide ? 25 : 13;
            if (pos + headerSize > d.Length) return null;
            long end, numProps;
            if (wide)
            {
                end = BitConverter.ToInt64(d, pos); numProps = BitConverter.ToInt64(d, pos + 8);
                pos += 24;
            }
            else
            {
                end = BitConverter.ToUInt32(d, pos); numProps = BitConverter.ToUInt32(d, pos + 4);
                pos += 12;
            }
            int nameLen = d[pos++];
            if (end == 0) return null;
            var node = new Node { Name = Encoding.ASCII.GetString(d, pos, nameLen) };
            pos += nameLen;
            for (long i = 0; i < numProps; i++) node.Props.Add(ReadProp(d, ref pos));
            while (pos < end)
            {
                var c = ReadNode(d, ref pos, wide);
                if (c == null) break;
                node.Children.Add(c);
            }
            pos = (int)end;
            return node;
        }

        static object ReadProp(byte[] d, ref int pos)
        {
            char t = (char)d[pos++];
            switch (t)
            {
                case 'Y': { var v = BitConverter.ToInt16(d, pos); pos += 2; return (long)v; }
                case 'C': { var v = d[pos] != 0; pos += 1; return v; }
                case 'I': { var v = BitConverter.ToInt32(d, pos); pos += 4; return (long)v; }
                case 'F': { var v = BitConverter.ToSingle(d, pos); pos += 4; return (double)v; }
                case 'D': { var v = BitConverter.ToDouble(d, pos); pos += 8; return v; }
                case 'L': { var v = BitConverter.ToInt64(d, pos); pos += 8; return v; }
                case 'S':
                case 'R':
                    {
                        int len = BitConverter.ToInt32(d, pos); pos += 4;
                        object v = t == 'S' ? (object)Encoding.UTF8.GetString(d, pos, len) : (object)SubArray(d, pos, len);
                        pos += len;
                        return v;
                    }
                case 'f': case 'd': case 'l': case 'i': case 'b':
                    {
                        int count = BitConverter.ToInt32(d, pos);
                        int enc = BitConverter.ToInt32(d, pos + 4);
                        int clen = BitConverter.ToInt32(d, pos + 8);
                        pos += 12;
                        byte[] raw;
                        if (enc == 1)
                        {
                            // zlib: skip 2-byte header, inflate the deflate stream.
                            using (var ms = new MemoryStream(d, pos + 2, clen - 2))
                            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                            using (var outMs = new MemoryStream())
                            {
                                ds.CopyTo(outMs);
                                raw = outMs.ToArray();
                            }
                        }
                        else raw = SubArray(d, pos, clen);
                        pos += clen;
                        if (t == 'd') { var r = new double[count]; for (int i = 0; i < count && i * 8 + 8 <= raw.Length; i++) r[i] = BitConverter.ToDouble(raw, i * 8); return r; }
                        if (t == 'f') { var r = new double[count]; for (int i = 0; i < count && i * 4 + 4 <= raw.Length; i++) r[i] = BitConverter.ToSingle(raw, i * 4); return r; }
                        if (t == 'l') { var r = new long[count]; for (int i = 0; i < count && i * 8 + 8 <= raw.Length; i++) r[i] = BitConverter.ToInt64(raw, i * 8); return r; }
                        if (t == 'i') { var r = new long[count]; for (int i = 0; i < count && i * 4 + 4 <= raw.Length; i++) r[i] = BitConverter.ToInt32(raw, i * 4); return r; }
                        { var r = new long[count]; for (int i = 0; i < count && i < raw.Length; i++) r[i] = raw[i]; return r; }
                    }
            }
            throw new InvalidDataException("Unknown FBX property type '" + t + "'");
        }

        static byte[] SubArray(byte[] d, int pos, int len)
        {
            var r = new byte[len];
            Array.Copy(d, pos, r, 0, len);
            return r;
        }

        // ASCII FBX tokenizer/parser.
        sealed class AsciiParser
        {
            readonly string _s; int _i;
            public AsciiParser(string s) { _s = s; }

            void Skip()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ';') { while (_i < _s.Length && _s[_i] != '\n') _i++; }
                    else if (char.IsWhiteSpace(c)) _i++;
                    else break;
                }
            }

            public List<Node> ParseAll()
            {
                var list = new List<Node>();
                while (true)
                {
                    Skip();
                    if (_i >= _s.Length) break;
                    if (_s[_i] == '}') { _i++; break; }
                    var n = ParseNode();
                    if (n == null) break;
                    list.Add(n);
                }
                return list;
            }

            Node ParseNode()
            {
                int st = _i;
                while (_i < _s.Length && _s[_i] != ':' && _s[_i] != '\n' && _s[_i] != '{' && _s[_i] != '}') _i++;
                if (_i >= _s.Length || _s[_i] != ':') { _i++; return new Node { Name = "" }; }
                var node = new Node { Name = _s.Substring(st, _i - st).Trim() };
                _i++;
                // properties until newline or '{'
                while (true)
                {
                    while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\r')) _i++;
                    if (_i >= _s.Length) return node;
                    char c = _s[_i];
                    if (c == '\n' || c == ';') { if (!ContinuesOnNextLine()) return node; continue; }
                    if (c == '{') { _i++; ParseBlock(node); return node; }
                    if (c == '}') return node;
                    if (c == ',') { _i++; continue; }
                    if (c == '"')
                    {
                        int e = _s.IndexOf('"', _i + 1);
                        if (e < 0) e = _s.Length;
                        node.Props.Add(_s.Substring(_i + 1, e - _i - 1));
                        _i = e + 1;
                        continue;
                    }
                    if (c == '*')
                    {
                        // array: *N { a: v,v,v }
                        while (_i < _s.Length && _s[_i] != '{') _i++;
                        _i++;
                        Skip();
                        while (_i < _s.Length && _s[_i] != ':') _i++;
                        _i++;
                        int e = _s.IndexOf('}', _i);
                        if (e < 0) e = _s.Length;
                        var parts = _s.Substring(_i, e - _i).Split(new[] { ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        var arr = new double[parts.Length];
                        for (int k = 0; k < parts.Length; k++) double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out arr[k]);
                        node.Props.Add(arr);
                        _i = Math.Min(_s.Length, e + 1);
                        continue;
                    }
                    int st2 = _i;
                    while (_i < _s.Length && _s[_i] != ',' && _s[_i] != '\n' && _s[_i] != '{' && _s[_i] != '}' && _s[_i] != '\r') _i++;
                    string tok = _s.Substring(st2, _i - st2).Trim();
                    if (tok.Length == 0) continue;
                    long lv; double dv;
                    if (long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv)) node.Props.Add(lv);
                    else if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) node.Props.Add(dv);
                    else node.Props.Add(tok);
                }
            }

            // Long numeric lists in old ASCII FBX may wrap with a trailing comma.
            bool ContinuesOnNextLine()
            {
                int j = _i - 1;
                while (j >= 0 && (_s[j] == ' ' || _s[j] == '\t' || _s[j] == '\r')) j--;
                if (j >= 0 && _s[j] == ',') { _i++; return true; }
                return false;
            }

            void ParseBlock(Node node)
            {
                node.Children.AddRange(ParseAll());
            }
        }

        // ------------------------------------------------------------------ helpers

        static double[] Doubles(object p)
        {
            var d = p as double[];
            if (d != null) return d;
            var l = p as long[];
            if (l != null) return l.Select(x => (double)x).ToArray();
            return new double[0];
        }

        static long[] Longs(object p)
        {
            var l = p as long[];
            if (l != null) return l;
            var d = p as double[];
            if (d != null) return d.Select(x => (long)x).ToArray();
            return new long[0];
        }

        static long Id(object p)
        {
            if (p is long) return (long)p;
            if (p is double) return (long)(double)p;
            var s = p as string;
            long v;
            if (s != null && long.TryParse(s, out v)) return v;
            return s != null ? s.GetHashCode() : 0;
        }

        static string CleanName(object p)
        {
            var s = p as string ?? "";
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            int c = s.IndexOf("::", StringComparison.Ordinal);
            if (c >= 0) s = s.Substring(c + 2);
            return s;
        }

        static double Num(object p)
        {
            if (p is double) return (double)p;
            if (p is long) return (long)p;
            if (p is bool) return (bool)p ? 1 : 0;
            return 0;
        }

        static string Str(object p) { return p as string ?? ""; }

        /// <summary>Properties70 { P: "name", type, label, flags, values... } (or old "Property:" style).</summary>
        static Dictionary<string, List<object>> Props70(Node n)
        {
            var r = new Dictionary<string, List<object>>();
            if (n == null) return r;
            var p70 = n.Child("Properties70") ?? n.Child("Properties60");
            if (p70 == null) return r;
            foreach (var p in p70.Children)
            {
                if (p.Props.Count < 1) continue;
                int skip = p.Name == "P" ? 4 : 3;
                r[Str(p.Props[0])] = p.Props.Skip(skip).ToList();
            }
            return r;
        }

        static Vector3 Vec(Dictionary<string, List<object>> p, string key, Vector3 def)
        {
            List<object> v;
            if (!p.TryGetValue(key, out v) || v.Count < 3) return def;
            return new Vector3((float)Num(v[0]), (float)Num(v[1]), (float)Num(v[2]));
        }

        static Matrix4x4 Euler(Vector3 deg)
        {
            float k = (float)Math.PI / 180f;
            return Matrix4x4.CreateRotationX(deg.X * k) * Matrix4x4.CreateRotationY(deg.Y * k) * Matrix4x4.CreateRotationZ(deg.Z * k);
        }

        // ------------------------------------------------------------------ import

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            List<Node> top;
            bool binary = data.Length > 27;
            for (int i = 0; binary && i < BinMagic.Length; i++) if (data[i] != BinMagic[i]) binary = false;
            int version;
            if (binary) top = ParseBinary(data, out version);
            else top = new AsciiParser(FmtUtil.Text(data)).ParseAll();

            var objects = top.FirstOrDefault(n => n.Name == "Objects");
            var conns = top.FirstOrDefault(n => n.Name == "Connections");
            var global = top.FirstOrDefault(n => n.Name == "GlobalSettings");
            if (objects == null) throw new InvalidDataException("FBX has no Objects section (FBX 6 and older are not supported).");

            var geoms = new Dictionary<long, Node>();
            var models = new Dictionary<long, Node>();
            var mats = new Dictionary<long, Node>();
            foreach (var n in objects.Children)
            {
                if (n.Props.Count == 0) continue;
                long id = Id(n.Props[0]);
                if (n.Name == "Geometry") geoms[id] = n;
                else if (n.Name == "Model") models[id] = n;
                else if (n.Name == "Material") mats[id] = n;
            }

            var modelGeom = new Dictionary<long, long>();
            var modelMats = new Dictionary<long, List<long>>();
            var modelParent = new Dictionary<long, long>();
            if (conns != null)
                foreach (var c in conns.Children)
                {
                    if (c.Props.Count < 3) continue;
                    long child = Id(c.Props[1]), parent = Id(c.Props[2]);
                    if (geoms.ContainsKey(child) && models.ContainsKey(parent)) modelGeom[parent] = child;
                    else if (mats.ContainsKey(child) && models.ContainsKey(parent))
                    {
                        List<long> l;
                        if (!modelMats.TryGetValue(parent, out l)) { l = new List<long>(); modelMats[parent] = l; }
                        l.Add(child);
                    }
                    else if (models.ContainsKey(child) && models.ContainsKey(parent)) modelParent[child] = parent;
                }

            var scene = new Scene();
            scene.Materials.Clear();
            var matIndex = new Dictionary<long, int>();
            foreach (var kv in mats)
            {
                var p = Props70(kv.Value);
                var col = Vec(p, "DiffuseColor", Vec(p, "Diffuse", new Vector3(0.8f)));
                float factor = 1f;
                List<object> df;
                if (p.TryGetValue("DiffuseFactor", out df) && df.Count > 0) factor = (float)Num(df[0]);
                if (factor <= 0f) factor = 1f;
                float opacity = 1f;
                List<object> op;
                if (p.TryGetValue("Opacity", out op) && op.Count > 0) opacity = (float)Num(op[0]);
                if (opacity <= 0f) opacity = 1f;
                matIndex[kv.Key] = scene.Materials.Count;
                scene.Materials.Add(new Material { Name = CleanName(kv.Value.Props.Count > 1 ? kv.Value.Props[1] : "Material"), Color = new Vector4(col * Math.Min(1f, factor), opacity) });
            }
            int defaultMat = -1;
            Func<int> getDefault = () =>
            {
                if (defaultMat < 0) { defaultMat = scene.Materials.Count; scene.Materials.Add(new Material { Name = "Default" }); }
                return defaultMat;
            };

            var localCache = new Dictionary<long, Matrix4x4>();
            Func<long, Matrix4x4> local = id =>
            {
                Matrix4x4 m;
                if (localCache.TryGetValue(id, out m)) return m;
                var p = Props70(models[id]);
                var t = Vec(p, "Lcl Translation", Vector3.Zero);
                var r = Vec(p, "Lcl Rotation", Vector3.Zero);
                var s = Vec(p, "Lcl Scaling", Vector3.One);
                var pre = Vec(p, "PreRotation", Vector3.Zero);
                var post = Vec(p, "PostRotation", Vector3.Zero);
                Matrix4x4 postInv;
                if (!Matrix4x4.Invert(Euler(post), out postInv)) postInv = Matrix4x4.Identity;
                m = Matrix4x4.CreateScale(s) * postInv * Euler(r) * Euler(pre) * Matrix4x4.CreateTranslation(t);
                localCache[id] = m;
                return m;
            };
            Func<long, Matrix4x4> world = null;
            world = id =>
            {
                var m = local(id);
                long parent; int guard = 0;
                long cur = id;
                while (modelParent.TryGetValue(cur, out parent) && guard++ < 64)
                {
                    m = m * local(parent);
                    cur = parent;
                }
                return m;
            };

            foreach (var kv in modelGeom)
            {
                long modelId = kv.Key;
                var geom = geoms[kv.Value];
                List<long> ml;
                modelMats.TryGetValue(modelId, out ml);
                var matList = (ml ?? new List<long>()).Select(x => matIndex.ContainsKey(x) ? matIndex[x] : getDefault()).ToList();
                var mesh = BuildMesh(geom, matList, getDefault);
                if (mesh.Faces.Count == 0) continue;
                var gp = Props70(models[modelId]);
                var gt = Vec(gp, "GeometricTranslation", Vector3.Zero);
                var gr = Vec(gp, "GeometricRotation", Vector3.Zero);
                var gs = Vec(gp, "GeometricScaling", Vector3.One);
                if (gt != Vector3.Zero || gr != Vector3.Zero || gs != Vector3.One)
                {
                    var g = Matrix4x4.CreateScale(gs) * Euler(gr) * Matrix4x4.CreateTranslation(gt);
                    for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] = Vector3.Transform(mesh.Positions[i], g);
                    for (int i = 0; i < mesh.Normals.Count; i++) mesh.Normals[i] = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i], g) + new Vector3(1e-9f));
                }
                var o = new SceneObject { Name = CleanName(models[modelId].Props.Count > 1 ? models[modelId].Props[1] : "Mesh"), Mesh = mesh };
                o.SetWorld(world(modelId));
                if (mesh.Normals.Count == 0) o.Shading = Shading.Smooth;
                scene.Objects.Add(o);
            }

            // Global axis / unit conversion.
            var gs2 = Props70(global);
            float unit = 1f;
            List<object> u;
            if (gs2.TryGetValue("UnitScaleFactor", out u) && u.Count > 0) unit = (float)Num(u[0]);
            if (unit <= 0) unit = 1f;
            int upAxis = 1;
            List<object> ua;
            if (gs2.TryGetValue("UpAxis", out ua) && ua.Count > 0) upAxis = (int)Num(ua[0]);
            var conv = Matrix4x4.CreateScale(unit / 100f);
            if (upAxis == 2) conv = conv * FmtUtil.ZUpToYUp;
            FmtUtil.TransformScene(scene, conv);

            if (scene.Materials.Count == 0) getDefault();
            return scene;
        }

        static Mesh BuildMesh(Node geom, List<int> matList, Func<int> getDefault)
        {
            var m = new Mesh();
            var vNode = geom.Child("Vertices");
            var iNode = geom.Child("PolygonVertexIndex");
            if (vNode == null || iNode == null || vNode.Props.Count == 0 || iNode.Props.Count == 0) return m;
            var verts = Doubles(vNode.Props[0]);
            for (int i = 0; i + 2 < verts.Length; i += 3) m.Positions.Add(new Vector3((float)verts[i], (float)verts[i + 1], (float)verts[i + 2]));
            var pvi = Longs(iNode.Props[0]);

            // polygons: list of (cornerStart, count)
            var polys = new List<int[]>();
            var cur = new List<int>();
            for (int i = 0; i < pvi.Length; i++)
            {
                long v = pvi[i];
                bool end = v < 0;
                cur.Add(i);
                if (end)
                {
                    polys.Add(cur.ToArray());
                    cur.Clear();
                }
            }
            if (cur.Count >= 3) polys.Add(cur.ToArray());
            Func<int, int> ctrl = corner => { long v = pvi[corner]; return (int)(v < 0 ? ~v : v); };

            var normals = LayerData(geom, "LayerElementNormal", "Normals", "NormalsIndex", 3);
            var uvs = LayerData(geom, "LayerElementUV", "UV", "UVIndex", 2);
            if (normals != null) for (int i = 0; i + 2 < normals.Values.Length; i += 3) m.Normals.Add(new Vector3((float)normals.Values[i], (float)normals.Values[i + 1], (float)normals.Values[i + 2]));
            if (uvs != null) for (int i = 0; i + 1 < uvs.Values.Length; i += 2) m.UVs.Add(new Vector2((float)uvs.Values[i], (float)uvs.Values[i + 1]));

            long[] matIdx = null; bool allSame = true;
            var lem = geom.Child("LayerElementMaterial");
            if (lem != null)
            {
                var mi = lem.Child("Materials");
                if (mi != null && mi.Props.Count > 0) matIdx = Longs(mi.Props[0]);
                var mapping = lem.Child("MappingInformationType");
                allSame = mapping == null || Str(mapping.Props.FirstOrDefault()) != "ByPolygon";
            }

            for (int p = 0; p < polys.Count; p++)
            {
                var poly = polys[p];
                if (poly.Length < 3) continue;
                long local = 0;
                if (matIdx != null && matIdx.Length > 0) local = allSame ? matIdx[0] : (p < matIdx.Length ? matIdx[p] : 0);
                int mat = local >= 0 && local < matList.Count ? matList[(int)local] : (matList.Count > 0 ? matList[0] : getDefault());
                var f = new Face(new int[poly.Length], mat);
                for (int k = 0; k < poly.Length; k++) f.V[k] = ctrl(poly[k]);
                if (normals != null) f.N = poly.Select(c => normals.Index(c, ctrl(c), p)).ToArray();
                if (uvs != null) f.T = poly.Select(c => uvs.Index(c, ctrl(c), p)).ToArray();
                if (f.N != null && f.N.Any(x => x < 0 || x >= m.Normals.Count)) f.N = null;
                if (f.T != null && f.T.Any(x => x < 0 || x >= m.UVs.Count)) f.T = null;
                if (f.V.All(x => x >= 0 && x < m.Positions.Count)) m.Faces.Add(f);
            }
            return m;
        }

        sealed class Layer
        {
            public double[] Values;
            public long[] Indices;
            public string Mapping, Reference;

            public int Index(int corner, int controlPoint, int polygon)
            {
                int i;
                if (Mapping == "ByPolygonVertex") i = corner;
                else if (Mapping == "ByPolygon") i = polygon;
                else if (Mapping == "AllSame") i = 0;
                else i = controlPoint; // ByVertice / ByVertex / ByControlPoint
                if (Reference == "IndexToDirect" || Reference == "Index")
                    return Indices != null && i >= 0 && i < Indices.Length ? (int)Indices[i] : -1;
                return i;
            }
        }

        static Layer LayerData(Node geom, string layerName, string valuesName, string indexName, int comps)
        {
            var le = geom.Child(layerName);
            if (le == null) return null;
            var vals = le.Child(valuesName);
            if (vals == null || vals.Props.Count == 0) return null;
            var idx = le.Child(indexName);
            var map = le.Child("MappingInformationType");
            var refr = le.Child("ReferenceInformationType");
            return new Layer
            {
                Values = Doubles(vals.Props[0]),
                Indices = idx != null && idx.Props.Count > 0 ? Longs(idx.Props[0]) : null,
                Mapping = map != null ? Str(map.Props.FirstOrDefault()) : "ByPolygonVertex",
                Reference = refr != null ? Str(refr.Props.FirstOrDefault()) : "Direct"
            };
        }

        // ------------------------------------------------------------------ export (binary FBX 7.4)

        sealed class WNode
        {
            public string Name;
            public List<object> Props = new List<object>();
            public List<WNode> Children = new List<WNode>();
            public WNode(string name, params object[] props) { Name = name; Props.AddRange(props); }
            public WNode Add(WNode c) { Children.Add(c); return this; }
            public WNode Add(string name, params object[] props) { Children.Add(new WNode(name, props)); return this; }
        }

        static WNode P(string name, string type, string label, string flags, params object[] values)
        {
            var n = new WNode("P", name, type, label, flags);
            n.Props.AddRange(values);
            return n;
        }

        static void WriteNode(BinaryWriter w, WNode n)
        {
            long start = w.BaseStream.Position;
            w.Write(0u); w.Write((uint)n.Props.Count); w.Write(0u);
            var nameBytes = Encoding.ASCII.GetBytes(n.Name);
            w.Write((byte)nameBytes.Length); w.Write(nameBytes);
            long propStart = w.BaseStream.Position;
            foreach (var p in n.Props) WriteProp(w, p);
            long propLen = w.BaseStream.Position - propStart;
            foreach (var c in n.Children) WriteNode(w, c);
            if (n.Children.Count > 0 || n.Props.Count == 0) w.Write(new byte[13]);
            long end = w.BaseStream.Position;
            w.BaseStream.Position = start;
            w.Write((uint)end); w.Write((uint)n.Props.Count); w.Write((uint)propLen);
            w.BaseStream.Position = end;
        }

        static void WriteProp(BinaryWriter w, object p)
        {
            if (p is int) { w.Write((byte)'I'); w.Write((int)p); }
            else if (p is long) { w.Write((byte)'L'); w.Write((long)p); }
            else if (p is double) { w.Write((byte)'D'); w.Write((double)p); }
            else if (p is float) { w.Write((byte)'F'); w.Write((float)p); }
            else if (p is bool) { w.Write((byte)'C'); w.Write((byte)((bool)p ? 1 : 0)); }
            else if (p is short) { w.Write((byte)'Y'); w.Write((short)p); }
            else if (p is string)
            {
                var b = Encoding.UTF8.GetBytes((string)p);
                w.Write((byte)'S'); w.Write(b.Length); w.Write(b);
            }
            else if (p is double[])
            {
                var a = (double[])p;
                w.Write((byte)'d'); w.Write(a.Length); w.Write(0); w.Write(a.Length * 8);
                foreach (var v in a) w.Write(v);
            }
            else if (p is int[])
            {
                var a = (int[])p;
                w.Write((byte)'i'); w.Write(a.Length); w.Write(0); w.Write(a.Length * 4);
                foreach (var v in a) w.Write(v);
            }
            else throw new InvalidOperationException("Unsupported FBX property");
        }

        static string ObjName(string name, string cls) { return (name ?? "") + "\0\u0001" + cls; }

        static Vector3 ToEulerDeg(Quaternion q)
        {
            var m = Matrix4x4.CreateFromQuaternion(q);
            double x, y, z;
            double sy = -m.M13;
            if (sy >= 0.99999)
            {
                y = Math.PI / 2; x = Math.Atan2(m.M21, m.M22); z = 0;
            }
            else if (sy <= -0.99999)
            {
                y = -Math.PI / 2; x = Math.Atan2(-m.M21, m.M22); z = 0;
            }
            else
            {
                y = Math.Asin(sy); x = Math.Atan2(m.M23, m.M33); z = Math.Atan2(m.M12, m.M11);
            }
            double k = 180.0 / Math.PI;
            return new Vector3((float)(x * k), (float)(y * k), (float)(z * k));
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            long nextId = 1000000;
            var objects = new WNode("Objects");
            var connections = new WNode("Connections");
            var matIds = new long[scene.Materials.Count];
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var mat = scene.Materials[i];
                matIds[i] = nextId++;
                var props = new WNode("Properties70")
                    .Add(P("DiffuseColor", "Color", "", "A", (double)mat.Color.X, (double)mat.Color.Y, (double)mat.Color.Z))
                    .Add(P("Opacity", "double", "Number", "", (double)mat.Color.W))
                    .Add(P("Shininess", "double", "Number", "", (double)((1f - mat.Roughness) * 100f)));
                objects.Add(new WNode("Material", matIds[i], ObjName(mat.Name, "Material"), "")
                    .Add("Version", 102).Add("ShadingModel", "phong").Add("MultiLayer", 0).Add(props));
            }

            int modelCount = 0;
            foreach (var o in scene.Objects)
            {
                var m = o.Mesh;
                var faces = m.Faces.Where(f => m.IsValidFace(f)).ToList();
                if (faces.Count == 0) continue;
                long gid = nextId++, mid = nextId++;
                var verts = new double[m.Positions.Count * 3];
                for (int i = 0; i < m.Positions.Count; i++) { verts[i * 3] = m.Positions[i].X; verts[i * 3 + 1] = m.Positions[i].Y; verts[i * 3 + 2] = m.Positions[i].Z; }
                var pvi = new List<int>();
                var nrm = new List<double>();
                var smooth = o.Shading == Shading.Smooth ? m.ComputeVertexNormals() : null;
                bool hasUV = m.UVs.Count > 0 && faces.All(f => f.T != null && f.T.Length == f.V.Length);
                var uvIdx = new List<int>();
                // Materials used by this object, in local order.
                var used = faces.Select(f => Math.Max(0, Math.Min(scene.Materials.Count - 1, f.Material))).Distinct().ToList();
                var localMat = new List<int>();
                foreach (var f in faces)
                {
                    var flat = m.FaceNormal(f);
                    for (int k = 0; k < f.V.Length; k++)
                    {
                        pvi.Add(k == f.V.Length - 1 ? ~f.V[k] : f.V[k]);
                        var n = FmtUtil.CornerNormal(m, f, k, o.Shading, smooth, flat);
                        nrm.Add(n.X); nrm.Add(n.Y); nrm.Add(n.Z);
                        if (hasUV) uvIdx.Add(f.T[k]);
                    }
                    localMat.Add(used.IndexOf(Math.Max(0, Math.Min(scene.Materials.Count - 1, f.Material))));
                }
                var geom = new WNode("Geometry", gid, ObjName(o.Name, "Geometry"), "Mesh")
                    .Add(new WNode("Properties70"))
                    .Add("GeometryVersion", 124)
                    .Add("Vertices", verts)
                    .Add("PolygonVertexIndex", pvi.ToArray())
                    .Add(new WNode("LayerElementNormal", 0)
                        .Add("Version", 101).Add("Name", "")
                        .Add("MappingInformationType", "ByPolygonVertex")
                        .Add("ReferenceInformationType", "Direct")
                        .Add("Normals", nrm.ToArray()));
                if (hasUV)
                {
                    var uv = new double[m.UVs.Count * 2];
                    for (int i = 0; i < m.UVs.Count; i++) { uv[i * 2] = m.UVs[i].X; uv[i * 2 + 1] = m.UVs[i].Y; }
                    geom.Add(new WNode("LayerElementUV", 0)
                        .Add("Version", 101).Add("Name", "UVMap")
                        .Add("MappingInformationType", "ByPolygonVertex")
                        .Add("ReferenceInformationType", "IndexToDirect")
                        .Add("UV", uv).Add("UVIndex", uvIdx.ToArray()));
                }
                geom.Add(new WNode("LayerElementMaterial", 0)
                    .Add("Version", 101).Add("Name", "")
                    .Add("MappingInformationType", "ByPolygon")
                    .Add("ReferenceInformationType", "IndexToDirect")
                    .Add("Materials", localMat.ToArray()));
                var layer = new WNode("Layer", 0).Add("Version", 100)
                    .Add(new WNode("LayerElement").Add("Type", "LayerElementNormal").Add("TypedIndex", 0))
                    .Add(new WNode("LayerElement").Add("Type", "LayerElementMaterial").Add("TypedIndex", 0));
                if (hasUV) layer.Add(new WNode("LayerElement").Add("Type", "LayerElementUV").Add("TypedIndex", 0));
                geom.Add(layer);
                objects.Add(geom);

                var e = ToEulerDeg(o.Rotation);
                var mprops = new WNode("Properties70")
                    .Add(P("Lcl Translation", "Lcl Translation", "", "A", (double)o.Position.X, (double)o.Position.Y, (double)o.Position.Z))
                    .Add(P("Lcl Rotation", "Lcl Rotation", "", "A", (double)e.X, (double)e.Y, (double)e.Z))
                    .Add(P("Lcl Scaling", "Lcl Scaling", "", "A", (double)o.Scale.X, (double)o.Scale.Y, (double)o.Scale.Z))
                    .Add(P("DefaultAttributeIndex", "int", "Integer", "", 0));
                objects.Add(new WNode("Model", mid, ObjName(o.Name, "Model"), "Mesh")
                    .Add("Version", 232).Add(mprops).Add("Shading", true).Add("Culling", "CullingOff"));

                connections.Add(new WNode("C", "OO", mid, 0L));
                connections.Add(new WNode("C", "OO", gid, mid));
                foreach (int mi in used) connections.Add(new WNode("C", "OO", matIds[mi], mid));
                modelCount++;
            }

            var header = new WNode("FBXHeaderExtension")
                .Add("FBXHeaderVersion", 1003)
                .Add("FBXVersion", 7400)
                .Add("Creator", "Facet3D for Xbox");
            var gprops = new WNode("Properties70")
                .Add(P("UpAxis", "int", "Integer", "", 1))
                .Add(P("UpAxisSign", "int", "Integer", "", 1))
                .Add(P("FrontAxis", "int", "Integer", "", 2))
                .Add(P("FrontAxisSign", "int", "Integer", "", 1))
                .Add(P("CoordAxis", "int", "Integer", "", 0))
                .Add(P("CoordAxisSign", "int", "Integer", "", 1))
                .Add(P("OriginalUpAxis", "int", "Integer", "", 1))
                .Add(P("OriginalUpAxisSign", "int", "Integer", "", 1))
                .Add(P("UnitScaleFactor", "double", "Number", "", 100.0))
                .Add(P("OriginalUnitScaleFactor", "double", "Number", "", 100.0));
            var globalSettings = new WNode("GlobalSettings").Add("Version", 1000).Add(gprops);
            var definitions = new WNode("Definitions").Add("Version", 100).Add("Count", modelCount * 2 + scene.Materials.Count + 1)
                .Add(new WNode("ObjectType", "GlobalSettings").Add("Count", 1))
                .Add(new WNode("ObjectType", "Model").Add("Count", modelCount))
                .Add(new WNode("ObjectType", "Geometry").Add("Count", modelCount))
                .Add(new WNode("ObjectType", "Material").Add("Count", scene.Materials.Count));

            string name = baseName + ".fbx";
            using (var s = createFile(name))
            {
                var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.ASCII, true))
                {
                    w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
                    w.Write((byte)0); w.Write((byte)0x1A); w.Write((byte)0);
                    w.Write(7400u);
                    foreach (var n in new[] { header, globalSettings, new WNode("Documents").Add("Count", 0), new WNode("References"), definitions, objects, connections })
                        WriteNode(w, n);
                    w.Write(new byte[13]);
                    // Footer.
                    w.Write(new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e });
                    w.Write(new byte[4]);
                    int pad = (int)((16 - (ms.Length % 16)) % 16);
                    if (pad == 0) pad = 16;
                    w.Write(new byte[pad]);
                    w.Write(7400u);
                    w.Write(new byte[120]);
                    w.Write(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });
                    w.Flush();
                }
                ms.Position = 0;
                ms.CopyTo(s);
                s.Flush();
            }
            return name;
        }
    }
}
