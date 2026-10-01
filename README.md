# MySimCaddie

A full-screen home screen for **JuiceBoxGuy's Hack Shack**, the golf simulator room. One tap starts a whole session: the right apps open in the right order, each goes to the right display, and everything is cleaned up when you finish.

| Display | Role |
|---|---|
| **Projector** (BenQ AH500ST) | Main Windows display. GSPro opens full-screen here |
| **TV** (50") | MySimCaddie's home screen |
| **Monitor** (27") | SimTuner swing cameras and overlay |

## Install

1. Download **MySimCaddie.exe** from [Releases → latest](../../releases/tag/latest).
2. Put it somewhere permanent, for example `C:\MySimCaddie\MySimCaddie.exe`, and run it.
   - Windows SmartScreen may warn you because the exe is unsigned. Click **More info → Run anyway**.
3. On first run, **Setup** opens:
   - **Displays:** check that Projector, TV and Monitor are assigned to the right screens. **Identify displays** shows a big number on each screen.
   - **Apps:** confirm the GSPro path (`C:\GSProV1\GSPLauncher.exe`), then **Browse…** to SimTuner's `.exe`.
   - **Logo:** choose your Hack Shack logo. It's copied into MySimCaddie's data folder.
   - Press **Save**.

## Using it

| Action | How |
|---|---|
| Start a session | Click a tile, or select it with the arrow keys and press Enter |
| Get back to MySimCaddie from GSPro | **Ctrl + Alt + H** |
| End a session early | **End Session** (tap twice). This closes GSPro, SimTuner and GSPro Connect |
| Exit or hide | **Exit** button (or **Ctrl + Q** / **Alt + F4**), then **Exit MySimCaddie** or **Show desktop** |
| Refresh status | **F5** |
| Leave Setup | **Back** (top right) or **Esc** without saving; **Save & close** to keep changes |

**GSPro Round** runs these steps:

1. Makes the projector the main display.
2. Starts SimTuner, moves it to the 27" monitor and maximizes it.
3. Starts the GSPro launcher. You press Play.
4. Waits until `GSPro.exe` is running.
5. When you close GSPro, it closes SimTuner and GSPro Connect, which is the Bushnell Launch Pro link.

## Customizing

Everything lives in `%APPDATA%\MySimCaddie\config.json`. Setup → **Open config folder** takes you there. Restart MySimCaddie after hand-editing it. If the JSON has a mistake, MySimCaddie falls back to defaults and keeps a copy named `config.json.broken-*`.

- **`apps`**: each program, with its `path`, optional `arguments`, `processName` (used for detecting it and waiting on it), `runAsAdmin`, and `icon`.
- **`profiles`**: the tiles. Each has an ordered list of `steps`:

  ```jsonc
  {
    "app": "simtuner",          // id from "apps"
    "display": "Monitor",       // Projector | TV | Monitor
    "window": "Maximize",       // None | Maximize | Fill | Move
    "waitForProcess": "",       // for launchers that hand off to another exe (GSPLauncher → GSPro)
    "waitInBackground": false,
    "waitTimeoutSeconds": 45,
    "delayAfterSeconds": 2,
    "closeOnEnd": true,
    "ifRunning": "Skip"         // Skip | Restart
  }
  ```

  Profile-level options are `primaryDisplay`, `sessionProcess` (whose exit ends the session), `alsoCloseOnEnd` and `restorePrimaryOnEnd`.
- **`quickLinks`**: folders, files or URLs shown as buttons.
- **`autoRunProfile`**: a profile id to start automatically when Windows signs in.
- **Icons**: [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font) code points, for example `E7C1` (flag), `E714` (video) or `E8B7` (folder).

## Troubleshooting

Logs are in `%APPDATA%\MySimCaddie\logs\`. Each day gets its own file, and every launch step and window move is recorded there.

## Building

GitHub Actions builds a self-contained single-file `MySimCaddie.exe` on every push to `main` and attaches it to the **latest** release.

To build locally on Windows with the .NET 8 SDK:

```powershell
dotnet publish src/MySimCaddie/MySimCaddie.csproj -c Release -o publish
```

`src/MySimCaddie.Core` contains the config, display, window and launch logic. `src/MySimCaddie` contains the WPF user interface.
