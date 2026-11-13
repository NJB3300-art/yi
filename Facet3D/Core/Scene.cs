using System;
using System.Collections.Generic;
using System.Numerics;

namespace Facet3D
{
    public sealed class Material
    {
        public string Name = "Material";
        public Vector4 Color = new Vector4(0.8f, 0.8f, 0.8f, 1f);
        public float Metallic = 0f;
        public float Roughness = 0.6f;
        /// <summary>Texture file name kept for round-tripping (not rendered in the viewport).</summary>
        public string Texture;

        public Material Clone()
        {
            return new Material { Name = Name, Color = Color, Metallic = Metallic, Roughness = Roughness, Texture = Texture };
        }
    }

    public enum Shading { Auto, Flat, Smooth }

    public sealed class Keyframe
    {
        public int Frame;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public Keyframe Clone() { return new Keyframe { Frame = Frame, Position = Position, Rotation = Rotation, Scale = Scale }; }
    }

    public sealed class SceneObject
    {
        public string Name = "Object";
        public Mesh Mesh = new Mesh();
        public Vector3 Position = Vector3.Zero;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
        public Shading Shading = Shading.Auto;
        public List<Keyframe> Keys = new List<Keyframe>();

        public Matrix4x4 World
        {
            get { return Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position); }
        }

        public void SetWorld(Matrix4x4 m)
        {
            Vector3 s, t; Quaternion r;
            if (Matrix4x4.Decompose(m, out s, out r, out t))
            {
                Scale = s; Rotation = Quaternion.Normalize(r); Position = t;
            }
            else
            {
                Position = m.Translation; Rotation = Quaternion.Identity; Scale = Vector3.One;
            }
        }

        public SceneObject Clone()
        {
            var o = new SceneObject
            {
                Name = Name, Mesh = Mesh.Clone(), Position = Position, Rotation = Rotation, Scale = Scale,
                Visible = Visible, Shading = Shading
            };
            foreach (var k in Keys) o.Keys.Add(k.Clone());
            return o;
        }

        public bool HasKey(int frame)
        {
            foreach (var k in Keys) if (k.Frame == frame) return true;
            return false;
        }

        public void SetKey(int frame)
        {
            Keys.RemoveAll(k => k.Frame == frame);
            Keys.Add(new Keyframe { Frame = frame, Position = Position, Rotation = Rotation, Scale = Scale });
            Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        }

        public bool RemoveKey(int frame) { return Keys.RemoveAll(k => k.Frame == frame) > 0; }

        public void Evaluate(float frame)
        {
            if (Keys.Count == 0) return;
            if (frame <= Keys[0].Frame) { Apply(Keys[0]); return; }
            var last = Keys[Keys.Count - 1];
            if (frame >= last.Frame) { Apply(last); return; }
            for (int i = 0; i < Keys.Count - 1; i++)
            {
                var a = Keys[i]; var b = Keys[i + 1];
                if (frame >= a.Frame && frame <= b.Frame)
                {
                    float t = b.Frame == a.Frame ? 0f : (frame - a.Frame) / (b.Frame - a.Frame);
                    // Smoothstep easing gives a softer, Prisma-like default motion.
                    t = t * t * (3f - 2f * t);
                    Position = Vector3.Lerp(a.Position, b.Position, t);
                    Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                    Scale = Vector3.Lerp(a.Scale, b.Scale, t);
                    return;
                }
            }
        }

        void Apply(Keyframe k) { Position = k.Position; Rotation = k.Rotation; Scale = k.Scale; }
    }

    public sealed class Scene
    {
        static int s_matVersion;

        public List<SceneObject> Objects = new List<SceneObject>();
        public List<Material> Materials = new List<Material>();
        public int FrameEnd = 120;
        public float Fps = 24f;
        public int MaterialsVersion { get; private set; }

        public Scene() { EnsureDefaultMaterial(); MaterialsChanged(); }

        public void MaterialsChanged() { MaterialsVersion = System.Threading.Interlocked.Increment(ref s_matVersion); }

        public void EnsureDefaultMaterial()
        {
            if (Materials.Count == 0) Materials.Add(new Material { Name = "Default" });
        }

        public Material MaterialAt(int index)
        {
            if (index >= 0 && index < Materials.Count) return Materials[index];
            return Materials.Count > 0 ? Materials[0] : new Material();
        }

        public Scene Clone()
        {
            var s = new Scene();
            s.Materials.Clear();
            foreach (var m in Materials) s.Materials.Add(m.Clone());
            foreach (var o in Objects) s.Objects.Add(o.Clone());
            s.FrameEnd = FrameEnd; s.Fps = Fps;
            s.MaterialsChanged();
            return s;
        }

        /// <summary>Appends another scene's objects and materials (material indices are remapped). Returns the added objects.</summary>
        public List<SceneObject> Merge(Scene other)
        {
            other.EnsureDefaultMaterial();
            int offset = Materials.Count;
            foreach (var m in other.Materials) Materials.Add(m);
            var added = new List<SceneObject>();
            foreach (var o in other.Objects)
            {
                foreach (var f in o.Mesh.Faces)
                {
                    int mi = f.Material;
                    if (mi < 0 || mi >= other.Materials.Count) mi = 0;
                    f.Material = mi + offset;
                }
                o.Mesh.Changed();
                o.Name = UniqueName(o.Name);
                Objects.Add(o);
                added.Add(o);
            }
            int maxKey = 0;
            foreach (var o in other.Objects) foreach (var k in o.Keys) maxKey = Math.Max(maxKey, k.Frame);
            if (maxKey > FrameEnd) FrameEnd = maxKey;
            MaterialsChanged();
            return added;
        }

        public string UniqueName(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Object";
            bool taken = false;
            foreach (var o in Objects) if (o.Name == baseName) { taken = true; break; }
            if (!taken) return baseName;
            for (int i = 1; ; i++)
            {
                string n = baseName + "." + i.ToString("000");
                bool t = false;
                foreach (var o in Objects) if (o.Name == n) { t = true; break; }
                if (!t) return n;
            }
        }

        public void GetBounds(IEnumerable<SceneObject> objs, out Vector3 min, out Vector3 max, out bool any)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue); any = false;
            foreach (var o in objs)
            {
                if (!o.Visible || o.Mesh.Positions.Count == 0) continue;
                var w = o.World;
                foreach (var p in o.Mesh.Positions)
                {
                    var q = Vector3.Transform(p, w);
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                    any = true;
                }
            }
        }

        /// <summary>World-space copy of an object's mesh (positions and normals baked).</summary>
        public static Mesh BakeWorld(SceneObject o)
        {
            var m = o.Mesh.Clone();
            var w = o.World;
            for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] = Vector3.Transform(m.Positions[i], w);
            Matrix4x4 inv;
            Matrix4x4 nm = Matrix4x4.Invert(w, out inv) ? Matrix4x4.Transpose(inv) : w;
            for (int i = 0; i < m.Normals.Count; i++)
            {
                var n = Vector3.TransformNormal(m.Normals[i], nm);
                float l = n.Length();
                m.Normals[i] = l > 1e-12f ? n / l : Vector3.UnitY;
            }
            if (w.GetDeterminant() < 0) foreach (var f in m.Faces) f.Reverse();
            return m;
        }
    }
}
