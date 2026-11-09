# BrightTool

Minimalist brightness and contrast control for monitors, built for the
LG 22MK400H — works with any monitor that supports **DDC/CI** (most LG, Dell,
Samsung, BenQ, ASUS displays do).

Two tiny tools, zero dependencies — the executables are compiled with the C#
compiler that already ships with Windows (.NET Framework 4.8):

| Program | What it is |
|---|---|
| `bright.exe` | Command line tool |
| `BrightTray.exe` | Tray icon with a Windows 11-styled slider popup |

## Features

- **Tray popup** — left-click the sun icon: slider for brightness/contrast
  (toggle between them), keyboard arrows, PageUp/PageDown and mouse wheel
  all work. Esc or clicking away closes it.
- **Run at login** — right-click the tray icon and tick "Run at login"
  (stored as a standard `HKCU\...\Run` registry entry; untick to remove).
- **CLI**

      bright               show current brightness
      bright 70            set brightness to 70%
      bright +10 / -20     nudge brightness
      bright c             show current contrast
      bright c 80          set contrast to 80%
      bright contrast get  full setting names also work

  `BrightTray.exe` accepts the same arguments silently
  (e.g. `BrightTray.exe c 80`) — useful for scripts and hotkeys.

## Install and run

1. Get the source (clone this repo, or download and extract the ZIP).
2. Double-click **`build.cmd`** — it compiles `bright.exe` and
   `BrightTray.exe` into the repo folder using the built-in Windows C#
   compiler. No Visual Studio or SDK required.
3. Run **`BrightTray.exe`** (the tray icon appears near the clock) or use
   the CLI from a terminal:
   `.\bright.exe 80`
4. For autostart: right-click the tray icon → **Run at login**.

If it stops working, check the monitor's OSD: the **DDC/CI** option must be
"On" (LG menu: Menu > General/Other > DDC/CI).

## Build from source

    build.cmd

Rebuilds both executables from `Program.cs`. Deleting an exe while the tray
app is running will fail the build — exit the tray first (right-click → Exit).

## Notes

- Brightness/contrast travel over DDC/CI (VCP codes 0x10 / 0x12) through the
  Windows low-level monitor API (`dxva2.dll`).
- All monitors are adjusted together; per-monitor selection is intentionally
  out of scope for a minimalist tool.
- Windows 11 look (rounded corners, dark flyout, accent slider) with a plain
  GDI+ implementation — no frameworks, no extra runtime memory.
