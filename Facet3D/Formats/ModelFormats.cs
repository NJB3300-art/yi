using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Facet3D
{
    /// <summary>Opens a file next to the main file (MTL, .bin, textures). Returns null if missing.</summary>
    public delegate Stream SiblingOpener(string relativeName);

    /// <summary>Creates (overwrites) a file in the export folder.</summary>
    public delegate Stream FileCreator(string fileName);

    public interface IModelFormat
    {
        string Name { get; }
        string[] Extensions { get; }
        bool CanImport { get; }
        bool CanExport { get; }
        Scene Import(byte[] data, string fileName, SiblingOpener openSibling);
        /// <summary>Writes the scene. baseName has no extension. Returns the main file name written.</summary>
        string Export(Scene scene, string baseName, FileCreator createFile);
    }

    public static class ModelFormats
    {
        public static readonly IModelFormat[] All =
        {
            new GltfFormat(true),
            new GltfFormat(false),
            new ObjFormat(),
            new FbxFormat(),
            new StlFormat(),
            new PlyFormat(),
            new ColladaFormat(),
            new ThreeDsFormat(),
            new OffFormat(),
        };

        public static IModelFormat ForImport(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            foreach (var f in All) if (f.CanImport && f.Extensions.Contains(ext)) return f;
            return null;
        }

        public static IEnumerable<string> ImportExtensions
        {
            get { return All.Where(f => f.CanImport).SelectMany(f => f.Extensions).Distinct(); }
        }

        public static Scene Import(byte[] data, string fileName, SiblingOpener sibling)
        {
            var fmt = ForImport(fileName);
            if (fmt == null) throw new NotSupportedException("Unsupported file type: " + Path.GetExtension(fileName));
            var scene = fmt.Import(data, fileName, sibling);
            scene.EnsureDefaultMaterial();
            foreach (var o in scene.Objects)
            {
                o.Mesh.RemoveDegenerateFaces();
                o.Mesh.WeldExact();
                if (o.Mesh.Normals.Count == 0) o.Shading = Shading.Flat;
                o.Mesh.Changed();
            }
            scene.Objects.RemoveAll(o => o.Mesh.Positions.Count == 0);
            if (scene.Objects.Count == 0) throw new InvalidDataException("No mesh geometry found in " + fileName);
            return scene;
        }
    }

    /// <summary>Shared helpers for importers and exporters.</summary>
    static class FmtUtil
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static float F(string s)
        {
            float v;
            float.TryParse(s, NumberStyles.Float, Inv, out v);
            return v;
        }

        public static string S(float v) { return v.ToString("0.######", Inv); }

        public static byte[] ReadAll(Stream s)
        {
            if (s == null) return null;
            using (s)
            {
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static string Text(byte[] data)
        {
            int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(data, start, data.Length - start);
        }

        public static string BaseName(string fileName)
        {
            return Path.GetFileNameWithoutExtension(fileName ?? "Model");
        }

        /// <summary>Material colour at index, defaulting to grey.</summary>
        public static Vector4 Color(Scene s, int i) { return s.MaterialAt(i).Color; }

        public static string SafeName(string n, string fallback)
        {
            if (string.IsNullOrWhiteSpace(n)) return fallback;
            var sb = new StringBuilder();
            foreach (char c in n) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            return sb.ToString();
        }

        /// <summary>Corner normal for export, following the object's shading mode.</summary>
        public static Vector3 CornerNormal(Mesh m, Face f, int corner, Shading shading, Vector3[] smooth, Vector3 flat)
        {
            if (shading == Shading.Flat) return flat;
            if (shading == Shading.Smooth) return smooth[f.V[corner]];
            if (f.N != null && corner < f.N.Length && f.N[corner] >= 0 && f.N[corner] < m.Normals.Count) return m.Normals[f.N[corner]];
            return flat;
        }

        public static Matrix4x4 ZUpToYUp { get { return Matrix4x4.CreateRotationX((float)(-Math.PI / 2)); } }
        public static Matrix4x4 YUpToZUp { get { return Matrix4x4.CreateRotationX((float)(Math.PI / 2)); } }

        /// <summary>Applies a matrix to every position/normal of every object (used for axis conversion and unit scale).</summary>
        public static void TransformScene(Scene s, Matrix4x4 m)
        {
            foreach (var o in s.Objects)
            {
                var w = o.World * m;
                o.SetWorld(w);
                foreach (var k in o.Keys)
                {
                    var kw = Matrix4x4.CreateScale(k.Scale) * Matrix4x4.CreateFromQuaternion(k.Rotation) * Matrix4x4.CreateTranslation(k.Position) * m;
                    Vector3 sc, tr; Quaternion r;
                    if (Matrix4x4.Decompose(kw, out sc, out r, out tr)) { k.Position = tr; k.Rotation = r; k.Scale = sc; }
                }
            }
        }
    }
}
