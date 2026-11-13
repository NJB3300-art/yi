using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>Stanford PLY: ASCII / binary little / big endian import, binary little endian export (with vertex colours).</summary>
    public sealed class PlyFormat : IModelFormat
    {
        public string Name { get { return "Stanford PLY"; } }
        public string[] Extensions { get { return new[] { ".ply" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        sealed class Prop { public string Name; public string Type; public bool IsList; public string CountType; }
        sealed class Element { public string Name; public int Count; public List<Prop> Props = new List<Prop>(); }

        sealed class Reader
        {
            readonly byte[] _d; int _p; readonly int _mode; // 0 ascii, 1 LE, 2 BE
            readonly string[] _tokens; int _ti;
            public Reader(byte[] d, int start, int mode)
            {
                _d = d; _p = start; _mode = mode;
                if (mode == 0)
                    _tokens = Encoding.ASCII.GetString(d, start, d.Length - start)
                        .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }

            public double Read(string type)
            {
                if (_mode == 0)
                {
                    if (_ti >= _tokens.Length) throw new EndOfStreamException();
                    return double.Parse(_tokens[_ti++], NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                int size = Size(type);
                if (_p + size > _d.Length) throw new EndOfStreamException();
                var b = new byte[size];
                Array.Copy(_d, _p, b, 0, size);
                _p += size;
                if ((_mode == 2) == BitConverter.IsLittleEndian) Array.Reverse(b);
                switch (type)
                {
                    case "char": case "int8": return (sbyte)b[0];
                    case "uchar": case "uint8": return b[0];
                    case "short": case "int16": return BitConverter.ToInt16(b, 0);
                    case "ushort": case "uint16": return BitConverter.ToUInt16(b, 0);
                    case "int": case "int32": return BitConverter.ToInt32(b, 0);
                    case "uint": case "uint32": return BitConverter.ToUInt32(b, 0);
                    case "float": case "float32": return BitConverter.ToSingle(b, 0);
                    case "double": case "float64": return BitConverter.ToDouble(b, 0);
                }
                throw new InvalidDataException("Unknown PLY type " + type);
            }

            static int Size(string t)
            {
                switch (t)
                {
                    case "char": case "int8": case "uchar": case "uint8": return 1;
                    case "short": case "int16": case "ushort": case "uint16": return 2;
                    case "int": case "int32": case "uint": case "uint32": case "float": case "float32": return 4;
                    case "double": case "float64": return 8;
                }
                throw new InvalidDataException("Unknown PLY type " + t);
            }
        }

        static bool IsIntType(string t) { return !(t == "float" || t == "float32" || t == "double" || t == "float64"); }

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            // Locate end_header.
            int headerEnd = -1;
            var marker = Encoding.ASCII.GetBytes("end_header");
            for (int i = 0; i + marker.Length <= data.Length && i < 1 << 20; i++)
            {
                bool ok = true;
                for (int k = 0; k < marker.Length; k++) if (data[i + k] != marker[k]) { ok = false; break; }
                if (!ok) continue;
                int j = i + marker.Length;
                while (j < data.Length && data[j] != '\n') j++;
                headerEnd = j + 1;
                break;
            }
            if (headerEnd < 0) throw new InvalidDataException("PLY header not found");
            var header = Encoding.ASCII.GetString(data, 0, headerEnd).Split('\n');
            int mode = 0;
            var elements = new List<Element>();
            foreach (var raw in header)
            {
                var tk = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length == 0) continue;
                if (tk[0] == "format") mode = tk[1] == "ascii" ? 0 : tk[1] == "binary_big_endian" ? 2 : 1;
                else if (tk[0] == "element") elements.Add(new Element { Name = tk[1], Count = int.Parse(tk[2], CultureInfo.InvariantCulture) });
                else if (tk[0] == "property" && elements.Count > 0)
                {
                    var e = elements[elements.Count - 1];
                    if (tk[1] == "list") e.Props.Add(new Prop { IsList = true, CountType = tk[2], Type = tk[3], Name = tk[4] });
                    else e.Props.Add(new Prop { Type = tk[1], Name = tk[2] });
                }
            }

            var o = new SceneObject { Name = FmtUtil.BaseName(fileName) };
            var m = o.Mesh;
            var r = new Reader(data, headerEnd, mode);
            bool hasNormals = false, hasUV = false, hasColor = false;
            foreach (var e in elements)
            {
                if (e.Name == "vertex")
                {
                    foreach (var p in e.Props)
                    {
                        if (p.Name == "nx") hasNormals = true;
                        if (p.Name == "s" || p.Name == "u" || p.Name == "texture_u" || p.Name == "texture_s") hasUV = true;
                        if (p.Name == "red" || p.Name == "r" || p.Name == "diffuse_red") hasColor = true;
                    }
                    if (hasColor) m.Colors = new List<Vector4>(e.Count);
                }
                for (int i = 0; i < e.Count; i++)
                {
                    float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, u = 0, v = 0, cr = 1, cg = 1, cb = 1, ca = 1;
                    List<int> idx = null;
                    foreach (var p in e.Props)
                    {
                        if (p.IsList)
                        {
                            int n = (int)r.Read(p.CountType);
                            var vals = new List<int>(n);
                            for (int k = 0; k < n; k++) vals.Add((int)r.Read(p.Type));
                            if (p.Name == "vertex_indices" || p.Name == "vertex_index") idx = vals;
                            continue;
                        }
                        double val = r.Read(p.Type);
                        float cv = IsIntType(p.Type) ? (float)(val / 255.0) : (float)val;
                        switch (p.Name)
                        {
                            case "x": x = (float)val; break;
                            case "y": y = (float)val; break;
                            case "z": z = (float)val; break;
                            case "nx": nx = (float)val; break;
                            case "ny": ny = (float)val; break;
                            case "nz": nz = (float)val; break;
                            case "s": case "u": case "texture_u": case "texture_s": u = (float)val; break;
                            case "t": case "v": case "texture_v": case "texture_t": v = (float)val; break;
                            case "red": case "r": case "diffuse_red": cr = cv; break;
                            case "green": case "g": case "diffuse_green": cg = cv; break;
                            case "blue": case "b": case "diffuse_blue": cb = cv; break;
                            case "alpha": case "a": ca = cv; break;
                        }
                    }
                    if (e.Name == "vertex")
                    {
                        m.Positions.Add(new Vector3(x, y, z));
                        if (hasNormals) m.Normals.Add(new Vector3(nx, ny, nz));
                        if (hasUV) m.UVs.Add(new Vector2(u, v));
                        if (hasColor) m.Colors.Add(new Vector4(cr, cg, cb, ca));
                    }
                    else if (e.Name == "face" && idx != null && idx.Count >= 3)
                    {
                        var arr = idx.ToArray();
                        var f = new Face(arr);
                        if (hasNormals) f.N = (int[])arr.Clone();
                        if (hasUV) f.T = (int[])arr.Clone();
                        m.Faces.Add(f);
                    }
                }
            }
            if (!hasNormals) o.Shading = Shading.Smooth;
            var scene = new Scene();
            scene.Objects.Add(o);
            return scene;
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            // Merge everything into one mesh (PLY holds a single mesh); bake material colours into vertex colours.
            var pos = new List<Vector3>();
            var col = new List<Vector4>();
            var faces = new List<int[]>();
            foreach (var o in scene.Objects)
            {
                var m = Scene.BakeWorld(o);
                int baseIdx = pos.Count;
                var vc = new Vector4[m.Positions.Count];
                var set = new bool[m.Positions.Count];
                foreach (var f in m.Faces)
                {
                    if (!m.IsValidFace(f)) continue;
                    var mc = scene.MaterialAt(f.Material).Color;
                    foreach (int v in f.V) if (!set[v]) { set[v] = true; vc[v] = mc; }
                    var nf = new int[f.V.Length];
                    for (int k = 0; k < nf.Length; k++) nf[k] = f.V[k] + baseIdx;
                    if (nf.Length <= 255) faces.Add(nf);
                    else for (int k = 1; k < nf.Length - 1; k++) faces.Add(new[] { nf[0], nf[k], nf[k + 1] });
                }
                for (int i = 0; i < m.Positions.Count; i++)
                {
                    pos.Add(m.Positions[i]);
                    var c = set[i] ? vc[i] : Vector4.One;
                    if (m.Colors != null && i < m.Colors.Count) c *= m.Colors[i];
                    col.Add(c);
                }
            }
            string name = baseName + ".ply";
            using (var s = createFile(name))
            {
                var h = new StringBuilder();
                h.Append("ply\nformat binary_little_endian 1.0\ncomment Facet3D export\n");
                h.Append("element vertex ").Append(pos.Count).Append('\n');
                h.Append("property float x\nproperty float y\nproperty float z\n");
                h.Append("property uchar red\nproperty uchar green\nproperty uchar blue\nproperty uchar alpha\n");
                h.Append("element face ").Append(faces.Count).Append('\n');
                h.Append("property list uchar int vertex_indices\nend_header\n");
                var hb = Encoding.ASCII.GetBytes(h.ToString());
                s.Write(hb, 0, hb.Length);
                using (var w = new BinaryWriter(s))
                {
                    for (int i = 0; i < pos.Count; i++)
                    {
                        w.Write(pos[i].X); w.Write(pos[i].Y); w.Write(pos[i].Z);
                        w.Write(ToByte(col[i].X)); w.Write(ToByte(col[i].Y)); w.Write(ToByte(col[i].Z)); w.Write(ToByte(col[i].W));
                    }
                    foreach (var f in faces)
                    {
                        w.Write((byte)f.Length);
                        foreach (int v in f) w.Write(v);
                    }
                    w.Flush();
                }
            }
            return name;
        }

        static byte ToByte(float v) { return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255f))); }
    }
}
