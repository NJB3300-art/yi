using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Gaming.Input;

namespace Facet3D
{
    public enum EditMode { Object, Vertex, Face }
    public enum ToolKind { Move, Rotate, Scale }
    public enum AxisLock { Free, X, Y, Z }

    /// <summary>All editor state and controller-driven interaction. UI-agnostic: MainPage forwards input and shows menus.</summary>
    public sealed class EditorController
    {
        public Scene Scene = new Scene();
        public readonly Camera Camera = new Camera();
        public readonly Selection Sel = new Selection();
        public readonly UndoStack History = new UndoStack();

        public EditMode Mode = EditMode.Object;
        public ToolKind Tool = ToolKind.Move;
        public AxisLock Axis = AxisLock.Free;
        public Vector2 Cursor = new Vector2(480, 270);
        public Vector2 ViewSize = new Vector2(960, 540);
        public bool ShowGrid = true;
        public bool InvertCameraY;
        public float CursorSpeed = 1f;
        public float Frame;
        public bool Playing;
        public bool AutoKey = true;
        public bool Transforming { get; private set; }

        /// <summary>Raised with a short user-facing message.</summary>
        public event Action<string> Message;

        Dictionary<SceneObject, Keyframe> _origObj;
        Dictionary<int, Vector3> _origVerts;
        SceneObject _editObj;
        Vector3 _pivot, _move;
        float _angle, _scale = 1f;

        void Say(string s) { var h = Message; if (h != null) h(s); }

        public Matrix4x4 ViewProj
        {
            get { return Camera.View * Camera.Projection(ViewSize.X / Math.Max(1f, ViewSize.Y)); }
        }

        // ------------------------------------------------------------------ frame update

        public void Update(GamepadInput pad, float dt)
        {
            dt = Math.Min(dt, 0.1f);
            bool lb = pad.Down(GamepadButtons.LeftShoulder);

            // Camera: right stick orbits (LB + right stick pans), triggers zoom.
            var rs = pad.RightStick;
            if (rs != Vector2.Zero)
            {
                if (lb) Camera.Pan(rs.X * dt * 0.9f, rs.Y * dt * 0.9f);
                else Camera.Orbit(rs.X * dt * 2.4f, (InvertCameraY ? -rs.Y : rs.Y) * dt * 1.9f);
            }
            float zoom = pad.RightTrigger - pad.LeftTrigger;
            if (zoom != 0) Camera.Zoom(zoom * dt * 2.4f);
            if (pad.Pressed(GamepadButtons.RightThumbstick)) FrameSelection();
            if (pad.Pressed(GamepadButtons.LeftThumbstick)) Cursor = ViewSize * 0.5f;

            if (Transforming)
            {
                UpdateTransform(pad, dt, lb);
                if (pad.Pressed(GamepadButtons.A)) ConfirmTransform();
                else if (pad.Pressed(GamepadButtons.B)) CancelTransform();
                else if (pad.Pressed(GamepadButtons.Y)) { Axis = (AxisLock)(((int)Axis + 1) % 4); RestartTransform(); }
                else if (pad.Pressed(GamepadButtons.RightShoulder)) { Tool = (ToolKind)(((int)Tool + 1) % 3); RestartTransform(); }
                return;
            }

            // Cursor.
            var ls = pad.LeftStick;
            if (ls != Vector2.Zero)
            {
                float mag = ls.Length();
                float speed = (180f + 820f * mag * mag) * CursorSpeed * (lb ? 0.35f : 1f);
                Cursor += new Vector2(ls.X, -ls.Y) / mag * speed * mag * dt;
                Cursor = Vector2.Clamp(Cursor, Vector2.Zero, ViewSize);
            }

            if (pad.Pressed(GamepadButtons.A)) SelectAtCursor(lb);
            if (pad.Pressed(GamepadButtons.B)) { if (lb) Undo(); else Deselect(); }
            if (pad.Pressed(GamepadButtons.X)) { if (lb) Redo(); else BeginTransform(true); }
            if (pad.Pressed(GamepadButtons.RightShoulder)) { Tool = (ToolKind)(((int)Tool + 1) % 3); Say("Tool: " + Tool); }
            if (pad.Pressed(GamepadButtons.View)) CycleMode();

            // Timeline on the D-pad.
            if (pad.Pressed(GamepadButtons.DPadLeft)) StepFrame(lb ? -10 : -1);
            if (pad.Pressed(GamepadButtons.DPadRight)) StepFrame(lb ? 10 : 1);
            if (pad.Pressed(GamepadButtons.DPadUp)) { if (lb) DeleteKey(); else InsertKey(); }
            if (pad.Pressed(GamepadButtons.DPadDown)) TogglePlay();

            if (Playing)
            {
                Frame += dt * Scene.Fps;
                if (Frame > Scene.FrameEnd) Frame = 0;
                EvaluateAnimation();
            }
        }

