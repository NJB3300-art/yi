using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Facet3D
{
    public static class MeshOps
    {
        /// <summary>Region extrude. Faces in 'faces' are moved onto new vertices; side walls are created on the region boundary.
        /// Returns the new vertex indices.</summary>
        public static HashSet<int> Extrude(Mesh m, HashSet<int> faces)
        {
            var edgeCount = new Dictionary<long, int>();
            foreach (int fi in faces)
            {
                var f = m.Faces[fi];
                for (int k = 0; k < f.V.Length; k++)
                {
                    long key = Mesh.EdgeKey(f.V[k], f.V[(k + 1) % f.V.Length]);
                    int c; edgeCount.TryGetValue(key, out c); edgeCount[key] = c + 1;
                }
            }
            var map = new Dictionary<int, int>();
            foreach (int fi in faces)
                foreach (int v in m.Faces[fi].V)
                    if (!map.ContainsKey(v))
                    {
                        map[v] = m.Positions.Count;
                        m.Positions.Add(m.Positions[v]);
                        if (m.Colors != null) m.Colors.Add(v < m.Colors.Count ? m.Colors[v] : Vector4.One);
                    }
            var sides = new List<Face>();
            foreach (int fi in faces)
            {
                var f = m.Faces[fi];
                for (int k = 0; k < f.V.Length; k++)
                {
                    int a = f.V[k], b = f.V[(k + 1) % f.V.Length];
                    if (edgeCount[Mesh.EdgeKey(a, b)] == 1)
                        sides.Add(new Face(new[] { a, b, map[b], map[a] }, f.Material));
                }
            }
            foreach (int fi in faces)
            {
                var f = m.Faces[fi];
                for (int k = 0; k < f.V.Length; k++) f.V[k] = map[f.V[k]];
                f.N = null;
            }
            m.Faces.AddRange(sides);
            m.Changed();
            return new HashSet<int>(map.Values);
        }

        public static Vector3 AverageNormal(Mesh m, IEnumerable<int> faces)
        {
            Vector3 n = Vector3.Zero;
            foreach (int fi in faces) n += m.FaceNormal(m.Faces[fi]);
            float l = n.Length();
            return l > 1e-6f ? n / l : Vector3.UnitY;
        }

        public static void DeleteFaces(Mesh m, HashSet<int> faces)
        {
            var keep = new List<Face>();
            for (int i = 0; i < m.Faces.Count; i++) if (!faces.Contains(i)) keep.Add(m.Faces[i]);
            m.Faces = keep;
            m.RemoveUnusedVertices();
        }

        public static void DeleteVertices(Mesh m, HashSet<int> verts)
        {
            m.Faces = m.Faces.Where(f => !f.V.Any(v => verts.Contains(v))).ToList();
            // Remove the vertices themselves even if they were loose.
            var map = new int[m.Positions.Count];
            var np = new List<Vector3>();
            var nc = m.Colors == null ? null : new List<Vector4>();
            for (int i = 0; i < m.Positions.Count; i++)
            {
                if (verts.Contains(i)) { map[i] = -1; continue; }
                map[i] = np.Count; np.Add(m.Positions[i]);
                if (nc != null) nc.Add(i < m.Colors.Count ? m.Colors[i] : Vector4.One);
            }
            m.Positions = np; m.Colors = nc;
            foreach (var f in m.Faces) for (int k = 0; k < f.V.Length; k++) f.V[k] = map[f.V[k]];
            m.Changed();
        }

        public static void FlipNormals(Mesh m, HashSet<int> faces)
        {
            for (int i = 0; i < m.Faces.Count; i++)
                if (faces == null || faces.Contains(i)) m.Faces[i].Reverse();
            m.Changed();
        }

        public static void Triangulate(Mesh m)
        {
            var nf = new List<Face>();
            foreach (var f in m.Faces)
            {
                if (f.V.Length <= 3) { nf.Add(f); continue; }
                for (int k = 1; k < f.V.Length - 1; k++)
                {
                    var t = new Face(new[] { f.V[0], f.V[k], f.V[k + 1] }, f.Material);
                    if (f.T != null) t.T = new[] { f.T[0], f.T[k], f.T[k + 1] };
                    if (f.N != null) t.N = new[] { f.N[0], f.N[k], f.N[k + 1] };
                    nf.Add(t);
                }
            }
            m.Faces = nf;
            m.Changed();
        }

        /// <summary>Welds vertices closer than 'distance'. Returns how many vertices were removed.</summary>
        public static int MergeByDistance(Mesh m, float distance)
        {
            if (m.Positions.Count == 0) return 0;
            float cell = Math.Max(distance, 1e-6f);
            var grid = new Dictionary<long, List<int>>();
            var map = new int[m.Positions.Count];
            var np = new List<Vector3>();
            var nc = m.Colors == null ? null : new List<Vector4>();
            float d2 = distance * distance;
            for (int i = 0; i < m.Positions.Count; i++)
            {
                var p = m.Positions[i];
                int cx = (int)Math.Floor(p.X / cell), cy = (int)Math.Floor(p.Y / cell), cz = (int)Math.Floor(p.Z / cell);
                int found = -1;
                for (int dx = -1; dx <= 1 && found < 0; dx++)
                    for (int dy = -1; dy <= 1 && found < 0; dy++)
                        for (int dz = -1; dz <= 1 && found < 0; dz++)
                        {
                            List<int> list;
                            if (!grid.TryGetValue(CellKey(cx + dx, cy + dy, cz + dz), out list)) continue;
                            foreach (int j in list)
                                if (Vector3.DistanceSquared(np[j], p) <= d2) { found = j; break; }
                        }
                if (found < 0)
                {
                    found = np.Count;
                    np.Add(p);
                    if (nc != null) nc.Add(i < m.Colors.Count ? m.Colors[i] : Vector4.One);
                    long key = CellKey(cx, cy, cz);
                    List<int> l2;
                    if (!grid.TryGetValue(key, out l2)) { l2 = new List<int>(); grid[key] = l2; }
                    l2.Add(found);
                }
                map[i] = found;
            }
            int removed = m.Positions.Count - np.Count;
            m.Positions = np; m.Colors = nc;
            foreach (var f in m.Faces) for (int k = 0; k < f.V.Length; k++) f.V[k] = map[f.V[k]];
            m.RemoveDegenerateFaces();
            m.Changed();
            return removed;
        }

        static long CellKey(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        /// <summary>Catmull-Clark (smooth=true) or simple midpoint split (smooth=false). Every n-gon becomes n quads.</summary>
        public static void Subdivide(Mesh m, bool smooth)
        {
            int nv = m.Positions.Count;
            var facePts = new Vector3[m.Faces.Count];
            for (int i = 0; i < m.Faces.Count; i++) facePts[i] = m.FaceCenter(m.Faces[i]);

            var edgeIndex = new Dictionary<long, int>();
            var edgeFaces = new List<List<int>>();
            var edgeVerts = new List<long>();
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                var f = m.Faces[fi];
                for (int k = 0; k < f.V.Length; k++)
                {
                    long key = Mesh.EdgeKey(f.V[k], f.V[(k + 1) % f.V.Length]);
                    int ei;
                    if (!edgeIndex.TryGetValue(key, out ei))
                    {
                        ei = edgeFaces.Count; edgeIndex[key] = ei;
                        edgeFaces.Add(new List<int>()); edgeVerts.Add(key);
                    }
                    edgeFaces[ei].Add(fi);
                }
            }
            var edgePts = new Vector3[edgeFaces.Count];
            var edgeMid = new Vector3[edgeFaces.Count];
            for (int e = 0; e < edgeFaces.Count; e++)
            {
                int a, b; Mesh.EdgeFromKey(edgeVerts[e], out a, out b);
                edgeMid[e] = (m.Positions[a] + m.Positions[b]) * 0.5f;
                if (smooth && edgeFaces[e].Count == 2)
                    edgePts[e] = (m.Positions[a] + m.Positions[b] + facePts[edgeFaces[e][0]] + facePts[edgeFaces[e][1]]) * 0.25f;
                else edgePts[e] = edgeMid[e];
            }

            var newVerts = new Vector3[nv];
            if (smooth)
            {
                var fSum = new Vector3[nv]; var fCnt = new int[nv];
                var rSum = new Vector3[nv]; var rCnt = new int[nv];
                var bSum = new Vector3[nv]; var bCnt = new int[nv];
                for (int fi = 0; fi < m.Faces.Count; fi++) foreach (int v in m.Faces[fi].V) { fSum[v] += facePts[fi]; fCnt[v]++; }
                for (int e = 0; e < edgeFaces.Count; e++)
                {
                    int a, b; Mesh.EdgeFromKey(edgeVerts[e], out a, out b);
                    rSum[a] += edgeMid[e]; rCnt[a]++; rSum[b] += edgeMid[e]; rCnt[b]++;
                    if (edgeFaces[e].Count == 1)
                    {
                        bSum[a] += m.Positions[b]; bCnt[a]++;
                        bSum[b] += m.Positions[a]; bCnt[b]++;
                    }
                }
                for (int v = 0; v < nv; v++)
                {
                    var p = m.Positions[v];
                    if (bCnt[v] == 2) newVerts[v] = (p * 6f + bSum[v]) / 8f;
                    else if (bCnt[v] > 0 || fCnt[v] == 0) newVerts[v] = p;
                    else
                    {
                        float n = fCnt[v];
                        var F = fSum[v] / fCnt[v];
                        var R = rSum[v] / rCnt[v];
                        newVerts[v] = (F + 2f * R + (n - 3f) * p) / n;
                    }
                }
            }
            else for (int v = 0; v < nv; v++) newVerts[v] = m.Positions[v];

            var pos = new List<Vector3>(newVerts);
            int edgeBase = pos.Count; pos.AddRange(edgePts);
            int faceBase = pos.Count; pos.AddRange(facePts);

            List<Vector4> cols = null;
            if (m.Colors != null)
            {
                cols = new List<Vector4>();
                for (int v = 0; v < nv; v++) cols.Add(v < m.Colors.Count ? m.Colors[v] : Vector4.One);
                for (int e = 0; e < edgeFaces.Count; e++)
                {
                    int a, b; Mesh.EdgeFromKey(edgeVerts[e], out a, out b);
                    cols.Add((cols[a] + cols[b]) * 0.5f);
                }
                foreach (var f in m.Faces)
                {
                    var c = Vector4.Zero; foreach (int v in f.V) c += cols[v];
                    cols.Add(c / f.V.Length);
                }
            }

            var faces = new List<Face>();
            for (int fi = 0; fi < m.Faces.Count; fi++)
            {
                var f = m.Faces[fi];
                int n = f.V.Length;
                for (int k = 0; k < n; k++)
                {
                    int v = f.V[k];
                    int eNext = edgeIndex[Mesh.EdgeKey(v, f.V[(k + 1) % n])];
                    int ePrev = edgeIndex[Mesh.EdgeKey(f.V[(k + n - 1) % n], v)];
                    faces.Add(new Face(new[] { v, edgeBase + eNext, faceBase + fi, edgeBase + ePrev }, f.Material));
                }
            }
            m.Positions = pos; m.Colors = cols; m.Faces = faces;
            m.UVs.Clear(); m.Normals.Clear();
            m.Changed();
        }

        public static bool MakeFace(Mesh m, IList<int> verts, int material)
        {
            var distinct = new List<int>();
            foreach (int v in verts) if (!distinct.Contains(v)) distinct.Add(v);
            if (distinct.Count < 3) return false;
            m.Faces.Add(new Face(distinct.ToArray(), material));
            m.Changed();
            return true;
        }

        /// <summary>Moves 'others' into 'target' (baking transforms into target's local space).</summary>
        public static void Join(SceneObject target, IEnumerable<SceneObject> others)
        {
            Matrix4x4 invT;
            if (!Matrix4x4.Invert(target.World, out invT)) invT = Matrix4x4.Identity;
            var m = target.Mesh;
            foreach (var o in others)
            {
                if (o == target) continue;
                var rel = o.World * invT;
                int pBase = m.Positions.Count, tBase = m.UVs.Count, nBase = m.Normals.Count;
                var src = o.Mesh;
                if (src.Colors != null && m.Colors == null) { m.Colors = new List<Vector4>(); for (int i = 0; i < pBase; i++) m.Colors.Add(Vector4.One); }
                for (int i = 0; i < src.Positions.Count; i++)
                {
                    m.Positions.Add(Vector3.Transform(src.Positions[i], rel));
                    if (m.Colors != null) m.Colors.Add(src.Colors != null && i < src.Colors.Count ? src.Colors[i] : Vector4.One);
                }
                m.UVs.AddRange(src.UVs);
                foreach (var n in src.Normals) m.Normals.Add(Vector3.Normalize(Vector3.TransformNormal(n, rel) + new Vector3(1e-9f)));
                bool flip = rel.GetDeterminant() < 0;
                foreach (var f in src.Faces)
                {
                    var nf = f.Clone();
                    for (int k = 0; k < nf.V.Length; k++) nf.V[k] += pBase;
                    if (nf.T != null) for (int k = 0; k < nf.T.Length; k++) nf.T[k] += tBase;
                    if (nf.N != null) for (int k = 0; k < nf.N.Length; k++) nf.N[k] += nBase;
                    if (flip) nf.Reverse();
                    m.Faces.Add(nf);
                }
            }
            m.Changed();
        }

        public static void ApplyTransform(SceneObject o)
        {
            o.Mesh = Scene.BakeWorld(o);
            o.Position = Vector3.Zero; o.Rotation = Quaternion.Identity; o.Scale = Vector3.One;
            o.Keys.Clear();
            o.Mesh.Changed();
        }

        public static void CenterOrigin(SceneObject o)
        {
            if (o.Mesh.Positions.Count == 0) return;
            Vector3 min, max; o.Mesh.GetBounds(out min, out max);
            var c = (min + max) * 0.5f;
            var worldC = Vector3.Transform(c, o.World);
            for (int i = 0; i < o.Mesh.Positions.Count; i++) o.Mesh.Positions[i] -= c;
            var delta = worldC - o.Position;
            o.Position = worldC;
            foreach (var k in o.Keys) k.Position += Vector3.Transform(c * k.Scale, k.Rotation);
            o.Mesh.Changed();
        }

        /// <summary>Splits selected faces out into a new object.</summary>
        public static SceneObject Separate(SceneObject o, HashSet<int> faces)
        {
            var copy = o.Clone();
            copy.Keys.Clear();
            var keepInCopy = new List<Face>();
            for (int i = 0; i < copy.Mesh.Faces.Count; i++) if (faces.Contains(i)) keepInCopy.Add(copy.Mesh.Faces[i]);
            copy.Mesh.Faces = keepInCopy;
            copy.Mesh.RemoveUnusedVertices();
            DeleteFaces(o.Mesh, faces);
            return copy;
        }
    }
}
