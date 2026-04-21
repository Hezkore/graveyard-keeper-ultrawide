# Graveyard Keeper Resolution Ultrawide Patcher

A patcher for Graveyard Keeper.\
Removes the built in 2560x1440 cap so the game uses the monitor's real native resolution.

Drop the exe into the game folder and run it once.\
The game launches borderless at the native resolution with a hardware cursor.

![Game running at 5120x1440](demo.png)

## Why

Out of the box the game rejects anything wider than 2560 or taller than 1440. On a 5120x1440 monitor that leaves half the screen unused or the game letterboxed.\
Unity also filters its own resolution list and sometimes drops the native mode, so even after the cap is raised the Options dropdown can still be wrong.\
The fog uses a fixed 6 tile wide grid which stops well short of the right edge on ultrawide aspect ratios.

The patcher fixes all of that in one pass so the game behaves the same on any monitor.

## How

The patcher rewrites a handful of methods inside `Assembly-CSharp.dll` using [Mono.Cecil](https://github.com/jbevain/cecil), byte edits one flag inside the `level2` scene file, and clears any saved Unity prefs so the new defaults actually show up.

On the first run it makes `Assembly-CSharp.dll.bak-original` and `level2.bak-original`. Every run after that reads from the backups, so patching twice is safe.

### Resolution cap

Inside `ResolutionConfig.GetResolutionConfigOrNull` the hardcoded 2560 and 1440 become 16384 and 8192. `IsHardwareSupported` is replaced with a method that returns true, so Unity hidden modes go through.

### Native resolution fallback

`GameSettings.ApplyScreenMode` hardcodes a 1920x1080 fallback when no resolution is saved. The patch swaps both constants for `Screen.currentResolution.width` and `.height` at runtime.

### Resolution list

At the end of `ResolutionConfig.InitResolutions` the patch injects a loop that adds `Screen.currentResolution` to `_available_resolutions` when it isn't already in the list. That puts the native mode in the Options dropdown.

### Fog

`FogObject.InitFog`, `Update`, and the constructor size the fog grid with the numbers 6 and 36. The patch replaces them with `Max(6, ceil(Screen.width / 576) + 2)` so the grid grows with the window.

### Cursor and borderless

Both `GameSettings..ctor` and `PlatformSpecific.LoadGameSettings` write `cursor_mode = 2` (Software). The patch swaps those for `1` (Hardware). Borderless is already the zero default so nothing else needs to change.

### Main menu debug label

The `level2` file contains a `UILabel` named `small_font` that renders debug text visible at ultrawide aspect ratios. The patch searches for a 24 byte pattern that identifies that label and flips one byte of `m_Enabled` from `01` to `00`.

## Install

The patcher runs on Windows, Linux, and macOS across Steam and GOG builds. Linux and macOS need [Mono](https://www.mono-project.com/) installed.

<details open>
<summary>Automatic</summary>
<br>

Drop `patch.exe` inside the Graveyard Keeper install folder and run it.

On Windows, double click `patch.exe` or run it from a terminal.

On Linux, run it with Mono.

```sh
mono patch.exe
```

On macOS, same as Linux once Mono is installed with `brew install mono`.

```sh
mono patch.exe
```

</details>

<details>
<summary>Manual</summary>
<br>

Keep `patch.exe` anywhere and pass the install path as the first argument:

```sh
mono patch.exe "~/.local/share/Steam/steamapps/common/Graveyard Keeper"
```

On Windows:

```sh
patch.exe "C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper"
```

On macOS point at the folder holding the `.app`:

```sh
mono patch.exe "/Applications/Graveyard Keeper.app"
```

To undo, copy the `.bak-original` files back over the patched ones, or verify integrity of game files in Steam.

</details>

## Building

`Mono.Cecil.dll` is embedded into the exe as a resource so the output stays self contained.

```sh
mcs -langversion:7 \
    -r:/usr/lib/mono/gac/Mono.Cecil/0.11.1.0__0738eb9f132ed756/Mono.Cecil.dll \
    -resource:/usr/lib/mono/gac/Mono.Cecil/0.11.1.0__0738eb9f132ed756/Mono.Cecil.dll,Mono.Cecil.dll \
    patch.cs
```