        // ------------------------------------------------------------------ selection

        public void SelectAtCursor(bool additive)
        {
            var vp = ViewProj;
            var ray = Picking.ScreenRay(Cursor, ViewSize, vp);
            switch (Mode)
            {
                case EditMode.Object:
                    {
                        var o = Picking.PickObject(Scene, ray);
                        if (o == null) { if (!additive) Sel.SelectOnly(null); }
                        else if (additive) Sel.Toggle(o);
                        else Sel.SelectOnly(o);
                        break;
                    }
                case EditMode.Vertex:
                    {
                        if (Sel.Active == null) return;
                        int v = Picking.PickVertex(Sel.Active, Cursor, ViewSize, vp, 30f);
                        if (v < 0) { if (!additive) { Sel.Verts.Clear(); Sel.VertOrder.Clear(); } }
                        else Sel.ToggleVert(v, additive);
                        break;
                    }
                case EditMode.Face:
                    {
                        if (Sel.Active == null) return;
                        int f; float t;
                        if (Picking.PickFace(Sel.Active, ray, out f, out t)) Sel.ToggleFace(f, additive);
                        else if (!additive) Sel.Faces.Clear();
                        break;
                    }
            }
        }

        public void Deselect()
        {
            if (Mode == EditMode.Object) Sel.SelectOnly(null);
            else if (Sel.Verts.Count == 0 && Sel.Faces.Count == 0) SetMode(EditMode.Object);
            else Sel.ClearElements();
        }

        public void SelectAll()
        {
            if (Mode == EditMode.Object)
            {
                Sel.Objects.Clear();
                Sel.Objects.AddRange(Scene.Objects.Where(o => o.Visible));
                if (Sel.Active == null || !Sel.Objects.Contains(Sel.Active)) Sel.Active = Sel.Objects.FirstOrDefault();
            }
            else if (Sel.Active != null)
            {
                if (Mode == EditMode.Vertex)
                {
                    Sel.Verts.Clear(); Sel.VertOrder.Clear();
                    for (int i = 0; i < Sel.Active.Mesh.Positions.Count; i++) { Sel.Verts.Add(i); Sel.VertOrder.Add(i); }
                }
                else
                {
                    Sel.Faces.Clear();
                    for (int i = 0; i < Sel.Active.Mesh.Faces.Count; i++) Sel.Faces.Add(i);
                }
            }
        }

        public void InvertSelection()
        {
            if (Mode == EditMode.Object)
            {
                var inv = Scene.Objects.Where(o => o.Visible && !Sel.Objects.Contains(o)).ToList();
                Sel.Objects.Clear(); Sel.Objects.AddRange(inv);
                Sel.Active = inv.FirstOrDefault();
            }
            else if (Sel.Active != null && Mode == EditMode.Vertex)
            {
                var n = Enumerable.Range(0, Sel.Active.Mesh.Positions.Count).Where(i => !Sel.Verts.Contains(i)).ToList();
                Sel.Verts.Clear(); Sel.VertOrder.Clear();
                foreach (int i in n) { Sel.Verts.Add(i); Sel.VertOrder.Add(i); }
            }
            else if (Sel.Active != null)
            {
                var n = Enumerable.Range(0, Sel.Active.Mesh.Faces.Count).Where(i => !Sel.Faces.Contains(i)).ToList();
                Sel.Faces.Clear();
                foreach (int i in n) Sel.Faces.Add(i);
            }
        }

        public void SelectNextObject(int dir)
        {
            if (Scene.Objects.Count == 0) return;
            int i = Sel.Active != null ? Scene.Objects.IndexOf(Sel.Active) : -1;
            i = ((i + dir) % Scene.Objects.Count + Scene.Objects.Count) % Scene.Objects.Count;
            if (Mode != EditMode.Object) SetMode(EditMode.Object);
            Sel.SelectOnly(Scene.Objects[i]);
            Say("Selected " + Scene.Objects[i].Name);
        }

