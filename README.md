# Mmopal

Mmopal is a Windows desktop automation project focused on Guild Wars 2 Trading Post and fishing workflows.

The .NET 10 solution under `src/` is completely standalone and does not load or depend on the old `core.dll` runtime:

- `src/` contains the WPF control panel, restartable worker process, typed GW2 trading logic, owned Windows capture/OpenCV/Windows OCR vision, official GW2 API client, persistence, and optional licensing.
- `assets/templates/tradingpost/` holds the Trading Post UI template images copied from the old scripts.
- `runtime/legacy/` remains on disk only as an unreferenced archive of the old app and can be deleted whenever no longer needed for reference.

Replay data, the offline sample, dry run, and **Screen observe** all run inside the .NET 10 worker using owned Win32 desktop capture, OpenCV template matching, Windows OCR, and the official Guild Wars 2 API. Fixture recording and raw frame capture stay optional. **Live (Armed trading)** places, cancels and lists Trading Post orders through bounded, verified clicks and keystrokes, and only while **Arm live input** is checked.

Start here: [docs/run.md](docs/run.md) and [docs/map.md](docs/map.md). How it trades: [docs/trading-strategy.md](docs/trading-strategy.md) and [docs/gw2-trading-controller.md](docs/gw2-trading-controller.md). Additional notes: [docs/gw2-observation-replay.md](docs/gw2-observation-replay.md), and [docs/core-behavior.md](docs/core-behavior.md).

## Build

```powershell
dotnet restore Mmopal.sln
dotnet build Mmopal.sln
dotnet test Mmopal.sln
```

Use Rider for the .NET solution. Python tooling, if added later, belongs under `tools/vision-replay` and is not part of the live worker.

## Runtime data

User data is stored under `%LOCALAPPDATA%\Mmopal` by default. Do not commit API keys, license files, or runtime CSV/database files.
