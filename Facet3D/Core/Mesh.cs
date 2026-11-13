using System;
using System.Collections.Generic;
using System.Numerics;

namespace Facet3D
{
    /// <summary>A polygon. V = position indices; T/N = optional per-corner UV / normal indices.</summary>
    public sealed class Face
    {
        public int[] V;
        public int[] T;
        public int[] N;
        public int Material;

        public Face(int[] v) { V = v; }

        public Face(int[] v, int material) { V = v; Material = material; }

        public Face Clone()
        {
            var f = new Face((int[])V.Clone());
            f.T = T == null ? null : (int[])T.Clone();
            f.N = N == null ? null : (int[])N.Clone();
            f.Material = Material;
            return f;
        }

        public void Reverse()
        {
            Array.Reverse(V);
            if (T != null) Array.Reverse(T);
            if (N != null) Array.Reverse(N);
        }
    }

    public sealed class Mesh
    {
        static int s_version;

        public List<Vector3> Positions = new List<Vector3>();
        public List<Vector2> UVs = new List<Vector2>();
        public List<Vector3> Normals = new List<Vector3>();
        /// <summary>Optional per-position RGBA colour (null when the mesh has no vertex colours).</summary>
        public List<Vector4> Colors;
        public List<Face> Faces = new List<Face>();

        public int Version { get; private set; }

        public Mesh() { Changed(); }

        public void Changed() { Version = System.Threading.Interlocked.Increment(ref s_version); }

        public Mesh Clone()
        {
            var m = new Mesh();
            m.Positions = new List<Vector3>(Positions);
            m.UVs = new List<Vector2>(UVs);
            m.Normals = new List<Vector3>(Normals);
            m.Colors = Colors == null ? null : new List<Vector4>(Colors);
            m.Faces = new List<Face>(Faces.Count);
            foreach (var f in Faces) m.Faces.Add(f.Clone());
            return m;
        }

        public int TriangleCount
        {
            get
            {
                int n = 0;
                foreach (var f in Faces) if (f.V.Length >= 3) n += f.V.Length - 2;
                return n;
            }
        }

        public bool IsValidFace(Face f)
        {
            if (f.V == null || f.V.Length < 3) return false;
            int n = Positions.Count;
            for (int i = 0; i < f.V.Length; i++) if (f.V[i] < 0 || f.V[i] >= n) return false;
            return true;
        }

        public void GetBounds(out Vector3 min, out Vector3 max)
        {
            if (Positions.Count == 0) { min = max = Vector3.Zero; return; }
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var p in Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }

        /// <summary>Newell's method normal (works for non-planar polygons), normalised.</summary>
        public Vector3 FaceNormal(Face f)
        {
            Vector3 n = Vector3.Zero;
            int c = f.V.Length;
            for (int i = 0; i < c; i++)
            {
                Vector3 a = Positions[f.V[i]];
                Vector3 b = Positions[f.V[(i + 1) % c]];
                n.X += (a.Y - b.Y) * (a.Z + b.Z);
                n.Y += (a.Z - b.Z) * (a.X + b.X);
                n.Z += (a.X - b.X) * (a.Y + b.Y);
            }
            float len = n.Length();
            return len > 1e-12f ? n / len : Vector3.UnitY;
        }

        public Vector3 FaceCenter(Face f)
        {
            Vector3 c = Vector3.Zero;
            foreach (int i in f.V) c += Positions[i];
            return c / f.V.Length;
        }

        /// <summary>Area-weighted smooth normals per position.</summary>
        public Vector3[] ComputeVertexNormals()
        {
            var acc = new Vector3[Positions.Count];
            foreach (var f in Faces)
            {
                if (!IsValidFace(f)) continue;
                Vector3 n = Vector3.Zero;
                int c = f.V.Length;
                for (int i = 0; i < c; i++)
                {
                    Vector3 a = Positions[f.V[i]];
                    Vector3 b = Positions[f.V[(i + 1) % c]];
                    n.X += (a.Y - b.Y) * (a.Z + b.Z);
                    n.Y += (a.Z - b.Z) * (a.X + b.X);
                    n.Z += (a.X - b.X) * (a.Y + b.Y);
                }
                foreach (int i in f.V) acc[i] += n;
            }
            for (int i = 0; i < acc.Length; i++)
            {
                float l = acc[i].Length();
                acc[i] = l > 1e-12f ? acc[i] / l : Vector3.UnitY;
            }
            return acc;
        }