        public void CycleMode()
        {
            SetMode(Mode == EditMode.Object ? EditMode.Vertex : Mode == EditMode.Vertex ? EditMode.Face : EditMode.Object);
        }

        public void SetMode(EditMode m)
        {
            if (m != EditMode.Object && Sel.Active == null)
            {
                Say("Select an object first, then switch to edit mode");
                return;
            }
            if (m != EditMode.Object)
            {
                // Edit mode works on the active object only.
                Sel.Objects.Clear(); Sel.Objects.Add(Sel.Active);
            }
            if (m != Mode) Sel.ClearElements();
            Mode = m;
            Say(m == EditMode.Object ? "Object mode" : m + " edit mode");
        }

        HashSet<int> EditVerts()
        {
            var set = new HashSet<int>();
            if (Sel.Active == null) return set;
            if (Mode == EditMode.Vertex) { foreach (int v in Sel.Verts) if (v < Sel.Active.Mesh.Positions.Count) set.Add(v); }
            else if (Mode == EditMode.Face)
                foreach (int f in Sel.Faces)
                    if (f < Sel.Active.Mesh.Faces.Count) foreach (int v in Sel.Active.Mesh.Faces[f].V) set.Add(v);
            return set;
        }

        public bool TryGetPivot(out Vector3 p)
        {
            p = Vector3.Zero;
            if (Transforming) { p = _pivot + (Tool == ToolKind.Move ? _move : Vector3.Zero); return true; }
            if (Mode == EditMode.Object)
            {
                if (Sel.Objects.Count == 0) return false;
                foreach (var o in Sel.Objects) p += o.Position;
                p /= Sel.Objects.Count;
                return true;
            }
            var verts = EditVerts();
            if (verts.Count == 0 || Sel.Active == null) return false;
            var w = Sel.Active.World;
            foreach (int v in verts) p += Vector3.Transform(Sel.Active.Mesh.Positions[v], w);
            p /= verts.Count;
            return true;
        }

        // ------------------------------------------------------------------ transform session

        public void BeginTransform(bool pushUndo)
        {
            if (Mode == EditMode.Object && Sel.Objects.Count == 0) { Say("Nothing selected. Point at an object and press A."); return; }
            var verts = EditVerts();
            if (Mode != EditMode.Object && verts.Count == 0) { Say(Mode == EditMode.Vertex ? "Select vertices with A first" : "Select faces with A first"); return; }
            if (Playing) Playing = false;
            if (pushUndo) History.Push(Scene);
            TryGetPivot(out _pivot);
            _origObj = new Dictionary<SceneObject, Keyframe>();
            foreach (var o in Sel.Objects) _origObj[o] = new Keyframe { Position = o.Position, Rotation = o.Rotation, Scale = o.Scale };
            _origVerts = null;
            _editObj = null;
            if (Mode != EditMode.Object)
            {
                _editObj = Sel.Active;
                _origVerts = new Dictionary<int, Vector3>();
                foreach (int v in verts) _origVerts[v] = _editObj.Mesh.Positions[v];
            }
            _move = Vector3.Zero; _angle = 0; _scale = 1f;
            Transforming = true;
        }

        void RestartTransform()
        {
            _move = Vector3.Zero; _angle = 0; _scale = 1f;
            ApplyTransform();
            Say(Tool + (Axis == AxisLock.Free ? " (free)" : " along " + Axis));
        }

        Vector3 AxisVec()
        {
            switch (Axis)
            {
                case AxisLock.X: return Vector3.UnitX;
                case AxisLock.Y: return Vector3.UnitY;
                case AxisLock.Z: return Vector3.UnitZ;
            }
            return Vector3.Zero;
        }

