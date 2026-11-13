using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>Wavefront OBJ + MTL.</summary>
    public sealed class ObjFormat : IModelFormat
    {
        public string Name { get { return "Wavefront OBJ (+MTL)"; } }
        public string[] Extensions { get { return new[] { ".obj" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        sealed class Builder
        {
            public SceneObject Obj;
            public Dictionary<int, int> P = new Dictionary<int, int>();
            public Dictionary<int, int> T = new Dictionary<int, int>();
            public Dictionary<int, int> N = new Dictionary<int, int>();
        }

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            var scene = new Scene();
            scene.Materials.Clear();
            var matIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var pos = new List<Vector3>();
            var col = new List<Vector4>();
            bool anyColor = false;
            var uv = new List<Vector2>();
            var nrm = new List<Vector3>();
            Builder cur = null;
            int curMat = 0;
            string pendingName = FmtUtil.BaseName(fileName);

            Func<Builder> ensure = () =>
            {
                if (cur == null)
                {
                    cur = new Builder { Obj = new SceneObject { Name = pendingName } };
                    scene.Objects.Add(cur.Obj);
                }
                return cur;
            };

            var text = FmtUtil.Text(data);
            var reader = new StringReader(text);
            string line;
            var sep = new[] { ' ', '\t' };
            string pendingLine = null;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.EndsWith("\\")) { pendingLine = (pendingLine ?? "") + line.Substring(0, line.Length - 1) + " "; continue; }
                if (pendingLine != null) { line = pendingLine + line; pendingLine = null; }
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tk = line.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                switch (tk[0])
                {
                    case "v":
                        pos.Add(new Vector3(FmtUtil.F(tk[1]), FmtUtil.F(tk[2]), FmtUtil.F(tk[3])));
                        if (tk.Length >= 7) { col.Add(new Vector4(FmtUtil.F(tk[4]), FmtUtil.F(tk[5]), FmtUtil.F(tk[6]), 1f)); anyColor = true; }
                        else col.Add(Vector4.One);
                        break;
                    case "vt":
                        uv.Add(new Vector2(FmtUtil.F(tk[1]), tk.Length > 2 ? FmtUtil.F(tk[2]) : 0f));
                        break;
                    case "vn":
                        nrm.Add(new Vector3(FmtUtil.F(tk[1]), FmtUtil.F(tk[2]), FmtUtil.F(tk[3])));
                        break;
                    case "o":
                    case "g":
                        {
                            string name = tk.Length > 1 ? line.Substring(tk[0].Length).Trim() : pendingName;
                            if (cur != null && cur.Obj.Mesh.Faces.Count == 0) cur.Obj.Name = name;
                            else { cur = null; pendingName = name; }
                            break;
                        }
                    case "usemtl":
                        {
                            string mn = tk.Length > 1 ? line.Substring(6).Trim() : "Default";
                            int mi;
                            if (!matIndex.TryGetValue(mn, out mi))
                            {
                                mi = scene.Materials.Count;
                                scene.Materials.Add(new Material { Name = mn });
                                matIndex[mn] = mi;
                            }
                            curMat = mi;
                            break;
                        }
                    case "mtllib":
                        if (tk.Length > 1 && openSibling != null)
                        {
                            try { LoadMtl(openSibling(line.Substring(6).Trim()), scene, matIndex); } catch { }
                        }
                        break;
                    case "f":
                    case "fo":
                        {
                            var b = ensure();
                            var m = b.Obj.Mesh;
                            int n = tk.Length - 1;
                            if (n < 3) break;
                            var fv = new int[n]; int[] ft = null; int[] fn = null;
                            bool okT = true, okN = true;
                            var ttmp = new int[n]; var ntmp = new int[n];
                            for (int k = 0; k < n; k++)
                            {
                                var parts = tk[k + 1].Split('/');
                                int pi = Resolve(parts[0], pos.Count);
                                if (pi < 0 || pi >= pos.Count) { fv = null; break; }
                                fv[k] = Map(b.P, pi, () =>
                                {
                                    m.Positions.Add(pos[pi]);
                                    if (m.Colors == null && anyColor) { m.Colors = new List<Vector4>(); for (int c = 0; c < m.Positions.Count - 1; c++) m.Colors.Add(Vector4.One); }
                                    if (m.Colors != null) m.Colors.Add(col[pi]);
                                    return m.Positions.Count - 1;
                                });
                                int ti = parts.Length > 1 && parts[1].Length > 0 ? Resolve(parts[1], uv.Count) : -1;
                                if (ti < 0 || ti >= uv.Count) okT = false;
                                else ttmp[k] = Map(b.T, ti, () => { m.UVs.Add(uv[ti]); return m.UVs.Count - 1; });
                                int ni = parts.Length > 2 && parts[2].Length > 0 ? Resolve(parts[2], nrm.Count) : -1;
                                if (ni < 0 || ni >= nrm.Count) okN = false;
                                else ntmp[k] = Map(b.N, ni, () => { m.Normals.Add(nrm[ni]); return m.Normals.Count - 1; });
                            }
                            if (fv == null) break;
                            if (okT) ft = ttmp;
                            if (okN) fn = ntmp;
                            m.Faces.Add(new Face(fv, curMat) { T = ft, N = fn });
                            break;
                        }
                }
            }
            if (scene.Materials.Count == 0) scene.Materials.Add(new Material { Name = "Default" });
            return scene;
        }

        static int Resolve(string s, int count)
        {
            int i;
            if (!int.TryParse(s, out i)) return -1;
            return i < 0 ? count + i : i - 1;
        }

        static int Map(Dictionary<int, int> d, int key, Func<int> create)
        {
            int v;
            if (d.TryGetValue(key, out v)) return v;
            v = create();
            d[key] = v;
            return v;
        }

        static void LoadMtl(Stream s, Scene scene, Dictionary<string, int> matIndex)
        {
            var data = FmtUtil.ReadAll(s);
            if (data == null) return;
            var reader = new StringReader(FmtUtil.Text(data));
            string line;
            Material cur = null;
            var sep = new[] { ' ', '\t' };
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tk = line.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                switch (tk[0])
                {
                    case "newmtl":
                        {
                            string n = line.Substring(6).Trim();
                            int mi;
                            if (matIndex.TryGetValue(n, out mi)) cur = scene.Materials[mi];
                            else
                            {
                                cur = new Material { Name = n };
                                matIndex[n] = scene.Materials.Count;
                                scene.Materials.Add(cur);
                            }
                            break;
                        }
                    case "Kd":
                        if (cur != null && tk.Length >= 4) cur.Color = new Vector4(FmtUtil.F(tk[1]), FmtUtil.F(tk[2]), FmtUtil.F(tk[3]), cur.Color.W);
                        break;
                    case "d":
                        if (cur != null && tk.Length >= 2) cur.Color.W = FmtUtil.F(tk[1]);
                        break;
                    case "Tr":
                        if (cur != null && tk.Length >= 2) cur.Color.W = 1f - FmtUtil.F(tk[1]);
                        break;
                    case "Pr":
                        if (cur != null && tk.Length >= 2) cur.Roughness = FmtUtil.F(tk[1]);
                        break;
                    case "Pm":
                        if (cur != null && tk.Length >= 2) cur.Metallic = FmtUtil.F(tk[1]);
                        break;
                    case "Ns":
                        if (cur != null && tk.Length >= 2) cur.Roughness = Math.Max(0f, Math.Min(1f, 1f - (float)Math.Sqrt(FmtUtil.F(tk[1]) / 1000f)));
                        break;
                    case "map_Kd":
                        if (cur != null && tk.Length >= 2) cur.Texture = tk[tk.Length - 1];
                        break;
                }
            }
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            string mtlName = baseName + ".mtl";
            var sb = new StringBuilder();
            sb.Append("# Facet3D OBJ export\n");
            sb.Append("mtllib ").Append(mtlName).Append('\n');
            int pBase = 1, tBase = 1, nBase = 1;
            foreach (var o in scene.Objects)
            {
                var m = Scene.BakeWorld(o);
                sb.Append("o ").Append(FmtUtil.SafeName(o.Name, "Object")).Append('\n');
                for (int i = 0; i < m.Positions.Count; i++)
                {
                    var p = m.Positions[i];
                    sb.Append("v ").Append(FmtUtil.S(p.X)).Append(' ').Append(FmtUtil.S(p.Y)).Append(' ').Append(FmtUtil.S(p.Z));
                    if (m.Colors != null && i < m.Colors.Count)
                    {
                        var c = m.Colors[i];
                        sb.Append(' ').Append(FmtUtil.S(c.X)).Append(' ').Append(FmtUtil.S(c.Y)).Append(' ').Append(FmtUtil.S(c.Z));
                    }
                    sb.Append('\n');
                }
                foreach (var t in m.UVs) sb.Append("vt ").Append(FmtUtil.S(t.X)).Append(' ').Append(FmtUtil.S(t.Y)).Append('\n');

                // Normals: write imported normals, plus smooth normals when the object is smooth-shaded.
                bool writeSmooth = o.Shading == Shading.Smooth;
                Vector3[] smooth = writeSmooth ? m.ComputeVertexNormals() : null;
                if (writeSmooth) foreach (var n in smooth) sb.Append("vn ").Append(FmtUtil.S(n.X)).Append(' ').Append(FmtUtil.S(n.Y)).Append(' ').Append(FmtUtil.S(n.Z)).Append('\n');
                else foreach (var n in m.Normals) sb.Append("vn ").Append(FmtUtil.S(n.X)).Append(' ').Append(FmtUtil.S(n.Y)).Append(' ').Append(FmtUtil.S(n.Z)).Append('\n');
                sb.Append(o.Shading == Shading.Flat ? "s off\n" : "s 1\n");

                int lastMat = -1;
                foreach (var f in m.Faces)
                {
                    if (!m.IsValidFace(f)) continue;
                    if (f.Material != lastMat)
                    {
                        lastMat = f.Material;
                        sb.Append("usemtl ").Append(FmtUtil.SafeName(scene.MaterialAt(lastMat).Name, "Material" + lastMat)).Append('\n');
                    }
                    bool hasT = f.T != null && f.T.Length == f.V.Length;
                    bool hasN = writeSmooth || (o.Shading != Shading.Flat && f.N != null && f.N.Length == f.V.Length);
                    sb.Append('f');
                    for (int k = 0; k < f.V.Length; k++)
                    {
                        sb.Append(' ').Append(f.V[k] + pBase);
                        if (hasT || hasN)
                        {
                            sb.Append('/');
                            if (hasT) sb.Append(f.T[k] + tBase);
                            if (hasN) sb.Append('/').Append((writeSmooth ? f.V[k] : f.N[k]) + nBase);
                        }
                    }
                    sb.Append('\n');
                }
                pBase += m.Positions.Count;
                tBase += m.UVs.Count;
                nBase += writeSmooth ? smooth.Length : m.Normals.Count;
            }
            WriteText(createFile(baseName + ".obj"), sb.ToString());

            var mtl = new StringBuilder();
            mtl.Append("# Facet3D MTL export\n");
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var mat = scene.Materials[i];
                mtl.Append("newmtl ").Append(FmtUtil.SafeName(mat.Name, "Material" + i)).Append('\n');
                mtl.Append("Kd ").Append(FmtUtil.S(mat.Color.X)).Append(' ').Append(FmtUtil.S(mat.Color.Y)).Append(' ').Append(FmtUtil.S(mat.Color.Z)).Append('\n');
                mtl.Append("Ka 0 0 0\nKs 0.2 0.2 0.2\n");
                mtl.Append("Ns ").Append(FmtUtil.S((1f - mat.Roughness) * (1f - mat.Roughness) * 1000f)).Append('\n');
                mtl.Append("d ").Append(FmtUtil.S(mat.Color.W)).Append('\n');
                mtl.Append("Pr ").Append(FmtUtil.S(mat.Roughness)).Append('\n');
                mtl.Append("Pm ").Append(FmtUtil.S(mat.Metallic)).Append('\n');
                mtl.Append("illum 2\n");
                if (!string.IsNullOrEmpty(mat.Texture)) mtl.Append("map_Kd ").Append(mat.Texture).Append('\n');
                mtl.Append('\n');
            }
            WriteText(createFile(mtlName), mtl.ToString());
            return baseName + ".obj";
        }

        internal static void WriteText(Stream s, string text)
        {
            using (s)
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
                s.Flush();
            }
        }
    }
}
