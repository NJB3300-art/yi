# Facet3D for Xbox

Facet3D is a 3D modelling and animation app for Xbox, inspired by Prisma3D. It is a UWP app written in C# with XAML and Direct3D 11 (through SharpDX), and you use it with an Xbox controller. Models go in and out through a USB drive.

## Features

- **Modelling**
  - Add a cube, plane, sphere, cylinder, cone or torus.
  - Move, rotate and scale objects, vertices or faces, either freely or locked to the X, Y or Z axis.
  - Extrude, separate faces, make a face from selected vertices, delete, duplicate and join.
  - Subdivide (smooth Catmull-Clark or flat), merge vertices by distance, triangulate and flip normals.
  - Choose smooth or flat shading, apply transforms, centre the origin, hide and show objects, rename objects.
- **Materials**: create and assign materials (per object or per face) and edit colour, opacity, metallic and roughness.
- **Animation**
  - Set keyframes for position, rotation and scale on a timeline, with optional auto-key.
  - Play back the animation, and choose its length and frame rate (fps).
  - Animation is saved in glTF/GLB files and loaded back from them.
- **Undo / redo** for every edit.
- **USB import and export**:

| Format | Import | Export |
|---|---|---|
| glTF 2.0 `.gltf` / `.glb` (incl. animation) | ✔ | ✔ |
| Wavefront `.obj` + `.mtl` | ✔ | ✔ |
| Autodesk `.fbx` (binary 7.x and ASCII) | ✔ | ✔ (binary 7.4) |
| STL `.stl` (ASCII / binary) | ✔ | ✔ (binary) |
| Stanford `.ply` (ASCII / binary) | ✔ | ✔ (binary, vertex colours) |
| COLLADA `.dae` | ✔ | ✔ |
| 3D Studio `.3ds` | ✔ | ✔ |
| OFF `.off` | ✔ | ✔ |

Textures are stored by file name in the scene and written back out on export. The viewport does not display them yet.

## Controller

| Input | Action |
|---|---|
| Right stick | Orbit the camera (LB + right stick pans it) |
| RT / LT | Zoom in / out |
| R3 | Frame the selection |
| Left stick | Move the cursor (L3 centres it) |
| A | Select what is under the cursor (LB + A adds to or removes from the selection) |
| B | Deselect (LB + B undoes) |
| X | Start moving, rotating or scaling with the left stick. While transforming: Y locks an axis, LB gives fine control, A confirms, B cancels. LB + X redoes. |
| RB | Switch between the Move, Rotate and Scale tools |
| View | Switch between Object, Vertex and Face mode |
| Y | Tools menu (add shapes, extrude, subdivide, materials…) |
| Menu | File menu (new scene, USB import/export, save the project, animation and view settings, help) |
| D-pad | Timeline: ◀ ▶ step one frame (LB: 10 frames), ▲ set a keyframe (LB: delete it), ▼ play / pause |

## Getting a build

GitHub Actions (`.github/workflows/build.yml`) builds the app on every push. Each run uploads the **Facet3D-Xbox-x64** artifact, which contains:

- the `.msix` app package
- the dependency packages (.NET Native framework and runtime, and VCLibs)
- the signing certificate

Pushing a tag such as `v1.0.0` also publishes a GitHub Release with the same files.

## Installing on an Xbox (Dev Mode)

1. Put your Xbox in Developer Mode and open Xbox Device Portal from a PC browser.
2. Go to **Add** and choose the `Facet3D_…_x64.msix` file.
3. When asked for dependencies, add the `Microsoft.NET.Native.Framework.2.2`, `Microsoft.NET.Native.Runtime.2.2` and `Microsoft.VCLibs.x64.14.00` `.appx` files from the same download.
4. Start **Facet3D** from Dev Home.

## Using a USB drive

- Format the drive as exFAT, FAT32 or NTFS, then copy your models onto it.
- Keep `.mtl` files and textures next to their `.obj`, and `.bin` files next to their `.gltf`.
- On the Xbox, choose **Menu → Import model from USB**. To save, choose **Export model to USB** or **Save project (.glb)**. GLB keeps materials and animation.
- Without a USB drive you can use **App storage (internal)**, which you can reach from Device Portal's file explorer.

## Building locally

Open `Facet3D.sln` in Visual Studio 2022 with the *Universal Windows Platform development* workload installed, choose **Release | x64**, and deploy to your console as a *Remote Machine*. If you don't have the precompiled `.cso` shaders, the app compiles `Shaders/Scene.hlsl` when it starts.

## Project layout

```
Facet3D/
  Core/        Scene, Mesh, Material, keyframes, primitives, mesh operations, picking, camera, undo
  Formats/     importers and exporters (glTF/GLB, OBJ, FBX, STL, PLY, DAE, 3DS, OFF) + JSON
  Rendering/   Direct3D 11 viewport renderer (SharpDX)
  Editor/      controller input and the editor/interaction logic
  Storage/     USB / app-storage browsing
  Shaders/     HLSL
  MainPage.*   HUD, menus, file browser, material editor, help
```
