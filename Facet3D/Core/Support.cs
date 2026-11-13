using System;
using System.Collections.Generic;
using System.Numerics;

namespace Facet3D
{
    public sealed class Selection
    {
        public readonly List<SceneObject> Objects = new List<SceneObject>();
        public SceneObject Active;
        public readonly HashSet<int> Verts = new HashSet<int>();
        public readonly List<int> VertOrder = new List<int>();
        public readonly HashSet<int> Faces = new HashSet<int>();

        public void ClearElements() { Verts.Clear(); VertOrder.Clear(); Faces.Clear(); }

        public void ClearAll() { Objects.Clear(); Active = null; ClearElements(); }

        public void SelectOnly(SceneObject o)
        {
            Objects.Clear(); ClearElements();
            if (o != null) Objects.Add(o);
            Active = o;
        }

        public void Toggle(SceneObject o)
        {
            if (Objects.Contains(o))
            {
                Objects.Remove(o);
                if (Active == o) Active = Objects.Count > 0 ? Objects[Objects.Count - 1] : null;
            }
            else { Objects.Add(o); Active = o; }
        }

        public void ToggleVert(int v, bool additive)
        {
            if (!additive) { Verts.Clear(); VertOrder.Clear(); }
            if (Verts.Contains(v)) { Verts.Remove(v); VertOrder.Remove(v); }
            else { Verts.Add(v); VertOrder.Add(v); }
        }

        public void ToggleFace(int f, bool additive)
        {
            if (!additive) Faces.Clear();
            if (!Faces.Remove(f)) Faces.Add(f);
        }

        public void Remap(Scene oldScene, Scene newScene)
        {
            var idx = new List<int>();
            foreach (var o in Objects) { int i = oldScene.Objects.IndexOf(o); if (i >= 0) idx.Add(i); }
            int active = Active != null ? oldScene.Objects.IndexOf(Active) : -1;
            Objects.Clear();
            foreach (int i in idx) if (i < newScene.Objects.Count) Objects.Add(newScene.Objects[i]);
            Active = active >= 0 && active < newScene.Objects.Count ? newScene.Objects[active] : (Objects.Count > 0 ? Objects[0] : null);
            ClearElements();
        }
    }

    public sealed class UndoStack
    {
        readonly List<Scene> _undo = new List<Scene>();
        readonly List<Scene> _redo = new List<Scene>();

        int Limit(Scene s)
        {
            long verts = 0;
            foreach (var o in s.Objects) verts += o.Mesh.Positions.Count;
            if (verts > 2000000) return 4;
            if (verts > 500000) return 10;
            return 30;
        }

        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }

        public void Push(Scene current)
        {
            _undo.Add(current.Clone());
            int lim = Limit(current);
            while (_undo.Count > lim) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public void DiscardLast() { if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1); }

        public Scene Undo(Scene current)
        {
            if (_undo.Count == 0) return null;
            _redo.Add(current.Clone());
            var s = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            return s;
        }

        public Scene Redo(Scene current)
        {
            if (_redo.Count == 0) return null;
            _undo.Add(current.Clone());
            var s = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            return s;
        }