        /// <summary>Removes positions no face uses. Returns old->new index map (-1 = removed).</summary>
        public int[] RemoveUnusedVertices()
        {
            var used = new bool[Positions.Count];
            foreach (var f in Faces) foreach (int i in f.V) if (i >= 0 && i < used.Length) used[i] = true;
            var map = new int[Positions.Count];
            var np = new List<Vector3>();
            var nc = Colors == null ? null : new List<Vector4>();
            for (int i = 0; i < used.Length; i++)
            {
                if (used[i])
                {
                    map[i] = np.Count;
                    np.Add(Positions[i]);
                    if (nc != null) nc.Add(i < Colors.Count ? Colors[i] : Vector4.One);
                }
                else map[i] = -1;
            }
            Positions = np;
            Colors = nc;
            foreach (var f in Faces) for (int k = 0; k < f.V.Length; k++) f.V[k] = map[f.V[k]];
            Changed();
            return map;
        }

        /// <summary>Merges positions that are exactly equal. Per-corner UV/normal indices are kept, so seams survive.</summary>
        public void WeldExact()
        {
            if (Positions.Count == 0) return;
            var lookup = new Dictionary<Vector3, int>();
            var map = new int[Positions.Count];
            var np = new List<Vector3>();
            var nc = Colors == null ? null : new List<Vector4>();
            for (int i = 0; i < Positions.Count; i++)
            {
                int idx;
                if (!lookup.TryGetValue(Positions[i], out idx))
                {
                    idx = np.Count;
                    lookup[Positions[i]] = idx;
                    np.Add(Positions[i]);
                    if (nc != null) nc.Add(i < Colors.Count ? Colors[i] : Vector4.One);
                }
                map[i] = idx;
            }
            if (np.Count == Positions.Count) return;
            Positions = np;
            Colors = nc;
            foreach (var f in Faces) for (int k = 0; k < f.V.Length; k++) if (f.V[k] >= 0 && f.V[k] < map.Length) f.V[k] = map[f.V[k]];
            RemoveDegenerateFaces();
            Changed();
        }

        /// <summary>Removes repeated consecutive corners and faces left with fewer than 3 corners.</summary>
        public void RemoveDegenerateFaces()
        {
            var keep = new List<Face>(Faces.Count);
            foreach (var f in Faces)
            {
                var v = new List<int>(); var t = f.T != null ? new List<int>() : null; var n = f.N != null ? new List<int>() : null;
                for (int k = 0; k < f.V.Length; k++)
                {
                    int prev = v.Count > 0 ? v[v.Count - 1] : int.MinValue;
                    if (f.V[k] == prev) continue;
                    v.Add(f.V[k]);
                    if (t != null) t.Add(f.T[k]);
                    if (n != null) n.Add(f.N[k]);
                }
                while (v.Count > 1 && v[0] == v[v.Count - 1])
                {
                    v.RemoveAt(v.Count - 1);
                    if (t != null) t.RemoveAt(t.Count - 1);
                    if (n != null) n.RemoveAt(n.Count - 1);
                }
                if (v.Count < 3) continue;
                f.V = v.ToArray();
                f.T = t == null ? null : t.ToArray();
                f.N = n == null ? null : n.ToArray();
                keep.Add(f);
            }
            Faces = keep;
        }

        /// <summary>Unique undirected edges as (a,b) pairs with a&lt;b.</summary>
        public List<long> UniqueEdges()
        {
            var set = new HashSet<long>();
            var list = new List<long>();
            foreach (var f in Faces)
            {
                int c = f.V.Length;
                for (int i = 0; i < c; i++)
                {
                    long key = EdgeKey(f.V[i], f.V[(i + 1) % c]);
                    if (set.Add(key)) list.Add(key);
                }
            }
            return list;
        }

        public static long EdgeKey(int a, int b)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        public static void EdgeFromKey(long key, out int a, out int b)
        {
            a = (int)(key >> 32);
            b = (int)(key & 0xffffffffL);
        }
    }
}
