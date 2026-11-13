# SteamStreamer (UWP, C# + XAML)

A library-style front end for streaming your own Steam games from your PC to an Xbox (Dev Mode) using the open-source Sunshine host and Moonlight client core.

## What's here

- `Models/GameEntry.cs` : game tile model
- `Services/SunshineClient.cs` : talks to your Sunshine host (server info, app list, launch/quit) over the Moonlight HTTP(S) protocol
- `MainPage.xaml(.cs)` : controller-friendly grid of your apps with a one-tap launch
- `Package.appxmanifest` : capabilities needed on Xbox (internet client, private networks)

## What's NOT here (needs a Windows machine)

The actual video/audio/input streaming is not rewritten here. It comes from the GPL project **Moonlight** (Moonlight UWP port + `moonlight-common-c`). This repo is meant to be merged into a fork of it:

1. On your Windows PC install Visual Studio with the "Universal Windows Platform" workload.
2. Fork and clone a Moonlight UWP/Xbox client from GitHub (search "moonlight uwp xbox"; the GPL applies to your fork).
3. Copy `Models/`, `Services/` and `MainPage.*` into the fork, replacing its start page, and wire `LaunchAsync` to the fork's stream-session start (see TODO in `MainPage.xaml.cs`).
4. Build for x64, deploy to your console in Dev Mode (Xbox Device Portal).

## Host setup (your PC)

1. Install Sunshine on the PC that owns your Steam library, set a username/password in its web UI (https://localhost:47990).
2. In Sunshine's Applications, add "Steam Big Picture" (command `steam://open/bigpicture`) or individual games.
3. On the Xbox app, add the PC by IP, enter the pairing PIN into Sunshine's web UI.

## Notes

- Streaming uses your PC's license/copy of the game, which stays installed and running on the PC.
- Pairing (client cert exchange) is handled by Moonlight's existing code; `SunshineClient` expects an already-paired HTTPS client certificate (see `ConfigureClientCertificate`).
