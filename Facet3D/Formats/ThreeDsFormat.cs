using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>Autodesk 3D Studio (.3ds): meshes, UVs, per-face materials (diffuse colour, texture name).</summary>
    public sealed class ThreeDsFormat : IModelFormat
    {
        public string Name { get { return "3D Studio (.3ds)"; } }
        public string[] Extensions { get { return new[] { ".3ds" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        // ------------------------------------------------------------------ import

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            var scene = new Scene();
            scene.Materials.Clear();
            var matIndex = new Dictionary<string, int>();
            if (data.Length < 6 || BitConverter.ToUInt16(data, 0) != 0x4D4D) throw new InvalidDataException("Not a 3DS file");
            ReadChunks(data, 6, Math.Min(data.Length, (int)BitConverter.ToUInt32(data, 2)), scene, matIndex, null, null);
            if (scene.Materials.Count == 0) scene.Materials.Add(new Material { Name = "Default" });
            // 3DS is Z-up.
            foreach (var o in scene.Objects)
                for (int i = 0; i < o.Mesh.Positions.Count; i++)
                {
                    var p = o.Mesh.Positions[i];
                    o.Mesh.Positions[i] = new Vector3(p.X, p.Z, -p.Y);
                }
            return scene;
        }

        static string ReadCString(byte[] d, ref int pos, int end)
        {
            int st = pos;
            while (pos < end && d[pos] != 0) pos++;
            var s = Encoding.ASCII.GetString(d, st, pos - st);
            pos++;
            return s;
        }

        static void ReadChunks(byte[] d, int pos, int end, Scene scene, Dictionary<string, int> matIndex, SceneObject obj, Material mat)
        {
            while (pos + 6 <= end)
            {
                ushort id = BitConverter.ToUInt16(d, pos);
                int len = (int)BitConverter.ToUInt32(d, pos + 2);
                if (len < 6) break;
                int cEnd = Math.Min(end, pos + len);
                int body = pos + 6;
                switch (id)
                {
                    case 0x3D3D: // editor
                        ReadChunks(d, body, cEnd, scene, matIndex, null, null);
                        break;
                    case 0x4000: // named object
                        {
                            int p = body;
                            string name = ReadCString(d, ref p, cEnd);
                            var o = new SceneObject { Name = name.Length > 0 ? name : "Object", Shading = Shading.Smooth };
                            ReadChunks(d, p, cEnd, scene, matIndex, o, null);
                            if (o.Mesh.Positions.Count > 0) scene.Objects.Add(o);
                            break;
                        }
                    case 0x4100: // triangle mesh
                        ReadChunks(d, body, cEnd, scene, matIndex, obj, null);
                        break;
                    case 0x4110: // vertices
                        if (obj != null)
                        {
                            int n = BitConverter.ToUInt16(d, body);
                            for (int i = 0; i < n && body + 2 + i * 12 + 12 <= cEnd; i++)
                            {
                                int b = body + 2 + i * 12;
                                obj.Mesh.Positions.Add(new Vector3(BitConverter.ToSingle(d, b), BitConverter.ToSingle(d, b + 4), BitConverter.ToSingle(d, b + 8)));
                            }
                        }
                        break;
                    case 0x4140: // uv
                        if (obj != null)
                        {
                            int n = BitConverter.ToUInt16(d, body);
                            for (int i = 0; i < n && body + 2 + i * 8 + 8 <= cEnd; i++)
                            {
                                int b = body + 2 + i * 8;
                                obj.Mesh.UVs.Add(new Vector2(BitConverter.ToSingle(d, b), BitConverter.ToSingle(d, b + 4)));
                            }
                        }
                        break;
                    case 0x4120: // faces
                        if (obj != null)
                        {
                            int n = BitConverter.ToUInt16(d, body);
                            int p = body + 2;
                            bool uv = obj.Mesh.UVs.Count > 0;
                            for (int i = 0; i < n && p + 8 <= cEnd; i++, p += 8)
                            {
                                var f = new Face(new int[] { BitConverter.ToUInt16(d, p), BitConverter.ToUInt16(d, p + 2), BitConverter.ToUInt16(d, p + 4) }, -1);
                                obj.Mesh.Faces.Add(f);
                            }
                            // Sub-chunks: face materials.
                            int sp = p;
                            while (sp + 6 <= cEnd)
                            {
                                ushort sid = BitConverter.ToUInt16(d, sp);
                                int slen = (int)BitConverter.ToUInt32(d, sp + 2);
                                if (slen < 6) break;
                                if (sid == 0x4130)
                                {
                                    int q = sp + 6;
                                    string mname = ReadCString(d, ref q, sp + slen);
                                    int mi = MatIndex(scene, matIndex, mname);
                                    int cnt = BitConverter.ToUInt16(d, q); q += 2;
                                    for (int k = 0; k < cnt && q + 2 <= sp + slen; k++, q += 2)
                                    {
                                        int fi = BitConverter.ToUInt16(d, q);
                                        if (fi < obj.Mesh.Faces.Count) obj.Mesh.Faces[fi].Material = mi;
                                    }
                                }
                                sp += slen;
                            }
                            int def = -1;
                            foreach (var f in obj.Mesh.Faces)
                            {
                                if (f.Material < 0)
                                {
                                    if (def < 0) def = MatIndex(scene, matIndex, "Default");
                                    f.Material = def;
                                }
                                if (uv) f.T = (int[])f.V.Clone();
                            }
                        }
                        break;
                    case 0xAFFF: // material
                        {
                            var m = new Material { Name = "Material" };
                            ReadChunks(d, body, cEnd, scene, matIndex, null, m);
                            int existing;
                            if (matIndex.TryGetValue(m.Name, out existing)) scene.Materials[existing] = m;
                            else { matIndex[m.Name] = scene.Materials.Count; scene.Materials.Add(m); }
                            break;
                        }
                    case 0xA000:
                        if (mat != null) { int p = body; mat.Name = ReadCString(d, ref p, cEnd); }
                        break;
                    case 0xA020: // diffuse
                        if (mat != null)
                        {
                            var c = ReadColor(d, body, cEnd);
                            mat.Color = new Vector4(c, mat.Color.W);
                        }
                        break;
                    case 0xA050: // transparency
                        if (mat != null && body + 8 <= cEnd && BitConverter.ToUInt16(d, body) == 0x0030)
                            mat.Color.W = 1f - BitConverter.ToUInt16(d, body + 6) / 100f;
                        break;
                    case 0xA200: // texture map
                        if (mat != null) ReadChunks(d, body, cEnd, scene, matIndex, null, mat);
                        break;
                    case 0xA300:
                        if (mat != null) { int p = body; mat.Texture = ReadCString(d, ref p, cEnd); }
                        break;
                }
                pos += len;
            }
        }

        static int MatIndex(Scene scene, Dictionary<string, int> matIndex, string name)
        {
            int mi;
            if (matIndex.TryGetValue(name, out mi)) return mi;
            mi = scene.Materials.Count;
            scene.Materials.Add(new Material { Name = name });
            matIndex[name] = mi;
            return mi;
        }

        static Vector3 ReadColor(byte[] d, int pos, int end)
        {
            while (pos + 6 <= end)
            {
                ushort id = BitConverter.ToUInt16(d, pos);
                int len = (int)BitConverter.ToUInt32(d, pos + 2);
                if (len < 6) break;
                if ((id == 0x0011 || id == 0x0012) && pos + 9 <= end) return new Vector3(d[pos + 6] / 255f, d[pos + 7] / 255f, d[pos + 8] / 255f);
                if ((id == 0x0010 || id == 0x0013) && pos + 18 <= end)
                    return new Vector3(BitConverter.ToSingle(d, pos + 6), BitConverter.ToSingle(d, pos + 10), BitConverter.ToSingle(d, pos + 14));
                pos += len;
            }
            return new Vector3(0.8f);
        }

        // ------------------------------------------------------------------ export

        sealed class ChunkWriter
        {
            public readonly BinaryWriter W;
            readonly Stack<long> _starts = new Stack<long>();
            public ChunkWriter(Stream s) { W = new BinaryWriter(s); }

            public void Begin(ushort id) { _starts.Push(W.BaseStream.Position); W.Write(id); W.Write(0u); }

            public void End()
            {
                long st = _starts.Pop();
                long end = W.BaseStream.Position;
                W.BaseStream.Position = st + 2;
                W.Write((uint)(end - st));
                W.BaseStream.Position = end;
            }

            public void CString(string s)
            {
                W.Write(Encoding.ASCII.GetBytes(s));
                W.Write((byte)0);
            }
        }

        static string ShortName(string n, int max)
        {
            n = FmtUtil.SafeName(n, "obj");
            return n.Length > max ? n.Substring(0, max) : n;
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            var ms = new MemoryStream();
            var cw = new ChunkWriter(ms);
            var matNames = new List<string>();
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                string n = ShortName(scene.Materials[i].Name, 12);
                while (matNames.Contains(n)) n = ShortName("M" + i + "_" + n, 12);
                matNames.Add(n);
            }

            cw.Begin(0x4D4D);
            cw.Begin(0x0002); cw.W.Write(3u); cw.End();
            cw.Begin(0x3D3D);
            cw.Begin(0x3D3E); cw.W.Write(3u); cw.End();
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var m = scene.Materials[i];
                cw.Begin(0xAFFF);
                cw.Begin(0xA000); cw.CString(matNames[i]); cw.End();
                cw.Begin(0xA020);
                cw.Begin(0x0011);
                cw.W.Write(ToByte(m.Color.X)); cw.W.Write(ToByte(m.Color.Y)); cw.W.Write(ToByte(m.Color.Z));
                cw.End();
                cw.End();
                cw.Begin(0xA050);
                cw.Begin(0x0030); cw.W.Write((ushort)Math.Round((1f - m.Color.W) * 100f)); cw.End();
                cw.End();
                if (!string.IsNullOrEmpty(m.Texture))
                {
                    cw.Begin(0xA200);
                    cw.Begin(0xA300); cw.CString(m.Texture); cw.End();
                    cw.End();
                }
                cw.End();
            }

            var usedNames = new HashSet<string>();
            foreach (var o in scene.Objects)
            {
                var src = Scene.BakeWorld(o);
                // Unweld per (position, uv) pair because 3DS stores one UV per vertex.
                bool hasUV = src.UVs.Count > 0 && src.Faces.All(f => f.T != null && f.T.Length == f.V.Length);
                var verts = new List<Vector3>(); var uvs = new List<Vector2>();
                var tris = new List<int[]>(); var triMat = new List<int>();
                var map = new Dictionary<long, int>();
                foreach (var f in src.Faces)
                {
                    if (!src.IsValidFace(f)) continue;
                    var idx = new int[f.V.Length];
                    for (int k = 0; k < f.V.Length; k++)
                    {
                        long key = hasUV ? ((long)f.V[k] << 32) | (uint)f.T[k] : f.V[k];
                        int vi;
                        if (!map.TryGetValue(key, out vi))
                        {
                            vi = verts.Count;
                            map[key] = vi;
                            var p = src.Positions[f.V[k]];
                            verts.Add(new Vector3(p.X, -p.Z, p.Y)); // Y-up -> Z-up
                            uvs.Add(hasUV && f.T[k] < src.UVs.Count ? src.UVs[f.T[k]] : Vector2.Zero);
                        }
                        idx[k] = vi;
                    }
                    for (int k = 1; k < idx.Length - 1; k++) { tris.Add(new[] { idx[0], idx[k], idx[k + 1] }); triMat.Add(Math.Max(0, Math.Min(scene.Materials.Count - 1, f.Material))); }
                }
                // 3DS indices are 16-bit: split into chunks of <= 65535 vertices.
                int part = 0;
                int t0 = 0;
                while (t0 < tris.Count)
                {
                    var localMap = new Dictionary<int, int>();
                    var pt = new List<int[]>(); var pm = new List<int>();
                    int t = t0;
                    while (t < tris.Count && pt.Count < 65535)
                    {
                        int need = tris[t].Count(v => !localMap.ContainsKey(v));
                        if (localMap.Count + need > 65535) break;
                        var lt = new int[3];
                        for (int k = 0; k < 3; k++)
                        {
                            int lv;
                            if (!localMap.TryGetValue(tris[t][k], out lv)) { lv = localMap.Count; localMap[tris[t][k]] = lv; }
                            lt[k] = lv;
                        }
                        pt.Add(lt); pm.Add(triMat[t]);
                        t++;
                    }
                    t0 = t;
                    var lverts = new Vector3[localMap.Count]; var luvs = new Vector2[localMap.Count];
                    foreach (var kv in localMap) { lverts[kv.Value] = verts[kv.Key]; luvs[kv.Value] = uvs[kv.Key]; }

                    string oname = ShortName(o.Name + (part > 0 ? "_" + part : ""), 10);
                    for (int n = 1; usedNames.Contains(oname); n++) oname = ShortName(o.Name, 7) + "_" + n;
                    usedNames.Add(oname);
                    part++;

                    cw.Begin(0x4000); cw.CString(oname);
                    cw.Begin(0x4100);
                    cw.Begin(0x4110); cw.W.Write((ushort)lverts.Length);
                    foreach (var v in lverts) { cw.W.Write(v.X); cw.W.Write(v.Y); cw.W.Write(v.Z); }
                    cw.End();
                    if (hasUV)
                    {
                        cw.Begin(0x4140); cw.W.Write((ushort)luvs.Length);
                        foreach (var uv in luvs) { cw.W.Write(uv.X); cw.W.Write(uv.Y); }
                        cw.End();
                    }
                    cw.Begin(0x4160);
                    float[] ident = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
                    foreach (var fv in ident) cw.W.Write(fv);
                    cw.End();
                    cw.Begin(0x4120); cw.W.Write((ushort)pt.Count);
                    foreach (var tri in pt) { cw.W.Write((ushort)tri[0]); cw.W.Write((ushort)tri[1]); cw.W.Write((ushort)tri[2]); cw.W.Write((ushort)7); }
                    foreach (var g in Enumerable.Range(0, pt.Count).GroupBy(i => pm[i]))
                    {
                        cw.Begin(0x4130); cw.CString(matNames[g.Key]);
                        var list = g.ToList();
                        cw.W.Write((ushort)list.Count);
                        foreach (int fi in list) cw.W.Write((ushort)fi);
                        cw.End();
                    }
                    cw.End(); // faces
                    cw.End(); // trimesh
                    cw.End(); // object
                }
            }
            cw.End(); // editor
            cw.End(); // main
            cw.W.Flush();

            string name = baseName + ".3ds";
            using (var s = createFile(name))
            {
                ms.Position = 0;
                ms.CopyTo(s);
                s.Flush();
            }
            return name;
        }

        static byte ToByte(float v) { return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255f))); }
    }
}
