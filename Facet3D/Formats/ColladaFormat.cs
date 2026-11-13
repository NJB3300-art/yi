using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace Facet3D
{
    /// <summary>COLLADA 1.4/1.5 (.dae): geometry (triangles/polylist/polygons), node transforms, diffuse materials.</summary>
    public sealed class ColladaFormat : IModelFormat
    {
        public string Name { get { return "COLLADA (.dae)"; } }
        public string[] Extensions { get { return new[] { ".dae" }; } }
        public bool CanImport { get { return true; } }
        public bool CanExport { get { return true; } }

        static IEnumerable<XElement> Els(XElement e, string name)
        {
            if (e == null) return Enumerable.Empty<XElement>();
            return e.Elements().Where(x => x.Name.LocalName == name);
        }

        static XElement El(XElement e, string name) { return Els(e, name).FirstOrDefault(); }

        static IEnumerable<XElement> Desc(XElement e, string name)
        {
            if (e == null) return Enumerable.Empty<XElement>();
            return e.Descendants().Where(x => x.Name.LocalName == name);
        }

        static string Attr(XElement e, string name)
        {
            var a = e == null ? null : e.Attribute(name);
            return a == null ? null : a.Value;
        }

        static float[] Floats(string s)
        {
            if (string.IsNullOrEmpty(s)) return new float[0];
            var parts = s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var r = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++) r[i] = FmtUtil.F(parts[i]);
            return r;
        }

        static int[] Ints(string s)
        {
            if (string.IsNullOrEmpty(s)) return new int[0];
            var parts = s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var r = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out r[i]);
            return r;
        }

        static string Ref(string url) { return url != null && url.StartsWith("#") ? url.Substring(1) : url; }

        sealed class Source { public float[] Data; public int Stride; }

        public Scene Import(byte[] data, string fileName, SiblingOpener openSibling)
        {
            XDocument doc;
            using (var ms = new MemoryStream(data)) doc = XDocument.Load(ms);
            var root = doc.Root;
            var scene = new Scene();
            scene.Materials.Clear();

            // Materials: material id -> effect diffuse colour.
            var images = new Dictionary<string, string>();
            foreach (var img in Desc(El(root, "library_images"), "image"))
            {
                var init = Desc(img, "init_from").FirstOrDefault();
                if (init != null) images[Attr(img, "id") ?? ""] = (El(init, "ref") != null ? El(init, "ref").Value : init.Value).Trim();
            }
            var effects = new Dictionary<string, Material>();
            foreach (var fx in Els(El(root, "library_effects"), "effect"))
            {
                var mat = new Material { Name = Attr(fx, "name") ?? Attr(fx, "id") };
                var diffuse = Desc(fx, "diffuse").FirstOrDefault();
                var col = diffuse == null ? null : El(diffuse, "color");
                if (col != null)
                {
                    var c = Floats(col.Value);
                    if (c.Length >= 3) mat.Color = new Vector4(c[0], c[1], c[2], c.Length >= 4 ? c[3] : 1f);
                }
                var tex = diffuse == null ? null : El(diffuse, "texture");
                if (tex != null)
                {
                    string t = Attr(tex, "texture") ?? "";
                    // texture attr points to a sampler; resolve sampler->surface->image when possible.
                    string img = t;
                    foreach (var np in Desc(fx, "newparam"))
                        if (Attr(np, "sid") == t)
                        {
                            var src = Desc(np, "source").FirstOrDefault();
                            if (src != null)
                                foreach (var np2 in Desc(fx, "newparam"))
                                    if (Attr(np2, "sid") == src.Value.Trim())
                                    {
                                        var init = Desc(np2, "init_from").FirstOrDefault();
                                        if (init != null) img = init.Value.Trim();
                                    }
                        }
                    string file;
                    mat.Texture = images.TryGetValue(img, out file) ? file : img;
                }
                var transp = Desc(fx, "transparency").FirstOrDefault();
                if (transp != null && El(transp, "float") != null)
                {
                    float a = FmtUtil.F(El(transp, "float").Value);
                    if (a > 0f && a < 1f) mat.Color.W = a;
                }
                effects[Attr(fx, "id") ?? ""] = mat;
            }
            var matIndex = new Dictionary<string, int>();
            foreach (var m in Els(El(root, "library_materials"), "material"))
            {
                var ie = El(m, "instance_effect");
                Material fxMat;
                var mat = ie != null && effects.TryGetValue(Ref(Attr(ie, "url")) ?? "", out fxMat) ? fxMat.Clone() : new Material();
                mat.Name = Attr(m, "name") ?? Attr(m, "id") ?? mat.Name;
                matIndex[Attr(m, "id") ?? ""] = scene.Materials.Count;
                scene.Materials.Add(mat);
            }
            int defaultMat = -1;
            Func<int> getDefault = () =>
            {
                if (defaultMat < 0) { defaultMat = scene.Materials.Count; scene.Materials.Add(new Material { Name = "Default" }); }
                return defaultMat;
            };

            // Geometries.
            var geoms = new Dictionary<string, XElement>();
            foreach (var g in Els(El(root, "library_geometries"), "geometry")) geoms[Attr(g, "id") ?? ""] = g;

            // Up axis and units.
            var asset = El(root, "asset");
            string up = El(asset, "up_axis") != null ? El(asset, "up_axis").Value.Trim() : "Y_UP";
            float meter = 1f;
            var unit = El(asset, "unit");
            if (unit != null && Attr(unit, "meter") != null) meter = FmtUtil.F(Attr(unit, "meter"));
            if (meter <= 0) meter = 1f;
            var rootM = Matrix4x4.CreateScale(meter);
            if (up == "Z_UP") rootM = rootM * FmtUtil.ZUpToYUp;
            else if (up == "X_UP") rootM = rootM * Matrix4x4.CreateRotationZ((float)(Math.PI / 2));

            var vsLib = El(root, "library_visual_scenes");
            var visual = vsLib == null ? null : Els(vsLib, "visual_scene").FirstOrDefault();
            var sceneEl = El(root, "scene");
            var ivs = sceneEl == null ? null : El(sceneEl, "instance_visual_scene");
            if (ivs != null && vsLib != null)
            {
                string id = Ref(Attr(ivs, "url"));
                var match = Els(vsLib, "visual_scene").FirstOrDefault(v => Attr(v, "id") == id);
                if (match != null) visual = match;
            }
            var libNodes = new Dictionary<string, XElement>();
            foreach (var n in Desc(El(root, "library_nodes"), "node")) libNodes[Attr(n, "id") ?? ""] = n;

            if (visual != null)
            {
                foreach (var n in Els(visual, "node")) VisitNode(n, rootM, geoms, libNodes, matIndex, getDefault, scene, 0);
            }
            else
            {
                foreach (var kv in geoms)
                {
                    var o = new SceneObject { Name = Attr(kv.Value, "name") ?? kv.Key, Mesh = BuildMesh(kv.Value, new Dictionary<string, string>(), matIndex, getDefault) };
                    o.SetWorld(rootM);
                    scene.Objects.Add(o);
                }
            }
            if (scene.Materials.Count == 0) getDefault();
            return scene;
        }

        void VisitNode(XElement node, Matrix4x4 parent, Dictionary<string, XElement> geoms, Dictionary<string, XElement> libNodes,
            Dictionary<string, int> matIndex, Func<int> getDefault, Scene scene, int depth)
        {
            if (depth > 64) return;
            var local = Matrix4x4.Identity;
            // COLLADA lists transforms outer-to-inner (column vectors); with row vectors we prepend each one.
            foreach (var t in node.Elements())
            {
                var v = Floats(t.Value);
                Matrix4x4 m;
                switch (t.Name.LocalName)
                {
                    case "matrix":
                        if (v.Length < 16) continue;
                        m = new Matrix4x4(v[0], v[4], v[8], v[12], v[1], v[5], v[9], v[13], v[2], v[6], v[10], v[14], v[3], v[7], v[11], v[15]);
                        break;
                    case "translate":
                        if (v.Length < 3) continue;
                        m = Matrix4x4.CreateTranslation(v[0], v[1], v[2]);
                        break;
                    case "rotate":
                        if (v.Length < 4) continue;
                        var axis = new Vector3(v[0], v[1], v[2]);
                        if (axis.LengthSquared() < 1e-12f) continue;
                        m = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), v[3] * (float)Math.PI / 180f);
                        break;
                    case "scale":
                        if (v.Length < 3) continue;
                        m = Matrix4x4.CreateScale(v[0], v[1], v[2]);
                        break;
                    default: continue;
                }
                local = m * local;
            }
            var world = local * parent;
            foreach (var ig in Els(node, "instance_geometry"))
            {
                XElement g;
                if (!geoms.TryGetValue(Ref(Attr(ig, "url")) ?? "", out g)) continue;
                var bind = new Dictionary<string, string>();
                foreach (var im in Desc(ig, "instance_material")) bind[Attr(im, "symbol") ?? ""] = Ref(Attr(im, "target"));
                var o = new SceneObject { Name = Attr(node, "name") ?? Attr(node, "id") ?? Attr(g, "name") ?? "Mesh", Mesh = BuildMesh(g, bind, matIndex, getDefault) };
                o.SetWorld(world);
                scene.Objects.Add(o);
            }
            foreach (var inst in Els(node, "instance_node"))
            {
                XElement ln;
                if (libNodes.TryGetValue(Ref(Attr(inst, "url")) ?? "", out ln)) VisitNode(ln, world, geoms, libNodes, matIndex, getDefault, scene, depth + 1);
            }
            foreach (var child in Els(node, "node")) VisitNode(child, world, geoms, libNodes, matIndex, getDefault, scene, depth + 1);
        }

        static Mesh BuildMesh(XElement geom, Dictionary<string, string> bind, Dictionary<string, int> matIndex, Func<int> getDefault)
        {
            var mesh = new Mesh();
            var me = El(geom, "mesh");
            if (me == null) return mesh;
            var sources = new Dictionary<string, Source>();
            foreach (var s in Els(me, "source"))
            {
                var fa = El(s, "float_array");
                var acc = Desc(s, "accessor").FirstOrDefault();
                int stride = 1;
                if (acc != null && Attr(acc, "stride") != null) int.TryParse(Attr(acc, "stride"), out stride);
                sources[Attr(s, "id") ?? ""] = new Source { Data = Floats(fa != null ? fa.Value : null), Stride = Math.Max(1, stride) };
            }
            var vertsEl = El(me, "vertices");
            string vertsId = Attr(vertsEl, "id");
            Source posSrc = null, vNormSrc = null, vTexSrc = null;
            foreach (var inp in Els(vertsEl, "input"))
            {
                Source s;
                if (!sources.TryGetValue(Ref(Attr(inp, "source")) ?? "", out s)) continue;
                string sem = Attr(inp, "semantic");
                if (sem == "POSITION") posSrc = s;
                else if (sem == "NORMAL") vNormSrc = s;
                else if (sem == "TEXCOORD") vTexSrc = s;
            }
            if (posSrc == null) return mesh;
            for (int i = 0; i + 2 < posSrc.Data.Length; i += posSrc.Stride)
                mesh.Positions.Add(new Vector3(posSrc.Data[i], posSrc.Data[i + 1], posSrc.Data[i + 2]));

            var normalBase = new Dictionary<Source, int>();
            var uvBase = new Dictionary<Source, int>();
            Func<Source, int> nBase = s =>
            {
                int b;
                if (normalBase.TryGetValue(s, out b)) return b;
                b = mesh.Normals.Count;
                for (int i = 0; i + 2 < s.Data.Length; i += s.Stride) mesh.Normals.Add(new Vector3(s.Data[i], s.Data[i + 1], s.Data[i + 2]));
                normalBase[s] = b;
                return b;
            };
            Func<Source, int> tBase = s =>
            {
                int b;
                if (uvBase.TryGetValue(s, out b)) return b;
                b = mesh.UVs.Count;
                for (int i = 0; i + 1 < s.Data.Length; i += s.Stride) mesh.UVs.Add(new Vector2(s.Data[i], s.Data[i + 1]));
                uvBase[s] = b;
                return b;
            };

            foreach (var prim in me.Elements())
            {
                string kind = prim.Name.LocalName;
                if (kind != "triangles" && kind != "polylist" && kind != "polygons" && kind != "trifans" && kind != "tristrips") continue;
                int vOff = -1, nOff = -1, tOff = -1, stride = 0;
                Source nSrc = null, tSrc = null;
                foreach (var inp in Els(prim, "input"))
                {
                    int off = 0; int.TryParse(Attr(inp, "offset"), out off);
                    stride = Math.Max(stride, off + 1);
                    string sem = Attr(inp, "semantic");
                    string src = Ref(Attr(inp, "source"));
                    if (sem == "VERTEX") vOff = off;
                    else if (sem == "NORMAL" && nSrc == null && sources.ContainsKey(src ?? "")) { nOff = off; nSrc = sources[src]; }
                    else if (sem == "TEXCOORD" && tSrc == null && sources.ContainsKey(src ?? "")) { tOff = off; tSrc = sources[src]; }
                }
                if (vOff < 0) continue;
                // Normals/UVs that live on <vertices> share the position index.
                if (nSrc == null && vNormSrc != null) { nSrc = vNormSrc; nOff = vOff; }
                if (tSrc == null && vTexSrc != null) { tSrc = vTexSrc; tOff = vOff; }
                int nb = nSrc != null ? nBase(nSrc) : 0;
                int tb = tSrc != null ? tBase(tSrc) : 0;

                string symbol = Attr(prim, "material");
                int mat;
                string target;
                if (symbol != null && bind.TryGetValue(symbol, out target) && target != null && matIndex.ContainsKey(target)) mat = matIndex[target];
                else if (symbol != null && matIndex.ContainsKey(symbol)) mat = matIndex[symbol];
                else mat = getDefault();

                var polys = new List<int[]>(); // each = flattened index tuples
                if (kind == "polylist")
                {
                    var p = Ints(El(prim, "p") != null ? El(prim, "p").Value : null);
                    var vc = Ints(El(prim, "vcount") != null ? El(prim, "vcount").Value : null);
                    int pos = 0;
                    foreach (int c in vc)
                    {
                        if (pos + c * stride > p.Length) break;
                        var arr = new int[c * stride];
                        Array.Copy(p, pos, arr, 0, arr.Length);
                        polys.Add(arr);
                        pos += c * stride;
                    }
                }
                else if (kind == "triangles")
                {
                    var p = Ints(El(prim, "p") != null ? El(prim, "p").Value : null);
                    for (int i = 0; i + 3 * stride <= p.Length; i += 3 * stride)
                    {
                        var arr = new int[3 * stride];
                        Array.Copy(p, i, arr, 0, arr.Length);
                        polys.Add(arr);
                    }
                }
                else
                {
                    foreach (var pe in Els(prim, "p"))
                    {
                        var p = Ints(pe.Value);
                        int c = p.Length / stride;
                        if (c < 3) continue;
                        if (kind == "polygons" || kind == "trifans") polys.Add(p);
                        else
                            for (int i = 0; i + 2 < c; i++)
                            {
                                var arr = new int[3 * stride];
                                int[] order = (i & 1) == 0 ? new[] { i, i + 1, i + 2 } : new[] { i + 1, i, i + 2 };
                                for (int k = 0; k < 3; k++) Array.Copy(p, order[k] * stride, arr, k * stride, stride);
                                polys.Add(arr);
                            }
                    }
                }

                foreach (var poly in polys)
                {
                    int c = poly.Length / stride;
                    if (c < 3) continue;
                    var f = new Face(new int[c], mat);
                    if (nSrc != null) f.N = new int[c];
                    if (tSrc != null) f.T = new int[c];
                    bool ok = true;
                    for (int k = 0; k < c; k++)
                    {
                        f.V[k] = poly[k * stride + vOff];
                        if (f.V[k] < 0 || f.V[k] >= mesh.Positions.Count) ok = false;
                        if (f.N != null) f.N[k] = nb + poly[k * stride + nOff];
                        if (f.T != null) f.T[k] = tb + poly[k * stride + tOff];
                    }
                    if (f.N != null && f.N.Any(x => x < 0 || x >= mesh.Normals.Count)) f.N = null;
                    if (f.T != null && f.T.Any(x => x < 0 || x >= mesh.UVs.Count)) f.T = null;
                    if (ok) mesh.Faces.Add(f);
                }
            }
            return mesh;
        }

        public string Export(Scene scene, string baseName, FileCreator createFile)
        {
            XNamespace ns = "http://www.collada.org/2005/11/COLLADASchema";
            var effects = new XElement(ns + "library_effects");
            var materials = new XElement(ns + "library_materials");
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var m = scene.Materials[i];
                string id = "mat" + i;
                effects.Add(new XElement(ns + "effect", new XAttribute("id", id + "-fx"),
                    new XElement(ns + "profile_COMMON",
                        new XElement(ns + "technique", new XAttribute("sid", "common"),
                            new XElement(ns + "lambert",
                                new XElement(ns + "diffuse", new XElement(ns + "color", new XAttribute("sid", "diffuse"),
                                    string.Join(" ", FmtUtil.S(m.Color.X), FmtUtil.S(m.Color.Y), FmtUtil.S(m.Color.Z), FmtUtil.S(m.Color.W)))),
                                new XElement(ns + "transparency", new XElement(ns + "float", FmtUtil.S(m.Color.W))))))));
                materials.Add(new XElement(ns + "material", new XAttribute("id", id), new XAttribute("name", m.Name ?? id),
                    new XElement(ns + "instance_effect", new XAttribute("url", "#" + id + "-fx"))));
            }

            var geometries = new XElement(ns + "library_geometries");
            var vscene = new XElement(ns + "visual_scene", new XAttribute("id", "Scene"), new XAttribute("name", "Scene"));
            for (int oi = 0; oi < scene.Objects.Count; oi++)
            {
                var o = scene.Objects[oi];
                var mesh = o.Mesh;
                string gid = "geom" + oi;
                var pos = new StringBuilder();
                foreach (var p in mesh.Positions) pos.Append(FmtUtil.S(p.X)).Append(' ').Append(FmtUtil.S(p.Y)).Append(' ').Append(FmtUtil.S(p.Z)).Append(' ');
                bool hasUV = mesh.UVs.Count > 0 && mesh.Faces.All(f => f.T != null && f.T.Length == f.V.Length);
                var meshEl = new XElement(ns + "mesh",
                    new XElement(ns + "source", new XAttribute("id", gid + "-pos"),
                        new XElement(ns + "float_array", new XAttribute("id", gid + "-pos-array"), new XAttribute("count", mesh.Positions.Count * 3), pos.ToString().Trim()),
                        new XElement(ns + "technique_common",
                            new XElement(ns + "accessor", new XAttribute("source", "#" + gid + "-pos-array"), new XAttribute("count", mesh.Positions.Count), new XAttribute("stride", 3),
                                new XElement(ns + "param", new XAttribute("name", "X"), new XAttribute("type", "float")),
                                new XElement(ns + "param", new XAttribute("name", "Y"), new XAttribute("type", "float")),
                                new XElement(ns + "param", new XAttribute("name", "Z"), new XAttribute("type", "float"))))));
                if (hasUV)
                {
                    var uv = new StringBuilder();
                    foreach (var t in mesh.UVs) uv.Append(FmtUtil.S(t.X)).Append(' ').Append(FmtUtil.S(t.Y)).Append(' ');
                    meshEl.Add(new XElement(ns + "source", new XAttribute("id", gid + "-uv"),
                        new XElement(ns + "float_array", new XAttribute("id", gid + "-uv-array"), new XAttribute("count", mesh.UVs.Count * 2), uv.ToString().Trim()),
                        new XElement(ns + "technique_common",
                            new XElement(ns + "accessor", new XAttribute("source", "#" + gid + "-uv-array"), new XAttribute("count", mesh.UVs.Count), new XAttribute("stride", 2),
                                new XElement(ns + "param", new XAttribute("name", "S"), new XAttribute("type", "float")),
                                new XElement(ns + "param", new XAttribute("name", "T"), new XAttribute("type", "float"))))));
                }
                meshEl.Add(new XElement(ns + "vertices", new XAttribute("id", gid + "-vtx"),
                    new XElement(ns + "input", new XAttribute("semantic", "POSITION"), new XAttribute("source", "#" + gid + "-pos"))));

                var bindings = new XElement(ns + "technique_common");
                foreach (var grp in mesh.Faces.Where(f => mesh.IsValidFace(f)).GroupBy(f => Math.Max(0, Math.Min(scene.Materials.Count - 1, f.Material))))
                {
                    var faces = grp.ToList();
                    var vcount = new StringBuilder(); var p = new StringBuilder();
                    foreach (var f in faces)
                    {
                        vcount.Append(f.V.Length).Append(' ');
                        for (int k = 0; k < f.V.Length; k++)
                        {
                            p.Append(f.V[k]).Append(' ');
                            if (hasUV) p.Append(f.T[k]).Append(' ');
                        }
                    }
                    string sym = "mat" + grp.Key;
                    var pl = new XElement(ns + "polylist", new XAttribute("material", sym), new XAttribute("count", faces.Count),
                        new XElement(ns + "input", new XAttribute("semantic", "VERTEX"), new XAttribute("source", "#" + gid + "-vtx"), new XAttribute("offset", 0)));
                    if (hasUV) pl.Add(new XElement(ns + "input", new XAttribute("semantic", "TEXCOORD"), new XAttribute("source", "#" + gid + "-uv"), new XAttribute("offset", 1), new XAttribute("set", 0)));
                    pl.Add(new XElement(ns + "vcount", vcount.ToString().Trim()));
                    pl.Add(new XElement(ns + "p", p.ToString().Trim()));
                    meshEl.Add(pl);
                    bindings.Add(new XElement(ns + "instance_material", new XAttribute("symbol", sym), new XAttribute("target", "#" + sym)));
                }
                geometries.Add(new XElement(ns + "geometry", new XAttribute("id", gid), new XAttribute("name", o.Name), meshEl));

                var w = o.World;
                // Write the matrix row-major for column vectors (= transpose of our row-vector matrix).
                string mtx = string.Join(" ", new[]
                {
                    w.M11, w.M21, w.M31, w.M41, w.M12, w.M22, w.M32, w.M42,
                    w.M13, w.M23, w.M33, w.M43, w.M14, w.M24, w.M34, w.M44
                }.Select(FmtUtil.S));
                vscene.Add(new XElement(ns + "node", new XAttribute("id", "node" + oi), new XAttribute("name", o.Name), new XAttribute("type", "NODE"),
                    new XElement(ns + "matrix", new XAttribute("sid", "transform"), mtx),
                    new XElement(ns + "instance_geometry", new XAttribute("url", "#" + gid), new XAttribute("name", o.Name),
                        new XElement(ns + "bind_material", bindings))));
            }

            var docRoot = new XElement(ns + "COLLADA", new XAttribute("version", "1.4.1"),
                new XElement(ns + "asset",
                    new XElement(ns + "contributor", new XElement(ns + "authoring_tool", "Facet3D for Xbox")),
                    new XElement(ns + "created", DateTime.UtcNow.ToString("s") + "Z"),
                    new XElement(ns + "modified", DateTime.UtcNow.ToString("s") + "Z"),
                    new XElement(ns + "unit", new XAttribute("name", "meter"), new XAttribute("meter", "1")),
                    new XElement(ns + "up_axis", "Y_UP")),
                effects, materials, geometries,
                new XElement(ns + "library_visual_scenes", vscene),
                new XElement(ns + "scene", new XElement(ns + "instance_visual_scene", new XAttribute("url", "#Scene"))));
            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), docRoot);
            string name = baseName + ".dae";
            using (var s = createFile(name))
            {
                var settings = new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
                using (var xw = System.Xml.XmlWriter.Create(s, settings)) doc.Save(xw);
            }
            return name;
        }
    }
}