        void UpdateTransform(GamepadInput pad, float dt, bool precise)
        {
            var ls = pad.LeftStick;
            // D-pad gives small precise nudges while transforming.
            if (pad.Pressed(GamepadButtons.DPadRight)) ls.X += 6f;
            if (pad.Pressed(GamepadButtons.DPadLeft)) ls.X -= 6f;
            if (pad.Pressed(GamepadButtons.DPadUp)) ls.Y += 6f;
            if (pad.Pressed(GamepadButtons.DPadDown)) ls.Y -= 6f;
            if (ls == Vector2.Zero) return;
            float k = dt * (precise ? 0.2f : 1f);
            switch (Tool)
            {
                case ToolKind.Move:
                    {
                        float dist = Math.Max(0.5f, Vector3.Distance(Camera.Eye, _pivot));
                        if (Axis == AxisLock.Free) _move += (Camera.Right * ls.X + Camera.Up * ls.Y) * dist * 0.55f * k;
                        else
                        {
                            var ax = AxisVec();
                            float amount = ls.X;
                            Vector2 s0, s1; float d0, d1;
                            var vp = ViewProj;
                            if (Picking.Project(_pivot, vp, ViewSize, out s0, out d0) && Picking.Project(_pivot + ax, vp, ViewSize, out s1, out d1))
                            {
                                var sd = s1 - s0; sd.Y = -sd.Y;
                                float len = sd.Length();
                                if (len > 1e-3f) amount = Vector2.Dot(ls, sd / len);
                            }
                            _move += ax * amount * dist * 0.55f * k;
                        }
                        break;
                    }
                case ToolKind.Rotate:
                    _angle -= (Math.Abs(ls.X) >= Math.Abs(ls.Y) ? ls.X : -ls.Y) * 2.2f * k;
                    break;
                case ToolKind.Scale:
                    _scale *= (float)Math.Exp((Math.Abs(ls.Y) >= Math.Abs(ls.X) ? ls.Y : ls.X) * 1.4f * k);
                    _scale = Math.Max(0.001f, Math.Min(1000f, _scale));
                    break;
            }
            ApplyTransform();
        }

        void ApplyTransform()
        {
            if (!Transforming) return;
            Vector3 rotAxis = Axis == AxisLock.Free ? -Camera.Forward : AxisVec();
            var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(rotAxis), _angle);
            Vector3 sv = Axis == AxisLock.Free ? new Vector3(_scale) : Vector3.One + AxisVec() * (_scale - 1f);
            if (Mode == EditMode.Object || _editObj == null)
            {
                foreach (var kv in _origObj)
                {
                    var o = kv.Key; var k = kv.Value;
                    o.Position = k.Position; o.Rotation = k.Rotation; o.Scale = k.Scale;
                    switch (Tool)
                    {
                        case ToolKind.Move: o.Position = k.Position + _move; break;
                        case ToolKind.Rotate:
                            o.Rotation = Quaternion.Normalize(Quaternion.Concatenate(k.Rotation, q));
                            o.Position = _pivot + Vector3.Transform(k.Position - _pivot, q);
                            break;
                        case ToolKind.Scale:
                            if (Axis == AxisLock.Free) o.Scale = k.Scale * _scale;
                            else
                            {
                                // Scale along the world axis expressed in the object's local frame.
                                var localAxis = Vector3.Transform(AxisVec(), Quaternion.Inverse(k.Rotation));
                                var la = new Vector3(Math.Abs(localAxis.X), Math.Abs(localAxis.Y), Math.Abs(localAxis.Z));
                                o.Scale = k.Scale * (Vector3.One + la * (_scale - 1f));
                            }
                            o.Position = _pivot + (k.Position - _pivot) * sv;
                            break;
                    }
                }
            }
            else
            {
                var m = _editObj.Mesh;
                var w = _editObj.World;
                Matrix4x4 inv;
                if (!Matrix4x4.Invert(w, out inv)) return;
                foreach (var kv in _origVerts)
                {
                    var wp = Vector3.Transform(kv.Value, w);
                    switch (Tool)
                    {
                        case ToolKind.Move: wp += _move; break;
                        case ToolKind.Rotate: wp = _pivot + Vector3.Transform(wp - _pivot, q); break;
                        case ToolKind.Scale: wp = _pivot + (wp - _pivot) * sv; break;
                    }
                    if (kv.Key < m.Positions.Count) m.Positions[kv.Key] = Vector3.Transform(wp, inv);
                }
                m.Changed();
            }
        }

        void ConfirmTransform()
        {
            Transforming = false;
            if (Mode == EditMode.Object && AutoKey)
            {
                int f = (int)Math.Round(Frame);
                bool keyed = false;
                foreach (var o in _origObj.Keys) if (o.Keys.Count > 0) { o.SetKey(f); keyed = true; }
                if (keyed) Say("Auto-key at frame " + f);
            }
            if (_editObj != null)
            {
                // Imported per-corner normals are stale after moving vertices.
                foreach (var f in _editObj.Mesh.Faces) f.N = null;
                _editObj.Mesh.Changed();
            }
        }

