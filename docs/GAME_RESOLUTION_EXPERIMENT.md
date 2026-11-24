# Game resolution experiment

This branch adds an opt-in experiment for Unity windows. It is intentionally separate from the normal window-management path and does not inject into the game, patch game files, or write game memory.

Open a Unity window's row context menu and choose **Game resolution experiment**.

## A · Windows display output mode

The first section enumerates modes supported by the monitor containing the selected game window. Applying a mode:

1. Reads the current mode with `EnumDisplaySettingsExW`.
2. Writes a durable rollback journal before changing anything.
3. Validates the requested mode with `CDS_TEST`.
4. Applies it with `ChangeDisplaySettingsExW` and `CDS_FULLSCREEN`.
5. Reads the active mode back to verify the change.

The original mode is restored when the game exits, when its window leaves fullscreen for a stable interval, when WinWindow actually exits, or on the next WinWindow launch if a prior session left a journal behind. If another actor has already changed the monitor away from the mode WinWindow applied, rollback leaves that newer mode untouched.

This changes the **Windows display output mode**. It does not prove that the game's internal render target or backbuffer has the same dimensions. A Unity player may still render at another size and scale its output.

## B · Unity borderless startup arguments

The second section builds this Unity player argument set for the selected size:

```text
-screen-fullscreen 1 -window-mode borderless -screen-width <width> -screen-height <height>
```

Unity documents `-screen-width`, `-screen-height`, `-screen-fullscreen`, and the Windows `-window-mode borderless|exclusive` option as standalone-player startup arguments. Fullscreen Window / Borderless can render at a script-selected resolution and scale to the native display. This experiment uses Borderless so the Windows desktop mode should remain unchanged.

These arguments only take effect when the player starts. They cannot be injected into an already-running Unity player through this feature. The dialog copies the arguments for a launcher. WinWindow deliberately does not launch the game executable itself: WinWindow normally runs elevated, and directly starting the game from that process could unintentionally change the game's privilege level. Paste the copied arguments into the launcher you normally use. The game may ignore or override Unity defaults; acceptance by Genshin is not assumed until verified on the user's machine.

## Manual verification matrix

| Test | Expected result |
| --- | --- |
| A: apply a supported smaller mode while the game is fullscreen | Windows display settings report the selected output mode; the game window continues to cover the active display |
| A: Alt+Enter back to a normal window | The original desktop mode is restored after the transition settles |
| A: close the game while fullscreen | The original desktop mode is restored |
| A: close WinWindow to tray | The lease remains active; tray residency is not treated as application exit |
| A: actually exit WinWindow | The original desktop mode is restored |
| A: terminate WinWindow unexpectedly, then launch it again | Startup recovery attempts to restore the journaled original mode |
| A: manually change the display mode after applying | WinWindow does not overwrite the newer mode during rollback |
| B: launch with a smaller Borderless size | Desktop output mode stays at its original resolution; verify the game's rendered content separately |
| B: use Copy arguments with the normal launcher | The launcher passes the requested size to the Unity player, if the game accepts the arguments |

For B, a changed window rectangle alone is not sufficient evidence of a changed render target. Compare the game's own resolution readout or use a graphics capture / render diagnostic that exposes the actual backbuffer dimensions.

## References

- [Unity Standalone Player command-line arguments](https://docs.unity.cn/6000.0/Documentation/Manual/PlayerCommandLineArguments.html)
- [Unity Windows Player settings](https://docs.unity.cn/Documentation/Manual/playersettings-windows.html)
- [Microsoft: ChangeDisplaySettingsExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw)
- [Microsoft: EnumDisplaySettingsExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsexw)
- [Snap Hutao: Advanced launcher arguments](https://snap.hutaorp.org/zh/features/game-launcher)
