using System;
using System.Collections.Generic;
using System.Numerics;

namespace Facet3D
{
    public enum PrimitiveKind { Cube, Plane, Sphere, Cylinder, Cone, Torus }

    public static class Primitives
    {
        public static Mesh Create(PrimitiveKind kind)
        {
            switch (kind)
            {
                case PrimitiveKind.Plane: return Plane();
                case PrimitiveKind.Sphere: return Sphere(24, 12);
                case PrimitiveKind.Cylinder: return Cylinder(24, false);
                case PrimitiveKind.Cone: return Cylinder(24, true);
                case PrimitiveKind.Torus: return Torus(32, 12, 1f, 0.35f);
                default: return Cube();
            }
        }

        public static Mesh Cube()
        {
            var m = new Mesh();
            m.Positions.AddRange(new[]
            {
                new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
                new Vector3(-1,-1, 1), new Vector3(1,-1, 1), new Vector3(1,1, 1), new Vector3(-1,1, 1)
            });
            m.Faces.Add(new Face(new[] { 4, 5, 6, 7 }));
            m.Faces.Add(new Face(new[] { 1, 0, 3, 2 }));
            m.Faces.Add(new Face(new[] { 5, 1, 2, 6 }));
            m.Faces.Add(new Face(new[] { 0, 4, 7, 3 }));
            m.Faces.Add(new Face(new[] { 7, 6, 2, 3 }));
            m.Faces.Add(new Face(new[] { 0, 1, 5, 4 }));
            AddBoxUVs(m);
            return m;
        }

        static void AddBoxUVs(Mesh m)
        {
            m.UVs.AddRange(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            foreach (var f in m.Faces) if (f.V.Length == 4) f.T = new[] { 0, 1, 2, 3 };
        }

        public static Mesh Plane()
        {
            var m = new Mesh();
            m.Positions.AddRange(new[] { new Vector3(-1, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, -1), new Vector3(-1, 0, -1) });
            m.Faces.Add(new Face(new[] { 0, 1, 2, 3 }));
            AddBoxUVs(m);
            return m;
        }

        public static Mesh Sphere(int segments, int rings)
        {
            var m = new Mesh();
            m.Positions.Add(new Vector3(0, 1, 0));
            for (int r = 1; r < rings; r++)
            {
                double phi = Math.PI * r / rings;
                for (int s = 0; s < segments; s++)
                {
                    double th = 2 * Math.PI * s / segments;
                    m.Positions.Add(new Vector3((float)(Math.Sin(phi) * Math.Cos(th)), (float)Math.Cos(phi), (float)(Math.Sin(phi) * Math.Sin(th))));
                }
            }
            int bottom = m.Positions.Count;
            m.Positions.Add(new Vector3(0, -1, 0));
            Func<int, int, int> idx = (r, s) => 1 + (r - 1) * segments + ((s % segments + segments) % segments);
            for (int s = 0; s < segments; s++) m.Faces.Add(new Face(new[] { 0, idx(1, s + 1), idx(1, s) }));
            for (int r = 1; r < rings - 1; r++)
                for (int s = 0; s < segments; s++)
                    m.Faces.Add(new Face(new[] { idx(r, s), idx(r, s + 1), idx(r + 1, s + 1), idx(r + 1, s) }));
            for (int s = 0; s < segments; s++) m.Faces.Add(new Face(new[] { bottom, idx(rings - 1, s), idx(rings - 1, s + 1) }));
            OrientOutward(m, p => p);
            return m;
        }

        public static Mesh Cylinder(int segments, bool cone)
        {
            var m = new Mesh();
            for (int s = 0; s < segments; s++)
            {
                double th = 2 * Math.PI * s / segments;
                m.Positions.Add(new Vector3((float)Math.Cos(th), -1, (float)Math.Sin(th)));
            }
            if (cone)
            {
                int apex = m.Positions.Count;
                m.Positions.Add(new Vector3(0, 1, 0));
                for (int s = 0; s < segments; s++) m.Faces.Add(new Face(new[] { s, (s + 1) % segments, apex }));
            }
            else
            {
                for (int s = 0; s < segments; s++)
                {
                    double th = 2 * Math.PI * s / segments;
                    m.Positions.Add(new Vector3((float)Math.Cos(th), 1, (float)Math.Sin(th)));
                }
                for (int s = 0; s < segments; s++)
                {
                    int n = (s + 1) % segments;
                    m.Faces.Add(new Face(new[] { s, n, segments + n, segments + s }));
                }
                var top = new int[segments];
                for (int s = 0; s < segments; s++) top[s] = segments + s;
                m.Faces.Add(new Face(top));
            }
            var bottomCap = new int[segments];
            for (int s = 0; s < segments; s++) bottomCap[s] = s;
            m.Faces.Add(new Face(bottomCap));
            OrientOutward(m, p => p);
            return m;
        }

        public static Mesh Torus(int segments, int sides, float major, float minor)
        {
            var m = new Mesh();
            for (int s = 0; s < segments; s++)
            {
                double u = 2 * Math.PI * s / segments;
                for (int t = 0; t < sides; t++)
                {
                    double v = 2 * Math.PI * t / sides;
                    double r = major + minor * Math.Cos(v);
                    m.Positions.Add(new Vector3((float)(r * Math.Cos(u)), (float)(minor * Math.Sin(v)), (float)(r * Math.Sin(u))));
                }
            }
            for (int s = 0; s < segments; s++)
                for (int t = 0; t < sides; t++)
                {
                    int s1 = (s + 1) % segments, t1 = (t + 1) % sides;
                    m.Faces.Add(new Face(new[] { s * sides + t, s * sides + t1, s1 * sides + t1, s1 * sides + t }));
                }
            OrientOutward(m, p =>
            {
                var ring = new Vector3(p.X, 0, p.Z);
                float l = ring.Length();
                ring = l > 1e-6f ? ring / l * major : Vector3.Zero;
                return p - ring;
            });
            return m;
        }

        /// <summary>Flips any face whose normal points against outward(centre).</summary>
        public static void OrientOutward(Mesh m, Func<Vector3, Vector3> outward)
        {
            foreach (var f in m.Faces)
            {
                var c = m.FaceCenter(f);
                if (Vector3.Dot(m.FaceNormal(f), outward(c)) < 0) f.Reverse();
            }
            m.Changed();
        }
    }
}