        void CancelTransform()
        {
            _move = Vector3.Zero; _angle = 0; _scale = 1f;
            ApplyTransform();
            Transforming = false;
            History.DiscardLast();
            Say("Cancelled");
        }

        // ------------------------------------------------------------------ history

        public void Undo()
        {
            if (Transforming) return;
            var s = History.Undo(Scene);
            if (s == null) { Say("Nothing to undo"); return; }
            ReplaceScene(s);
            Say("Undo");
        }

        public void Redo()
        {
            if (Transforming) return;
            var s = History.Redo(Scene);
            if (s == null) { Say("Nothing to redo"); return; }
            ReplaceScene(s);
            Say("Redo");
        }

        void ReplaceScene(Scene s)
        {
            var old = Scene;
            Scene = s;
            Sel.Remap(old, s);
            if (Mode != EditMode.Object && Sel.Active == null) Mode = EditMode.Object;
            EvaluateAnimation();
        }

        // ------------------------------------------------------------------ animation

        public void StepFrame(int d) { SetFrame((float)Math.Round(Frame) + d); }

        public void SetFrame(float f)
        {
            Frame = Math.Max(0, Math.Min(Scene.FrameEnd, f));
            EvaluateAnimation();
        }

        public void EvaluateAnimation() { foreach (var o in Scene.Objects) o.Evaluate(Frame); }

        public void TogglePlay()
        {
            Playing = !Playing;
            Say(Playing ? "Playing" : "Paused");
        }

        public void InsertKey()
        {
            if (Mode != EditMode.Object || Sel.Objects.Count == 0) { Say("Select objects in Object mode to keyframe them"); return; }
            History.Push(Scene);
            int f = (int)Math.Round(Frame);
            foreach (var o in Sel.Objects) o.SetKey(f);
            Say("Keyframe set at frame " + f);
        }

        public void DeleteKey()
        {
            if (Sel.Objects.Count == 0) return;
            History.Push(Scene);
            int f = (int)Math.Round(Frame);
            bool any = false;
            foreach (var o in Sel.Objects) any |= o.RemoveKey(f);
            if (!any) { History.DiscardLast(); Say("No keyframe at frame " + f); }
            else Say("Keyframe removed at frame " + f);
        }

        public void ClearAnimation()
        {
            if (Sel.Objects.Count == 0) return;
            History.Push(Scene);
            foreach (var o in Sel.Objects) o.Keys.Clear();
            Say("Animation cleared");
        }

        public void SetFrameEnd(int end)
        {
            Scene.FrameEnd = Math.Max(1, end);
            if (Frame > Scene.FrameEnd) SetFrame(Scene.FrameEnd);
        }

        // ------------------------------------------------------------------ scene actions

        public void NewScene()
        {
            History.Push(Scene);
            Scene = new Scene();
            var cube = new SceneObject { Name = "Cube", Mesh = Primitives.Cube() };
            Scene.Objects.Add(cube);
            Sel.SelectOnly(cube);
            Mode = EditMode.Object;
            Frame = 0; Playing = false;
            var c = new Camera();
            Camera.Target = c.Target; Camera.Yaw = c.Yaw; Camera.Pitch = c.Pitch; Camera.Distance = c.Distance;
            Cursor = ViewSize * 0.5f;
        }

        public void AddPrimitive(PrimitiveKind kind)
        {
            History.Push(Scene);
            if (Mode != EditMode.Object) Mode = EditMode.Object;
            var o = new SceneObject
            {
                Name = Scene.UniqueName(kind.ToString()),
                Mesh = Primitives.Create(kind),
                Position = Camera.Target,
                Shading = kind == PrimitiveKind.Sphere || kind == PrimitiveKind.Torus ? Shading.Smooth : Shading.Auto
            };
            Scene.Objects.Add(o);
            Sel.SelectOnly(o);
            Say("Added " + o.Name);
        }

        public void ImportScene(Scene imported, string fileName)
        {
            History.Push(Scene);
            if (Mode != EditMode.Object) Mode = EditMode.Object;
            var added = Scene.Merge(imported);
            Sel.ClearAll();
            Sel.Objects.AddRange(added);
            Sel.Active = added.FirstOrDefault();
            EvaluateAnimation();
            FrameSelection();
            int verts = added.Sum(o => o.Mesh.Positions.Count);
            int faces = added.Sum(o => o.Mesh.Faces.Count);
            Say("Imported " + fileName + ": " + added.Count + " object(s), " + verts.ToString("N0") + " vertices, " + faces.ToString("N0") + " faces");
        }

