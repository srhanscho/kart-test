# Kart Party

A couch kart racer for 1–4 players, built in Unity 6, where **each player uses their phone as the controller**. No app to install: phones scan a QR code and get a touch gamepad in the browser. Empty grid slots are filled with CPU racers.

![Race start at night](docs/screenshots/race_chasecam_toon_bloom.png)

## Features

| Area | What's in it |
|------|--------------|
| **Phone controllers** | Scan the QR code on the PC screen and a landscape touch gamepad opens in the phone's browser. Uses a small WebSocket server in the game over local Wi-Fi. Up to 4 players, with haptic feedback. |
| **Split screen** | 1–4 players (full screen, top/bottom, or quadrants), each with their own camera and HUD. |
| **CPU racers** | Fill the grid to 6 karts. They follow the racing line, avoid obstacles, use items, and have mild catch-up. |
| **12 characters** | Each driver sits in their own kart and has slightly different speed, acceleration and handling. |
| **Items** | Item boxes with a roulette: banana peel, turbo, homing rocket and shield. You get better items the further back you are. |
| **Driving** | Arcade handling, drifting with a mini-turbo, hop, crash spin-outs, and a rescue helper that puts you back on the track. |
| **Track** | An 809 m circuit with hairpins, S-curves, bumps, a hill, obstacles, and a night-time stadium setting. |
| **Look & sound** | Toon shading with outlines, bloom, procedural engine sounds and chiptune music (the music speeds up on the final lap). |

## Screenshots

| Lobby | 2-player split screen |
|-------|------------------------|
| ![Lobby](docs/screenshots/ui_lobby.png) | ![2-player split screen](docs/screenshots/ui_race_2p.png) |

| 4-player split screen | Results podium |
|------------------------|----------------|
| ![4-player split screen](docs/screenshots/ui_race_4p.png) | ![Results podium](docs/screenshots/ui_results_podium.png) |

## Quick start

