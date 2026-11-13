using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Gaming.Input;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Facet3D
{
    sealed class UiMenuItem
    {
        public string Label;
        public Action Run;
        /// <summary>True when Run opens another menu/panel itself (so the menu is not auto-closed).</summary>
        public bool Stay;

        public UiMenuItem(string label, Action run, bool stay = false) { Label = label; Run = run; Stay = stay; }
    }

    public sealed partial class MainPage : Page
    {
        public static MainPage Current { get; private set; }

        readonly EditorController _ed = new EditorController();
        readonly GamepadInput _pad = new GamepadInput();
        readonly Renderer _renderer = new Renderer();
        readonly Stopwatch _clock = new Stopwatch();
        readonly List<KeyValuePair<string, Func<List<UiMenuItem>>>> _menus = new List<KeyValuePair<string, Func<List<UiMenuItem>>>>();
        readonly DispatcherTimer _toastTimer = new DispatcherTimer();
        double _lastTime;
        double _inputCooldown;
        bool _busy;
        bool _rendererOk;
        Action<string> _inputOk;
        Material _editingMat;
        bool _matLoading;
        string _keySig = "";

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            _toastTimer.Interval = TimeSpan.FromSeconds(3.5);
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };
            _ed.Message += ShowToast;
            HelpText.Text = HelpContent;
            Loaded += OnLoaded;
            Unloaded += (s, e) => { CompositionTarget.Rendering -= OnRendering; _renderer.Dispose(); };
        }

        // ------------------------------------------------------------------ lifecycle

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            try
            {
                _renderer.Initialize(Swap);
                _rendererOk = true;
                ResizeRenderer();
            }
            catch (Exception ex)
            {
                ShowToast("Graphics init failed: " + ex.Message);
            }
            Swap.SizeChanged += (s, a) => ResizeRenderer();
            Swap.CompositionScaleChanged += (s, a) => ResizeRenderer();

            _ed.NewScene();
            _ed.History.Clear();
            _ed.ViewSize = new Vector2((float)Math.Max(1, Swap.ActualWidth), (float)Math.Max(1, Swap.ActualHeight));
            _ed.Cursor = _ed.ViewSize * 0.5f;

            var settings = ApplicationData.Current.LocalSettings.Values;
            if (!settings.ContainsKey("helpShown"))
            {
                settings["helpShown"] = true;
                ShowHelp();
            }

            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
        }

        void ResizeRenderer()
        {
            if (!_rendererOk || Swap.ActualWidth < 1 || Swap.ActualHeight < 1) return;
            try
            {
                _renderer.Resize(Swap.ActualWidth, Swap.ActualHeight, Swap.CompositionScaleX, Swap.CompositionScaleY);
                _ed.ViewSize = new Vector2((float)Swap.ActualWidth, (float)Swap.ActualHeight);
            }
            catch (Exception ex) { ShowToast("Resize failed: " + ex.Message); }
        }

        bool AnyPanelOpen
        {
            get
            {
                return _busy || MenuPanel.Visibility == Visibility.Visible || InputPanel.Visibility == Visibility.Visible
                    || MaterialPanel.Visibility == Visibility.Visible || HelpPanel.Visibility == Visibility.Visible;
            }
        }

        void OnRendering(object sender, object e)
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, now - _lastTime);
            _lastTime = now;

            _pad.Update();
            if (AnyPanelOpen)
            {
                _inputCooldown = 0.2;
                if (MenuPanel.Visibility == Visibility.Visible && _pad.Pressed(GamepadButtons.Menu)) CloseMenus();
            }
            else if (_inputCooldown > 0)
            {
                _inputCooldown -= dt;
                _pad.Swallow();
            }
            else
            {
                if (_pad.Pressed(GamepadButtons.Menu) && !_ed.Transforming) OpenMainMenu();
                else if (_pad.Pressed(GamepadButtons.Y) && !_ed.Transforming && !_pad.Down(GamepadButtons.LeftShoulder)) OpenToolsMenu();
                else
                {
                    try { _ed.Update(_pad, dt); }
                    catch (Exception ex) { ShowToast("Error: " + ex.Message); }
                }
            }

            if (_rendererOk)
            {
                try { _renderer.Render(_ed); }
                catch (Exception ex)
                {
                    _rendererOk = false;
                    ShowToast("Render error: " + ex.Message);
                }
            }
            UpdateHud();
        }

        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            // Never let B leave the app; it closes panels/menus instead.
            e.Handled = true;
            if (_busy) return;
            if (InputPanel.Visibility == Visibility.Visible) { CloseInput(); return; }
            if (MaterialPanel.Visibility == Visibility.Visible) { CloseMaterialPanel(); return; }
            if (HelpPanel.Visibility == Visibility.Visible) { HelpPanel.Visibility = Visibility.Collapsed; return; }
            if (MenuPanel.Visibility == Visibility.Visible) { PopMenu(); return; }
        }

        // ------------------------------------------------------------------ HUD

        static void SetText(TextBlock tb, string s) { if (tb.Text != s) tb.Text = s; }

        void UpdateHud()
        {
            SetText(ModeText, _ed.ModeText);
            SetText(ToolText, _ed.ToolText);
            SetText(InfoText, _ed.InfoText);
            SetText(HintText, _pad.Connected ? _ed.HintText : "Connect an Xbox controller");
            SetText(FrameText, "Frame " + (int)Math.Round(_ed.Frame) + " / " + _ed.Scene.FrameEnd + (_ed.Playing ? "   > playing" : "")
                + "    D-pad: < > frame  ^ key  v play");

            Canvas.SetLeft(CursorMark, _ed.Cursor.X);
            Canvas.SetTop(CursorMark, _ed.Cursor.Y);
            CursorMark.Visibility = AnyPanelOpen ? Visibility.Collapsed : Visibility.Visible;

            double w = Timeline.ActualWidth;
            if (w > 0)
            {
                double x = _ed.Frame / Math.Max(1, _ed.Scene.FrameEnd) * (w - 3);
                Playhead.Margin = new Thickness(x, 0, 0, 0);
                var a = _ed.Sel.Active;
                string sig = a == null ? "" : a.GetHashCode() + ":" + _ed.Scene.FrameEnd + ":" + w + ":" + string.Join(",", a.Keys.Select(k => k.Frame));
                if (sig != _keySig)
                {
                    _keySig = sig;
                    KeyCanvas.Children.Clear();
                    if (a != null)
                        foreach (var k in a.Keys)
                        {
                            var r = new Rectangle { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromArgb(255, 255, 210, 80)), RenderTransform = new RotateTransform { Angle = 45, CenterX = 3.5, CenterY = 3.5 } };
                            Canvas.SetLeft(r, k.Frame / (double)Math.Max(1, _ed.Scene.FrameEnd) * (w - 3) - 2);
                            Canvas.SetTop(r, 6.5);
                            KeyCanvas.Children.Add(r);
                        }
                }
            }
        }

        public void ShowToast(string msg)
        {
            ToastText.Text = msg;
            ToastBox.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        void ShowBusy(string text) { _busy = true; BusyText.Text = text; BusyPanel.Visibility = Visibility.Visible; }

        void HideBusy() { _busy = false; BusyPanel.Visibility = Visibility.Collapsed; _pad.Swallow(); }

        // ------------------------------------------------------------------ menu system

        void OpenMenu(string title, Func<List<UiMenuItem>> build)
        {
            _menus.Add(new KeyValuePair<string, Func<List<UiMenuItem>>>(title, build));
            RenderMenu();
        }

        void RenderMenu()
        {
            if (_menus.Count == 0) { CloseMenus(); return; }
            var top = _menus[_menus.Count - 1];
            MenuTitle.Text = top.Key;
            MenuStack.Children.Clear();
            List<UiMenuItem> items;
            try { items = top.Value(); }
            catch (Exception ex) { items = new List<UiMenuItem> { new UiMenuItem("Error: " + ex.Message, () => { }) }; }
            Button first = null;
            foreach (var item in items)
            {
                var it = item;
                var b = new Button { Content = it.Label, Style = (Style)Resources["MenuButton"] };
                b.Click += (s, e) => InvokeItem(it);
                MenuStack.Children.Add(b);
                if (first == null) first = b;
            }
            MenuPanel.Visibility = Visibility.Visible;
            MenuScroll.ChangeView(null, 0, null, true);
            if (first != null)
            {
                var f = first;
                RoutedEventHandler h = null;
                h = (s, e) => { f.Loaded -= h; f.Focus(FocusState.Keyboard); };
                f.Loaded += h;
            }
        }

        void InvokeItem(UiMenuItem it)
        {
            if (!it.Stay) CloseMenus();
            try { it.Run(); }
            catch (Exception ex) { ShowToast("Error: " + ex.Message); }
        }

        void PopMenu()
        {
            if (_menus.Count > 0) _menus.RemoveAt(_menus.Count - 1);
            if (_menus.Count == 0) CloseMenus();
            else RenderMenu();
        }

        void CloseMenus()
        {
            _menus.Clear();
            MenuStack.Children.Clear();
            MenuPanel.Visibility = Visibility.Collapsed;
            _pad.Swallow();
        }

        static string Check(bool on) { return on ? "[x] " : "[  ] "; }

        void OpenMainMenu()
        {
            OpenMenu("Facet3D", () => new List<UiMenuItem>
            {
                new UiMenuItem("New scene", () => { _ed.NewScene(); ShowToast("New scene (Undo with LB + B)"); }),
                new UiMenuItem("Import model from USB...", StartImport, true),
                new UiMenuItem("Export model to USB...", StartExport, true),
                new UiMenuItem("Save project (.glb)...", () => ChooseExportLocation(ModelFormats.All[0], false), true),
                new UiMenuItem("Animation  >", OpenAnimationMenu, true),
                new UiMenuItem("View  >", OpenViewMenu, true),
                new UiMenuItem("Undo", _ed.Undo),
                new UiMenuItem("Redo", _ed.Redo),
                new UiMenuItem("Controller guide", ShowHelp, true),
                new UiMenuItem("About", () => ShowToast("Facet3D - controller-first 3D modelling and animation for Xbox. " + _renderer.Status)),
            });
        }

        void OpenToolsMenu()
        {
            OpenMenu("Tools", () =>
            {
                var l = new List<UiMenuItem>();
                l.Add(new UiMenuItem("Add shape  >", OpenAddMenu, true));
                if (_ed.Mode == EditMode.Object)
                {
                    l.Add(new UiMenuItem("Edit vertices", () => _ed.SetMode(EditMode.Vertex)));
                    l.Add(new UiMenuItem("Edit faces", () => _ed.SetMode(EditMode.Face)));
                    l.Add(new UiMenuItem("Duplicate", _ed.Duplicate));
                    l.Add(new UiMenuItem("Delete", _ed.DeleteSelected));
                    l.Add(new UiMenuItem("Object  >", OpenObjectMenu, true));
                }
                else
                {
                    l.Add(new UiMenuItem("Back to Object mode", () => _ed.SetMode(EditMode.Object)));
                    if (_ed.Mode == EditMode.Face)
                    {
                        l.Add(new UiMenuItem("Extrude faces", _ed.Extrude));
                        l.Add(new UiMenuItem("Separate faces to new object", _ed.Separate));
                        l.Add(new UiMenuItem("Switch to vertex select", () => _ed.SetMode(EditMode.Vertex)));
                    }
                    else
                    {
                        l.Add(new UiMenuItem("Make face from selected vertices", _ed.MakeFace));
                        l.Add(new UiMenuItem("Switch to face select", () => _ed.SetMode(EditMode.Face)));
                    }
                    l.Add(new UiMenuItem("Delete selected", _ed.DeleteSelected));
                }
                l.Add(new UiMenuItem("Mesh  >", OpenMeshMenu, true));
                l.Add(new UiMenuItem("Select  >", OpenSelectMenu, true));
                l.Add(new UiMenuItem("Material  >", OpenMaterialMenu, true));
                l.Add(new UiMenuItem("Tool: Move", () => { _ed.Tool = ToolKind.Move; }));
                l.Add(new UiMenuItem("Tool: Rotate", () => { _ed.Tool = ToolKind.Rotate; }));
                l.Add(new UiMenuItem("Tool: Scale", () => { _ed.Tool = ToolKind.Scale; }));
                l.Add(new UiMenuItem("Axis: Free / X / Y / Z  (now " + _ed.Axis + ")", () => { _ed.Axis = (AxisLock)(((int)_ed.Axis + 1) % 4); ShowToast("Axis: " + _ed.Axis); }));
                l.Add(new UiMenuItem("Undo", _ed.Undo));
                l.Add(new UiMenuItem("Redo", _ed.Redo));
                return l;
            });
        }

        void OpenAddMenu()
        {
            OpenMenu("Add shape", () => Enum.GetValues(typeof(PrimitiveKind)).Cast<PrimitiveKind>()
                .Select(k => new UiMenuItem(k.ToString(), () => _ed.AddPrimitive(k))).ToList());
        }

        void OpenMeshMenu()
        {
            OpenMenu("Mesh", () => new List<UiMenuItem>
            {
                new UiMenuItem("Subdivide (smooth)", () => _ed.Subdivide(true)),
                new UiMenuItem("Subdivide (flat)", () => _ed.Subdivide(false)),
                new UiMenuItem("Merge vertices by distance", _ed.MergeByDistance),
                new UiMenuItem("Triangulate", _ed.Triangulate),
                new UiMenuItem("Flip normals", _ed.FlipNormals),
                new UiMenuItem("Shade smooth", () => _ed.SetShading(Shading.Smooth)),
                new UiMenuItem("Shade flat", () => _ed.SetShading(Shading.Flat)),
                new UiMenuItem("Shade as imported", () => _ed.SetShading(Shading.Auto)),
            });
        }

        void OpenObjectMenu()
        {
            OpenMenu("Object", () => new List<UiMenuItem>
            {
                new UiMenuItem("Join selected objects", _ed.Join),
                new UiMenuItem("Apply transform (bake into mesh)", _ed.ApplyTransforms),
                new UiMenuItem("Origin to geometry centre", _ed.CenterOrigin),
                new UiMenuItem("Reset position / rotation / scale", _ed.ResetTransforms),
                new UiMenuItem("Hide selected (or show all)", _ed.ToggleHidden),
                new UiMenuItem("Rename...", () =>
                {
                    if (_ed.Sel.Active == null) { ShowToast("Select an object first"); return; }
                    ShowInput("Rename object", _ed.Sel.Active.Name, n => { _ed.Rename(n); CloseMenus(); });
                }, true),
            });
        }

        void OpenSelectMenu()
        {
            OpenMenu("Select", () => new List<UiMenuItem>
            {
                new UiMenuItem("Select all", _ed.SelectAll),
                new UiMenuItem("Invert selection", _ed.InvertSelection),
                new UiMenuItem("Next object", () => _ed.SelectNextObject(1)),
                new UiMenuItem("Previous object", () => _ed.SelectNextObject(-1)),
                new UiMenuItem("Frame selection (R3)", _ed.FrameSelection),
            });
        }

        void OpenMaterialMenu()
        {
            OpenMenu("Materials", () =>
            {
                var l = new List<UiMenuItem>();
                l.Add(new UiMenuItem("Edit current material...", () => ShowMaterialPanel(_ed.CurrentMaterialIndex()), true));
                l.Add(new UiMenuItem("New material (assign + edit)...", () =>
                {
                    int i = _ed.NewMaterial();
                    _ed.AssignMaterial(i);
                    ShowMaterialPanel(i);
                }, true));
                for (int i = 0; i < _ed.Scene.Materials.Count; i++)
                {
                    int idx = i;
                    var m = _ed.Scene.Materials[i];
                    l.Add(new UiMenuItem("Assign: " + m.Name, () => _ed.AssignMaterial(idx)));
                }
                return l;
            });
        }

        void OpenAnimationMenu()
        {
            OpenMenu("Animation", () => new List<UiMenuItem>
            {
                new UiMenuItem(_ed.Playing ? "Pause" : "Play", _ed.TogglePlay),
                new UiMenuItem("Insert keyframe (D-pad up)", _ed.InsertKey),
                new UiMenuItem("Delete keyframe (LB + D-pad up)", _ed.DeleteKey),
                new UiMenuItem("Clear animation of selected", _ed.ClearAnimation),
                new UiMenuItem("Go to first frame", () => _ed.SetFrame(0)),
                new UiMenuItem(Check(_ed.AutoKey) + "Auto-key moved objects that are animated", () => { _ed.AutoKey = !_ed.AutoKey; }),
                new UiMenuItem("Length: 60 frames", () => _ed.SetFrameEnd(60)),
                new UiMenuItem("Length: 120 frames", () => _ed.SetFrameEnd(120)),
                new UiMenuItem("Length: 240 frames", () => _ed.SetFrameEnd(240)),
                new UiMenuItem("Length: 480 frames", () => _ed.SetFrameEnd(480)),
                new UiMenuItem("Frame rate: 24 fps", () => { _ed.Scene.Fps = 24; }),
                new UiMenuItem("Frame rate: 30 fps", () => { _ed.Scene.Fps = 30; }),
                new UiMenuItem("Frame rate: 60 fps", () => { _ed.Scene.Fps = 60; }),
            });
        }

        void OpenViewMenu()
        {
            OpenMenu("View", () => new List<UiMenuItem>
            {
                new UiMenuItem("Frame selection (R3)", _ed.FrameSelection),
                new UiMenuItem("Front view", () => _ed.ViewPreset(0)),
                new UiMenuItem("Right view", () => _ed.ViewPreset(1)),
                new UiMenuItem("Top view", () => _ed.ViewPreset(2)),
                new UiMenuItem("Perspective view", () => _ed.ViewPreset(3)),
                new UiMenuItem(Check(_ed.ShowGrid) + "Show grid", () => { _ed.ShowGrid = !_ed.ShowGrid; }),
                new UiMenuItem(Check(_ed.InvertCameraY) + "Invert camera up/down", () => { _ed.InvertCameraY = !_ed.InvertCameraY; }),
                new UiMenuItem("Cursor speed: slow", () => { _ed.CursorSpeed = 0.6f; }),
                new UiMenuItem("Cursor speed: normal", () => { _ed.CursorSpeed = 1f; }),
                new UiMenuItem("Cursor speed: fast", () => { _ed.CursorSpeed = 1.6f; }),
            });
        }

        // ------------------------------------------------------------------ import / export flows

        async void StartImport()
        {
            var locs = await UsbStorage.GetLocationsAsync();
            OpenMenu("Import from", () =>
            {
                var l = locs.Select(loc => new UiMenuItem(loc.Name, () => BrowseForImport(loc.Folder, loc.Name), true)).ToList();
                if (locs.Count == 1) l.Insert(0, new UiMenuItem("(No USB drive found - plug one in and reopen)", () => { }, true));
                return l;
            });
        }

        async void BrowseForImport(StorageFolder folder, string title)
        {
            FolderListing listing;
            try { listing = await UsbStorage.ListAsync(folder, ModelFormats.ImportExtensions); }
            catch (Exception ex) { ShowToast("Cannot read folder: " + ex.Message); return; }
            OpenMenu(title, () =>
            {
                var l = new List<UiMenuItem>();
                foreach (var f in listing.Folders) { var ff = f; l.Add(new UiMenuItem("[folder] " + ff.Name, () => BrowseForImport(ff, ff.Name), true)); }
                foreach (var f in listing.Files) { var ff = f; l.Add(new UiMenuItem(ff.Name, () => ImportFile(ff, folder))); }
                if (l.Count == 0) l.Add(new UiMenuItem("(No supported models here: " + string.Join(" ", ModelFormats.ImportExtensions) + ")", () => { }, true));
                return l;
            });
        }

        async void ImportFile(StorageFile file, StorageFolder folder)
        {
            ShowBusy("Importing " + file.Name + "...");
            try
            {
                var data = await UsbStorage.ReadAllAsync(file);
                string name = file.Name;
                var scene = await Task.Run(() => ModelFormats.Import(data, name, rel => UsbStorage.OpenSibling(folder, rel)));
                _ed.ImportScene(scene, name);
            }
            catch (Exception ex)
            {
                ShowToast("Import failed: " + ex.Message);
            }
            finally { HideBusy(); }
        }

        void StartExport()
        {
            OpenMenu("Export format", () => ModelFormats.All.Where(f => f.CanExport)
                .Select(f => new UiMenuItem(f.Name, () => ChooseScope(f), true)).ToList());
        }

        void ChooseScope(IModelFormat fmt)
        {
            if (_ed.Mode != EditMode.Object || _ed.Sel.Objects.Count == 0 || _ed.Sel.Objects.Count == _ed.Scene.Objects.Count)
            {
                ChooseExportLocation(fmt, false);
                return;
            }
            OpenMenu("What to export", () => new List<UiMenuItem>
            {
                new UiMenuItem("Whole scene", () => ChooseExportLocation(fmt, false), true),
                new UiMenuItem("Selected objects only (" + _ed.Sel.Objects.Count + ")", () => ChooseExportLocation(fmt, true), true),
            });
        }

        async void ChooseExportLocation(IModelFormat fmt, bool selectedOnly)
        {
            var locs = await UsbStorage.GetLocationsAsync();
            OpenMenu("Save to", () => locs.Select(loc => new UiMenuItem(loc.Name, () => BrowseForExport(loc.Folder, loc.Name, fmt, selectedOnly), true)).ToList());
        }

        async void BrowseForExport(StorageFolder folder, string title, IModelFormat fmt, bool selectedOnly)
        {
            FolderListing listing;
            try { listing = await UsbStorage.ListAsync(folder, fmt.Extensions); }
            catch (Exception ex) { ShowToast("Cannot read folder: " + ex.Message); return; }
            OpenMenu(title, () =>
            {
                var l = new List<UiMenuItem>();
                l.Add(new UiMenuItem("> Save here", () => AskExportName(folder, fmt, selectedOnly), true));
                l.Add(new UiMenuItem("> New folder here...", () => ShowInput("New folder name", "Facet3D", async n =>
                {
                    try
                    {
                        var nf = await folder.CreateFolderAsync(n, CreationCollisionOption.OpenIfExists);
                        BrowseForExport(nf, nf.Name, fmt, selectedOnly);
                    }
                    catch (Exception ex) { ShowToast("Cannot create folder: " + ex.Message); }
                }), true));
                foreach (var f in listing.Folders) { var ff = f; l.Add(new UiMenuItem("[folder] " + ff.Name, () => BrowseForExport(ff, ff.Name, fmt, selectedOnly), true)); }
                foreach (var f in listing.Files) l.Add(new UiMenuItem("(existing) " + f.Name, () => AskExportName(folder, fmt, selectedOnly, Path.GetFileNameWithoutExtension(f.Name)), true));
                return l;
            });
        }

        void AskExportName(StorageFolder folder, IModelFormat fmt, bool selectedOnly, string suggested = null)
        {
            string def = suggested ?? (_ed.Sel.Active != null && selectedOnly ? _ed.Sel.Active.Name : "model");
            ShowInput("File name (" + fmt.Extensions[0] + ")", def, n => ExportTo(folder, fmt, selectedOnly, n));
        }

        async void ExportTo(StorageFolder folder, IModelFormat fmt, bool selectedOnly, string baseName)
        {
            baseName = FmtUtil.SafeName(Path.GetFileNameWithoutExtension(baseName.Trim()), "model");
            var scene = _ed.SceneForExport(selectedOnly);
            CloseMenus();
            ShowBusy("Exporting " + baseName + fmt.Extensions[0] + "...");
            try
            {
                string written = await Task.Run(() => fmt.Export(scene, baseName, n => UsbStorage.CreateFile(folder, n)));
                ShowToast("Saved " + written + " to " + folder.DisplayName);
            }
            catch (Exception ex)
            {
                ShowToast("Export failed: " + ex.Message);
            }
            finally { HideBusy(); }
        }

        // ------------------------------------------------------------------ text input

        void ShowInput(string title, string value, Action<string> ok)
        {
            InputTitle.Text = title;
            InputBox.Text = value ?? "";
            _inputOk = ok;
            MenuPanel.Visibility = Visibility.Collapsed;
            InputPanel.Visibility = Visibility.Visible;
            RoutedEventHandler h = null;
            h = (s, e) => { InputBox.Loaded -= h; InputBox.Focus(FocusState.Keyboard); };
            InputBox.Loaded += h;
            InputBox.Focus(FocusState.Keyboard);
            InputBox.SelectAll();
        }

        void CloseInput()
        {
            InputPanel.Visibility = Visibility.Collapsed;
            _inputOk = null;
            if (_menus.Count > 0) RenderMenu();
            _pad.Swallow();
        }

        void InputOk_Click(object sender, RoutedEventArgs e)
        {
            var ok = _inputOk;
            string text = InputBox.Text;
            InputPanel.Visibility = Visibility.Collapsed;
            _inputOk = null;
            _pad.Swallow();
            if (string.IsNullOrWhiteSpace(text)) { ShowToast("Name cannot be empty"); if (_menus.Count > 0) RenderMenu(); return; }
            if (ok != null) ok(text);
        }

        void InputCancel_Click(object sender, RoutedEventArgs e) { CloseInput(); }

        // ------------------------------------------------------------------ material editor

        void ShowMaterialPanel(int index)
        {
            CloseMenus();
            _ed.History.Push(_ed.Scene);
            _editingMat = _ed.Scene.MaterialAt(index);
            _matLoading = true;
            MatTitle.Text = _editingMat.Name;
            SliderR.Value = _editingMat.Color.X * 255;
            SliderG.Value = _editingMat.Color.Y * 255;
            SliderB.Value = _editingMat.Color.Z * 255;
            SliderA.Value = _editingMat.Color.W * 100;
            SliderMetal.Value = _editingMat.Metallic * 100;
            SliderRough.Value = _editingMat.Roughness * 100;
            _matLoading = false;
            UpdateSwatch();
            MaterialPanel.Visibility = Visibility.Visible;
            RoutedEventHandler h = null;
            h = (s, e) => { SliderR.Loaded -= h; SliderR.Focus(FocusState.Keyboard); };
            SliderR.Loaded += h;
            SliderR.Focus(FocusState.Keyboard);
        }

        void MatSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_matLoading || _editingMat == null) return;
            _editingMat.Color = new Vector4((float)(SliderR.Value / 255), (float)(SliderG.Value / 255), (float)(SliderB.Value / 255), (float)Math.Max(0.01, SliderA.Value / 100));
            _editingMat.Metallic = (float)(SliderMetal.Value / 100);
            _editingMat.Roughness = (float)(SliderRough.Value / 100);
            _ed.Scene.MaterialsChanged();
            UpdateSwatch();
        }

        void UpdateSwatch()
        {
            if (_editingMat == null) return;
            var c = _editingMat.Color;
            MatSwatch.Fill = new SolidColorBrush(Color.FromArgb(255, (byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)));
        }

        void MatAssign_Click(object sender, RoutedEventArgs e)
        {
            if (_editingMat == null) return;
            int idx = _ed.Scene.Materials.IndexOf(_editingMat);
            if (idx >= 0) _ed.AssignMaterial(idx);
        }

        void MatRename_Click(object sender, RoutedEventArgs e)
        {
            var mat = _editingMat;
            if (mat == null) return;
            MaterialPanel.Visibility = Visibility.Collapsed;
            ShowInput("Rename material", mat.Name, n =>
            {
                mat.Name = n.Trim();
                ShowMaterialPanelAgain();
            });
        }

        void ShowMaterialPanelAgain()
        {
            if (_editingMat == null) return;
            MatTitle.Text = _editingMat.Name;
            MaterialPanel.Visibility = Visibility.Visible;
            MatDone.Focus(FocusState.Keyboard);
        }

        void MatDone_Click(object sender, RoutedEventArgs e) { CloseMaterialPanel(); }

        void CloseMaterialPanel()
        {
            MaterialPanel.Visibility = Visibility.Collapsed;
            _editingMat = null;
            _pad.Swallow();
        }

        // ------------------------------------------------------------------ help

        void ShowHelp()
        {
            CloseMenus();
            HelpPanel.Visibility = Visibility.Visible;
            RoutedEventHandler h = null;
            h = (s, e) => { HelpClose.Loaded -= h; HelpClose.Focus(FocusState.Keyboard); };
            HelpClose.Loaded += h;
            HelpClose.Focus(FocusState.Keyboard);
        }

        void HelpClose_Click(object sender, RoutedEventArgs e)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            _pad.Swallow();
        }

        const string HelpContent =
            "CAMERA\n" +
            "  Right stick - orbit        LB + right stick - pan\n" +
            "  RT / LT - zoom in / out    R3 (click right stick) - frame selection\n\n" +
            "SELECT\n" +
            "  Left stick - move the cursor (hold LB for precision)    L3 - centre cursor\n" +
            "  A - select under cursor    LB + A - add / remove from selection    B - deselect\n" +
            "  View button - cycle Object / Vertex / Face mode\n\n" +
            "TRANSFORM\n" +
            "  RB - choose tool (Move / Rotate / Scale)\n" +
            "  X - start transforming the selection, then use the left stick\n" +
            "     Y - lock axis (Free / X / Y / Z)    LB - precise    D-pad - nudge\n" +
            "     A - confirm    B - cancel\n\n" +
            "ANIMATION (timeline at the bottom)\n" +
            "  D-pad left / right - previous / next frame (LB: jump 10)\n" +
            "  D-pad up - insert keyframe for selected objects (LB: delete key)\n" +
            "  D-pad down - play / pause\n\n" +
            "MENUS\n" +
            "  Y - Tools (add shapes, extrude, subdivide, materials...)\n" +
            "  Menu - File (new, import / export via USB, animation, view)\n" +
            "  LB + B - undo    LB + X - redo\n\n" +
            "USB IMPORT / EXPORT\n" +
            "  Supported: glTF / GLB, OBJ (+MTL), FBX, STL, PLY, DAE (Collada), 3DS, OFF.\n" +
            "  Put models on a USB drive (FAT32/exFAT/NTFS), plug it into the Xbox, then Menu > Import.\n" +
            "  Keep .mtl and .bin files next to their .obj / .gltf.  Save projects as .glb to keep animation.";
    }
}