        /// <summary>Scene to hand to an exporter (whole scene or just the selected objects).</summary>
        public Scene SceneForExport(bool selectedOnly)
        {
            if (!selectedOnly || Sel.Objects.Count == 0) return Scene;
            var s = new Scene();
            s.Materials.Clear();
            s.Materials.AddRange(Scene.Materials);
            s.Objects.AddRange(Sel.Objects);
            s.FrameEnd = Scene.FrameEnd; s.Fps = Scene.Fps;
            return s;
        }

        public void DeleteSelected()
        {
            if (Mode == EditMode.Object)
            {
                if (Sel.Objects.Count == 0) { Say("Nothing selected"); return; }
                History.Push(Scene);
                int n = Sel.Objects.Count;
                foreach (var o in Sel.Objects) Scene.Objects.Remove(o);
                Sel.SelectOnly(null);
                Say("Deleted " + n + " object(s)");
                return;
            }
            var a = Sel.Active;
            if (a == null) return;
            if (Mode == EditMode.Vertex && Sel.Verts.Count > 0)
            {
                History.Push(Scene);
                MeshOps.DeleteVertices(a.Mesh, new HashSet<int>(Sel.Verts));
            }
            else if (Mode == EditMode.Face && Sel.Faces.Count > 0)
            {
                History.Push(Scene);
                MeshOps.DeleteFaces(a.Mesh, new HashSet<int>(Sel.Faces));
            }
            else { Say("Nothing selected"); return; }
            Sel.ClearElements();
            Say("Deleted");
        }

        public void Duplicate()
        {
            if (Mode != EditMode.Object) { Say("Duplicate works in Object mode"); return; }
            if (Sel.Objects.Count == 0) { Say("Nothing selected"); return; }
            History.Push(Scene);
            var copies = new List<SceneObject>();
            foreach (var o in Sel.Objects)
            {
                var c = o.Clone();
                c.Name = Scene.UniqueName(o.Name);
                Scene.Objects.Add(c);
                copies.Add(c);
            }
            Sel.ClearAll();
            Sel.Objects.AddRange(copies);
            Sel.Active = copies[0];
            Tool = ToolKind.Move;
            BeginTransform(false);
            Say("Duplicated - move with the left stick, A to place");
        }

        public void Extrude()
        {
            var a = Sel.Active;
            if (Mode != EditMode.Face || a == null || Sel.Faces.Count == 0) { Say("Extrude: switch to Face mode (View button) and select faces"); return; }
            History.Push(Scene);
            var m = a.Mesh;
            var n = MeshOps.AverageNormal(m, Sel.Faces);
            Vector3 min, max; m.GetBounds(out min, out max);
            float step = Math.Max(0.01f, (max - min).Length() * 0.08f);
            var newVerts = MeshOps.Extrude(m, new HashSet<int>(Sel.Faces));
            foreach (int v in newVerts) m.Positions[v] += n * step;
            m.Changed();
            Tool = ToolKind.Move;
            // Lock to the axis closest to the world-space normal so the stick pulls the faces out.
            var wn = Vector3.TransformNormal(n, a.World);
            var an = new Vector3(Math.Abs(wn.X), Math.Abs(wn.Y), Math.Abs(wn.Z));
            Axis = an.X >= an.Y && an.X >= an.Z ? AxisLock.X : an.Y >= an.Z ? AxisLock.Y : AxisLock.Z;
            BeginTransform(false);
            Say("Extruded - pull with the left stick, Y changes axis, A to confirm");
        }

        IEnumerable<SceneObject> Targets()
        {
            if (Mode != EditMode.Object) return Sel.Active != null ? new[] { Sel.Active } : new SceneObject[0];
            return Sel.Objects.ToList();
        }

        bool RequireTargets()
        {
            if (Targets().Any()) return true;
            Say("Select an object first");
            return false;
        }

