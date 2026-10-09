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
3. Starts the GSPro launcher and presses **Play!** in its "GSPro Configuration" pop-up for you. If GSPro Connect stops on its **Connection Manager** tab instead of connecting by itself, it waits 6 seconds and then presses **Connect**.
4. Waits until `GSPro.exe` is running.
5. When you close GSPro, it closes SimTuner and GSPro Connect, which is the Bushnell Launch Pro link.

## Adding your own scenarios

Go to **Setup → Scenarios**. You don't need to edit any files.

- **New scenario** creates a tile. Give it a name, subtitle, icon and color, then pick which display should be the **main display**. Games open full-screen there.
- **Launch steps** run in order. For each one, choose the **app** (or press **Browse…** on the step to find any .exe), the display to **put it on**, how to place the window, and whether to **close it when the round ends**. Under **More options** you'll find:
  - **Wait for program**, for launchers such as GSPLauncher that hand off to GSPro
  - **Pause after**
  - **If already running**
- **Auto-press button** (under **More options**) clicks a button in a pop-up for you. For GSPro, that's **Play!** in "GSPro Configuration". The second row, **Then, if it shows up, press**, is for a button that only sometimes appears: for GSPro, **Connect** in the "GSPro x Foresight" window, which is GSPro Connect. If the launch monitor connects by itself, nothing is pressed. **Use GSPro's Play! + Connect** fills in both rows. Browsing to GSPLauncher.exe on a step sets this up automatically.
- **The round is over when this closes** names the program that ends the round, usually the game. MySimCaddie then closes everything marked to close, plus anything ticked under **Then also close**.
- **Start this scenario automatically** runs the scenario when MySimCaddie opens at sign-in.
- Use **Up/Down** to reorder tiles, **Duplicate** to copy one as a starting point, and **Delete** (tap twice) to remove one.

To add a program such as E6 Connect, FSX or ShotForge, go to **Setup → Room & apps → Add an app** first. It then shows up in each step's app list.

## GSPro "Run as admin"

If GSPro is set to **Run as admin**, Windows won't let a normal program press its buttons. Turn on **Setup → Room & apps → System → Run MySimCaddie as administrator**. Windows asks for permission once. After that, MySimCaddie starts as administrator through a Task Scheduler task named **MySimCaddie**, with no prompt at sign-in, and can press **Play!** and **Connect** for you. Apps it launches inherit administrator rights, so the per-app **Run as admin** prompts go away too.

## Home screen layout

**Setup → Room & apps → Look → Tile position** puts the tiles in a column on the left, a column on the right, or a row across the top. Your logo fills the rest of the screen. **Logo visibility** controls how strongly it shows.

## Customizing (advanced)

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
