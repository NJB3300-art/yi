using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>Object File Format (OFF / COFF).</summary>
    public sealed class OffFormat : IModelFormat
    {
        public string Name { get { return "OFF (Object File Format)"; } }
        public string[] Extensions { get { return new[] { ".off" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            // Line-based: each vertex and each face sits on its own line (extra tokens such as colours are optional).
            var rows = new List<string[]>();
            foreach (var raw in FmtUtil.Text(data).Split('\n'))
            {
                var l = raw;
                int hash = l.IndexOf('#');
                if (hash >= 0) l = l.Substring(0, hash);
                var tk = l.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length > 0) rows.Add(tk);
            }
            int r = 0;
            bool colors = false;
            if (rows.Count > 0 && rows[0][0].EndsWith("OFF", StringComparison.OrdinalIgnoreCase))
            {
                colors = rows[0][0].StartsWith("C", StringComparison.OrdinalIgnoreCase);
                if (rows[0].Length >= 3) rows[0] = new[] { rows[0][1], rows[0][2] };
                else r = 1;
            }
            int nv = int.Parse(rows[r][0]); int nf = int.Parse(rows[r][1]); r++;
            var o = new SceneObject { Name = FmtUtil.BaseName(fileName), Shading = Shading.Flat };
            var m = o.Mesh;
            if (colors) m.Colors = new List<Vector4>();
            for (int v = 0; v < nv && r < rows.Count; v++, r++)
            {
                var t = rows[r];
                m.Positions.Add(new Vector3(FmtUtil.F(t[0]), FmtUtil.F(t[1]), FmtUtil.F(t[2])));
                if (colors)
                {
                    var c = t.Length >= 6 ? new Vector4(FmtUtil.F(t[3]), FmtUtil.F(t[4]), FmtUtil.F(t[5]), t.Length >= 7 ? FmtUtil.F(t[6]) : 1f) : Vector4.One;
                    if (c.X > 1 || c.Y > 1 || c.Z > 1) c /= 255f;
                    m.Colors.Add(c);
                }
            }
            for (int f = 0; f < nf && r < rows.Count; f++, r++)
            {
                var t = rows[r];
                int n = int.Parse(t[0]);
                if (n < 3 || t.Length < n + 1) continue;
                var idx = new int[n];
                for (int k = 0; k < n; k++) idx[k] = int.Parse(t[k + 1]);
                m.Faces.Add(new Face(idx));
            }
            var s = new Scene();
            s.Objects.Add(o);
            return s;
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            var sb = new StringBuilder();
            var pos = new List<Vector3>();
            var faces = new List<int[]>();
            foreach (var o in scene.Objects)
            {
                var m = Scene.BakeWorld(o);
                int b = pos.Count;
                pos.AddRange(m.Positions);
                foreach (var f in m.Faces)
                {
                    if (!m.IsValidFace(f)) continue;
                    var nf = new int[f.V.Length];
                    for (int k = 0; k < nf.Length; k++) nf[k] = f.V[k] + b;
                    faces.Add(nf);
                }
            }
            sb.Append("OFF\n").Append(pos.Count).Append(' ').Append(faces.Count).Append(" 0\n");
            foreach (var p in pos) sb.Append(FmtUtil.S(p.X)).Append(' ').Append(FmtUtil.S(p.Y)).Append(' ').Append(FmtUtil.S(p.Z)).Append('\n');
            foreach (var f in faces)
            {
                sb.Append(f.Length);
                foreach (int v in f) sb.Append(' ').Append(v);
                sb.Append('\n');
            }
            string name = baseName + ".off";
            ObjFormat.WriteText(createFile(name), sb.ToString());
            return name;
        }
    }
}