        public void Subdivide(bool smooth)
        {
            if (!RequireTargets()) return;
            if (Targets().Sum(o => (long)o.Mesh.Faces.Count) > 250000) { Say("Mesh too dense to subdivide again"); return; }
            History.Push(Scene);
            foreach (var o in Targets())
            {
                MeshOps.Subdivide(o.Mesh, smooth);
                if (smooth) o.Shading = Shading.Smooth;
            }
            Sel.ClearElements();
            Say(smooth ? "Smooth subdivision applied" : "Subdivided");
        }

        public void Triangulate()
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            foreach (var o in Targets()) MeshOps.Triangulate(o.Mesh);
            Sel.ClearElements();
            Say("Triangulated");
        }

        public void FlipNormals()
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            if (Mode == EditMode.Face && Sel.Faces.Count > 0) MeshOps.FlipNormals(Sel.Active.Mesh, Sel.Faces);
            else foreach (var o in Targets()) MeshOps.FlipNormals(o.Mesh, null);
            Say("Normals flipped");
        }

        public void MergeByDistance()
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            int removed = 0;
            foreach (var o in Targets())
            {
                Vector3 min, max; o.Mesh.GetBounds(out min, out max);
                removed += MeshOps.MergeByDistance(o.Mesh, Math.Max(1e-5f, (max - min).Length() * 1e-4f));
            }
            Sel.ClearElements();
            Say("Merged " + removed + " vertices");
        }

        public void MakeFace()
        {
            if (Mode != EditMode.Vertex || Sel.Active == null || Sel.VertOrder.Count < 3) { Say("Make face: select 3+ vertices in order (Vertex mode)"); return; }
            History.Push(Scene);
            int mat = Sel.Active.Mesh.Faces.Count > 0 ? Sel.Active.Mesh.Faces[0].Material : 0;
            MeshOps.MakeFace(Sel.Active.Mesh, Sel.VertOrder.ToList(), mat);
            Say("Face created");
        }

        public void Separate()
        {
            if (Mode != EditMode.Face || Sel.Active == null || Sel.Faces.Count == 0) { Say("Separate: select faces in Face mode"); return; }
            History.Push(Scene);
            var part = MeshOps.Separate(Sel.Active, new HashSet<int>(Sel.Faces));
            part.Name = Scene.UniqueName(Sel.Active.Name + ".part");
            Scene.Objects.Add(part);
            Mode = EditMode.Object;
            Sel.SelectOnly(part);
            Say("Separated into " + part.Name);
        }

        public void Join()
        {
            if (Mode != EditMode.Object || Sel.Objects.Count < 2) { Say("Join: select 2+ objects (LB + A adds to selection)"); return; }
            History.Push(Scene);
            var target = Sel.Active ?? Sel.Objects[0];
            var others = Sel.Objects.Where(o => o != target).ToList();
            MeshOps.Join(target, others);
            foreach (var o in others) Scene.Objects.Remove(o);
            Sel.SelectOnly(target);
            Say("Joined into " + target.Name);
        }

        public void ApplyTransforms()
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            foreach (var o in Targets()) MeshOps.ApplyTransform(o);
            Say("Transforms applied");
        }

        public void CenterOrigin()
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            foreach (var o in Targets()) MeshOps.CenterOrigin(o);
            Say("Origin centred");
        }

        public void ResetTransforms()
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            foreach (var o in Targets()) { o.Position = Vector3.Zero; o.Rotation = Quaternion.Identity; o.Scale = Vector3.One; }
            Say("Transforms reset");
        }

        public void SetShading(Shading s)
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            foreach (var o in Targets()) o.Shading = s;
            Say("Shading: " + s);
        }

        public void ToggleHidden()
        {
            if (Mode != EditMode.Object) return;
            if (Sel.Objects.Count > 0)
            {
                foreach (var o in Sel.Objects) o.Visible = false;
                Say("Hidden " + Sel.Objects.Count + " object(s)");
                Sel.SelectOnly(null);
            }
            else
            {
                foreach (var o in Scene.Objects) o.Visible = true;
                Say("All objects shown");
            }
        }

        public void Rename(string name)
        {
            if (Sel.Active == null || string.IsNullOrWhiteSpace(name)) return;
            History.Push(Scene);
            Sel.Active.Name = name.Trim();
        }

        // ------------------------------------------------------------------ materials

        public int NewMaterial()
        {
            History.Push(Scene);
            var rnd = new Random();
            var m = new Material
            {
                Name = "Material" + Scene.Materials.Count,
                Color = new Vector4(0.3f + (float)rnd.NextDouble() * 0.6f, 0.3f + (float)rnd.NextDouble() * 0.6f, 0.3f + (float)rnd.NextDouble() * 0.6f, 1f)
            };
            Scene.Materials.Add(m);
            Scene.MaterialsChanged();
            return Scene.Materials.Count - 1;
        }

        public void AssignMaterial(int index)
        {
            if (!RequireTargets()) return;
            History.Push(Scene);
            if (Mode == EditMode.Face && Sel.Active != null && Sel.Faces.Count > 0)
            {
                foreach (int f in Sel.Faces) if (f < Sel.Active.Mesh.Faces.Count) Sel.Active.Mesh.Faces[f].Material = index;
                Sel.Active.Mesh.Changed();
            }
            else foreach (var o in Targets()) { foreach (var f in o.Mesh.Faces) f.Material = index; o.Mesh.Changed(); }
            Say("Material '" + Scene.MaterialAt(index).Name + "' assigned");
        }

        /// <summary>Material of the active object/face (for the editor panel).</summary>
        public int CurrentMaterialIndex()
        {
            var a = Sel.Active;
            if (a == null || a.Mesh.Faces.Count == 0) return 0;
            if (Mode == EditMode.Face && Sel.Faces.Count > 0)
            {
                int f = Sel.Faces.First();
                if (f < a.Mesh.Faces.Count) return a.Mesh.Faces[f].Material;
            }
            return a.Mesh.Faces[0].Material;
        }

        // ------------------------------------------------------------------ view

        public void FrameSelection()
        {
            Vector3 min, max; bool any;
            if (Mode != EditMode.Object && Sel.Active != null && EditVerts().Count > 0)
            {
                min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
                var w = Sel.Active.World;
                foreach (int v in EditVerts())
                {
                    var p = Vector3.Transform(Sel.Active.Mesh.Positions[v], w);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
                any = true;
            }
            else
            {
                Scene.GetBounds(Sel.Objects.Count > 0 ? (IEnumerable<SceneObject>)Sel.Objects : Scene.Objects, out min, out max, out any);
            }
            if (any) Camera.Frame(min, max);
            Cursor = ViewSize * 0.5f;
        }

        public void ViewPreset(int which)
        {
            switch (which)
            {
                case 0: Camera.Yaw = 0; Camera.Pitch = 0; break;                       // front
                case 1: Camera.Yaw = (float)(Math.PI / 2); Camera.Pitch = 0; break;    // right
                case 2: Camera.Yaw = 0; Camera.Pitch = 1.55f; break;                   // top
                default: Camera.Yaw = 0.75f; Camera.Pitch = 0.5f; break;               // perspective
            }
        }

        // ------------------------------------------------------------------ HUD text

        public string ModeText
        {
            get { return Mode == EditMode.Object ? "OBJECT MODE" : Mode == EditMode.Vertex ? "EDIT - VERTEX" : "EDIT - FACE"; }
        }

        public string ToolText
        {
            get
            {
                string s = "Tool: " + Tool + "   Axis: " + (Axis == AxisLock.Free ? "Free (view)" : Axis.ToString());
                if (Transforming) s = "> " + s.ToUpperInvariant();
                return s;
            }
        }

        public string InfoText
        {
            get
            {
                var a = Sel.Active;
                if (a == null) return Scene.Objects.Count + " object(s) - nothing selected";
                string sel = Mode == EditMode.Object ? Sel.Objects.Count + " selected"
                    : Mode == EditMode.Vertex ? Sel.Verts.Count + " vertices selected" : Sel.Faces.Count + " faces selected";
                return a.Name + "  |  " + a.Mesh.Positions.Count.ToString("N0") + " verts, " + a.Mesh.Faces.Count.ToString("N0") + " faces  |  " + sel;
            }
        }

        public string HintText
        {
            get
            {
                if (Transforming) return "L-stick: " + Tool.ToString().ToLowerInvariant() + "   LB: precise   Y: axis   RB: tool   A: confirm   B: cancel";
                if (Mode == EditMode.Object) return "A: select   LB+A: add   X: " + Tool.ToString().ToLowerInvariant() + "   RB: tool   View: edit mode   Y: tools   Menu: file";
                return "A: pick " + (Mode == EditMode.Vertex ? "vertex" : "face") + "   LB+A: add   X: " + Tool.ToString().ToLowerInvariant() + "   B: deselect   View: mode   Y: tools";
            }
        }
    }
}
