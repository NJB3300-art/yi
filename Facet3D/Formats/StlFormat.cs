using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>STL, ASCII and binary import; binary export.</summary>
    public sealed class StlFormat : IModelFormat
    {
        public string Name { get { return "STL (3D printing)"; } }
        public string[] Extensions { get { return new[] { ".stl" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            var scene = new Scene();
            bool binary = true;
            if (data.Length >= 84)
            {
                uint count = BitConverter.ToUInt32(data, 80);
                if (84L + count * 50L != data.Length)
                {
                    string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 512)).TrimStart();
                    if (head.StartsWith("solid", StringComparison.OrdinalIgnoreCase) && head.Contains("facet")) binary = false;
                }
            }
            else binary = false;

            if (binary)
            {
                var o = new SceneObject { Name = FmtUtil.BaseName(fileName) };
                var m = o.Mesh;
                uint count = BitConverter.ToUInt32(data, 80);
                long max = (data.Length - 84) / 50;
                if (count > max) count = (uint)max;
                var lookup = new Dictionary<Vector3, int>();
                int off = 84;
                for (uint i = 0; i < count; i++, off += 50)
                {
                    var f = new int[3];
                    for (int k = 0; k < 3; k++)
                    {
                        int b = off + 12 + k * 12;
                        var p = new Vector3(BitConverter.ToSingle(data, b), BitConverter.ToSingle(data, b + 4), BitConverter.ToSingle(data, b + 8));
                        f[k] = Index(m, lookup, p);
                    }
                    m.Faces.Add(new Face(f));
                }
                scene.Objects.Add(o);
            }
            else
            {
                var reader = new StringReader(FmtUtil.Text(data));
                string line;
                SceneObject o = null;
                Dictionary<Vector3, int> lookup = null;
                var corner = new List<int>();
                var sep = new[] { ' ', '\t' };
                while ((line = reader.ReadLine()) != null)
                {
                    var tk = line.Trim().Split(sep, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length == 0) continue;
                    string k0 = tk[0].ToLowerInvariant();
                    if (k0 == "solid")
                    {
                        o = new SceneObject { Name = tk.Length > 1 ? string.Join(" ", tk, 1, tk.Length - 1) : FmtUtil.BaseName(fileName) };
                        scene.Objects.Add(o);
                        lookup = new Dictionary<Vector3, int>();
                    }
                    else if (k0 == "outer") corner.Clear();
                    else if (k0 == "vertex" && tk.Length >= 4)
                    {
                        if (o == null) { o = new SceneObject { Name = FmtUtil.BaseName(fileName) }; scene.Objects.Add(o); lookup = new Dictionary<Vector3, int>(); }
                        corner.Add(Index(o.Mesh, lookup, new Vector3(FmtUtil.F(tk[1]), FmtUtil.F(tk[2]), FmtUtil.F(tk[3]))));
                    }
                    else if (k0 == "endloop" && o != null && corner.Count >= 3) o.Mesh.Faces.Add(new Face(corner.ToArray()));
                }
            }
            foreach (var o in scene.Objects) o.Shading = Shading.Flat;
            return scene;
        }

        static int Index(Mesh m, Dictionary<Vector3, int> lookup, Vector3 p)
        {
            int i;
            if (lookup.TryGetValue(p, out i)) return i;
            i = m.Positions.Count;
            m.Positions.Add(p);
            lookup[p] = i;
            return i;
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            var tris = new List<Vector3>();
            foreach (var o in scene.Objects)
            {
                var m = Scene.BakeWorld(o);
                foreach (var f in m.Faces)
                {
                    if (!m.IsValidFace(f)) continue;
                    for (int k = 1; k < f.V.Length - 1; k++)
                    {
                        tris.Add(m.Positions[f.V[0]]); tris.Add(m.Positions[f.V[k]]); tris.Add(m.Positions[f.V[k + 1]]);
                    }
                }
            }
            string name = baseName + ".stl";
            using (var s = createFile(name))
            using (var w = new BinaryWriter(s))
            {
                var header = new byte[80];
                var txt = Encoding.ASCII.GetBytes("Facet3D binary STL");
                Array.Copy(txt, header, txt.Length);
                w.Write(header);
                w.Write((uint)(tris.Count / 3));
                for (int i = 0; i < tris.Count; i += 3)
                {
                    var n = Vector3.Cross(tris[i + 1] - tris[i], tris[i + 2] - tris[i]);
                    float l = n.Length();
                    n = l > 1e-12f ? n / l : Vector3.Zero;
                    Write(w, n); Write(w, tris[i]); Write(w, tris[i + 1]); Write(w, tris[i + 2]);
                    w.Write((ushort)0);
                }
                w.Flush();
            }
            return name;
        }

        static void Write(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    }
}