        public void Clear() { _undo.Clear(); _redo.Clear(); }
    }

    public sealed class Camera
    {
        public Vector3 Target = Vector3.Zero;
        public float Yaw = 0.75f;
        public float Pitch = 0.5f;
        public float Distance = 9f;
        public float Fov = 0.9f;

        public Vector3 Eye
        {
            get
            {
                float cp = (float)Math.Cos(Pitch);
                return Target + new Vector3(cp * (float)Math.Sin(Yaw), (float)Math.Sin(Pitch), cp * (float)Math.Cos(Yaw)) * Distance;
            }
        }

        public Vector3 Forward { get { return Vector3.Normalize(Target - Eye); } }
        public Vector3 Right { get { return Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY)); } }
        public Vector3 Up { get { return Vector3.Cross(Right, Forward); } }

        public Matrix4x4 View { get { return Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY); } }

        public Matrix4x4 Projection(float aspect)
        {
            float near = Math.Max(0.001f, Distance * 0.005f);
            float far = Math.Max(1000f, Distance * 200f);
            return Matrix4x4.CreatePerspectiveFieldOfView(Fov, Math.Max(0.1f, aspect), near, far);
        }

        public void Orbit(float dYaw, float dPitch)
        {
            Yaw += dYaw;
            Pitch = Math.Max(-1.55f, Math.Min(1.55f, Pitch + dPitch));
        }

        public void Pan(float dx, float dy)
        {
            float s = Distance * 0.9f;
            Target += Right * dx * s + Up * dy * s;
        }

        public void Zoom(float amount)
        {
            Distance = Math.Max(0.05f, Math.Min(5000f, Distance * (float)Math.Exp(-amount)));
        }

        public void Frame(Vector3 min, Vector3 max)
        {
            Target = (min + max) * 0.5f;
            float r = Math.Max(0.1f, (max - min).Length() * 0.5f);
            Distance = r / (float)Math.Sin(Fov * 0.5f) * 1.1f;
        }
    }

    public struct PickRay
    {
        public Vector3 Origin;
        public Vector3 Direction;
    }

    public static class Picking
    {
        public static PickRay ScreenRay(Vector2 screen, Vector2 size, Matrix4x4 viewProj)
        {
            float nx = screen.X / size.X * 2f - 1f;
            float ny = 1f - screen.Y / size.Y * 2f;
            Matrix4x4 inv;
            Matrix4x4.Invert(viewProj, out inv);
            var a = Vector4.Transform(new Vector4(nx, ny, 0f, 1f), inv);
            var b = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inv);
            var p0 = new Vector3(a.X, a.Y, a.Z) / a.W;
            var p1 = new Vector3(b.X, b.Y, b.Z) / b.W;
            return new PickRay { Origin = p0, Direction = Vector3.Normalize(p1 - p0) };
        }

        /// <summary>Projects to screen. Returns false if behind the camera. depth = clip w.</summary>
        public static bool Project(Vector3 world, Matrix4x4 viewProj, Vector2 size, out Vector2 screen, out float depth)
        {
            var c = Vector4.Transform(new Vector4(world, 1f), viewProj);
            depth = c.W;
            if (c.W <= 1e-6f) { screen = Vector2.Zero; return false; }
            screen = new Vector2((c.X / c.W * 0.5f + 0.5f) * size.X, (1f - (c.Y / c.W * 0.5f + 0.5f)) * size.Y);
            return true;
        }

        public static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0;
            var e1 = b - a; var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Math.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 0;
        }

        static bool RayBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
        {
            float tmin = float.MinValue, tmax = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                float oo = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
                float dd = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
                float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
                float hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
                if (Math.Abs(dd) < 1e-12f) { if (oo < lo || oo > hi) return false; continue; }
                float t1 = (lo - oo) / dd, t2 = (hi - oo) / dd;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            return tmax >= 0;
        }

        /// <summary>Closest hit face of an object; t is in world ray units.</summary>
        public static bool PickFace(SceneObject o, PickRay ray, out int face, out float t)
        {
            face = -1; t = float.MaxValue;
            Matrix4x4 inv;
            if (!Matrix4x4.Invert(o.World, out inv)) return false;
            var lo = Vector3.Transform(ray.Origin, inv);
            var ld = Vector3.TransformNormal(ray.Direction, inv);
            var m = o.Mesh;
            Vector3 bmin, bmax; m.GetBounds(out bmin, out bmax);
            var pad = (bmax - bmin) * 0.01f + new Vector3(1e-4f);
            if (!RayBox(lo, ld, bmin - pad, bmax + pad)) return false;
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                var f = m.Faces[fi];
                if (!m.IsValidFace(f)) continue;
                for (int k = 1; k < f.V.Length - 1; k++)
                {
                    float tt;
                    if (RayTriangle(lo, ld, m.Positions[f.V[0]], m.Positions[f.V[k]], m.Positions[f.V[k + 1]], out tt) && tt < t)
                    {
                        t = tt; face = fi;
                    }
                }
            }
            return face >= 0;
        }

        public static SceneObject PickObject(Scene scene, PickRay ray)
        {
            SceneObject best = null; float bestT = float.MaxValue;
            foreach (var o in scene.Objects)
            {
                if (!o.Visible) continue;
                int f; float t;
                if (PickFace(o, ray, out f, out t) && t < bestT) { bestT = t; best = o; }
            }
            return best;
        }

        public static int PickVertex(SceneObject o, Vector2 cursor, Vector2 size, Matrix4x4 viewProj, float radius)
        {
            var w = o.World * viewProj;
            int best = -1; float bestD = radius * radius; float bestDepth = float.MaxValue;
            var pos = o.Mesh.Positions;
            for (int i = 0; i < pos.Count; i++)
            {
                Vector2 s; float depth;
                if (!Project(pos[i], w, size, out s, out depth)) continue;
                float d = Vector2.DistanceSquared(s, cursor);
                if (d < bestD - 1f || (d <= bestD && depth < bestDepth))
                {
                    bestD = d; best = i; bestDepth = depth;
                }
            }
            return best;
        }
    }
}