**Requirements:** Windows 10/11, [Unity 6000.6.3f1](https://unity.com/releases/editor/archive), and phones on the **same Wi-Fi network** as the PC.

1. Clone the repo and open the folder in Unity Hub (**Add → Add project from disk**). The first import takes a few minutes.
2. Open `Assets/Scenes/Race.unity` and press **Play**.
3. If the Windows Firewall asks about the Unity Editor, allow **Private networks**.
4. On each phone, scan the QR code (or type the URL shown on screen), pick a character, and tap **READY**.
5. The race starts when everyone is ready.

> No phone? Press **Enter** to join as a keyboard player.

## Controls

### Phone (hold it sideways)

| Left thumb | Right thumb |
|------------|-------------|
| ◀ ▶ steer | **GAS**, **BRAKE**, **HOP/DRIFT** (tap to hop, hold while steering to drift), **ITEM** |

In the lobby, use ◀ ▶ to pick a character, then tap **READY**. Player 1's phone also has a **START** button to force-start the race.

### Keyboard

| Action | Keys |
|--------|------|
| Accelerate / brake / reverse | `W` `S` or `↑` `↓` |
| Steer | `A` `D` or `←` `→` |
| Hop (tap) / drift (hold while steering) | `Space` |
| Use item | `E` or `Left Shift` |
| Lobby: join / ready | `Enter` |
| Lobby: pick a character | `←` `→` |
| Lobby: leave | `Backspace` |
| Lobby: force start | `Space` |
| Back to lobby | `Esc` |
| Toggle music | `M` |
| Visual style (plain / toon / toon + bloom) | `F2` |

**Tip:** hold a drift for about a second, then release it for a mini-turbo. The sparks turn orange when it's charged.

## Building a playable .exe

In Unity: **Tools → Kart → Build Windows Player**. The build goes to `Builds/Windows/KartParty.exe`.

From the command line:

```bash
"C:\Program Files\Unity\Hub\Editor\6000.6.3f1\Editor\Unity.exe" -batchmode -nographics -projectPath . -executeMethod BuildScript.BuildWindows -logFile build.log
```

To share the game, copy the **whole** `Builds/Windows` folder, not just the `.exe`. Press `Alt+F4` to quit the game.

## How it works

```
Phone browser ──WebSocket (LAN)──▶ PhoneControllerServer ──▶ NetworkKartInput ─┐
Keyboard ─────────────────────────────────────────────────▶ KeyboardKartInput ─┼─▶ KartController
CPU logic ────────────────────────────────────────────────▶ AiKartInput ───────┘
```

- **Input is abstracted.** `KartController` only reads an `IKartInput`, so phone, keyboard and CPU karts all drive the same way.
- **The phone server is built in.** `PhoneControllerServer` uses `TcpListener` on port 8080 (it falls back to 8081–8089 if 8080 is busy). It serves the controller page from `ControllerPage.cs` and handles the WebSocket handshake itself. No packages are needed, and Windows doesn't require admin rights.
- **The QR code is generated in code** (`QrCode.cs`), for the address picked by `LanAddress.cs`.
- **The scene is generated.** `Tools → Kart → Build Race Scene` (`RaceSceneBuilder.Build`) rebuilds the whole race scene from code: the track and its collider, waypoints, checkpoints, item boxes, lights, cameras, UI and audio. **Hand edits to `Race.unity` are overwritten** the next time it runs, so make changes in the builder.
- **The track collider is one combined mesh**, with the faces at the joints between pieces removed. Separate colliders per piece used to stop karts dead at every joint.

### Project layout

```
Assets/
  Scripts/      Gameplay, networking, UI, audio (runtime)
  Editor/       Scene builder, build script, automated tests
  Shaders/      Toon, outline and bloom shaders (built-in render pipeline)
  Scenes/       Race.unity (generated)
  Generated/    Materials, character roster and meshes created by the builder
  ThirdParty/   Kenney assets (CC0)
docs/screenshots/
```

## Tests

These run in batchmode, from the Unity menu or with `-executeMethod`:

| Method | What it checks |
|--------|----------------|
| `RaceSceneBuilder.Build` | Builds the scene and runs self-checks: the track loop closes, grid slots and item boxes sit on the road, and the phone server and QR code work. |
| `RaceSmokeTest.Run` | Plays a full simulated race. A fake phone joins over a real WebSocket, then the test checks items, spin-outs, rescues, audio, UI, split-screen viewports and results, and saves screenshots to `Logs/SmokeShots`. |
| `RaceStallTest.Run` | Drives 2 laps at full throttle and fails if the kart stops suddenly or can't climb the hill. |

```bash
"C:\Program Files\Unity\Hub\Editor\6000.6.3f1\Editor\Unity.exe" -batchmode -projectPath . -executeMethod RaceSmokeTest.Run -logFile smoke.log
```

Close the Unity Editor before running these: Unity can't open the same project twice.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| The phone never loads the page | Make sure the phone is on the same Wi-Fi and not on mobile data. In Windows, set the Wi-Fi to a **Private** network and allow Unity / `KartParty.exe` through the firewall on Private networks. |
| Still not loading | Check for a firewall **Block** rule for Unity: run `wf.msc`, open **Inbound Rules**, and change any Unity rule with a red icon to **Allow**. Marking a blocked app as allowed in the simple firewall settings screen doesn't remove the block. |
| Works at home but not on campus or office Wi-Fi | Many public networks isolate devices from each other. Use a home network or a phone hotspot. |
| The QR code shows the wrong IP | Other detected addresses are listed under the QR code. Try those, or turn off virtual network adapters. |
| The game looks blurry in the editor | Set the Game view **Scale** to 1x. |

## Known limitations

- The phone connection is plain `ws://` without authentication. It's meant for trusted home networks only.
- Web builds (WebGL) can't host the phone server. Playing from a browser would need an online relay server.
- CPU racers don't hop over bananas.
- There's one track and no loop: the kit's loop piece only comes in the narrow version.

## Credits

- 3D models, sounds, fonts and UI art by [Kenney](https://kenney.nl) (CC0): Racing Kit, Toy Car Kit, Mini Characters, Impact Sounds, Interface Sounds, Digital Audio, Music Jingles, Kenney Fonts, UI Pack.
- Engine sounds, music, textures and shaders are generated in code.

Inspired by classic couch kart racers. This is a fan-made prototype, not affiliated with or endorsed by Nintendo.
