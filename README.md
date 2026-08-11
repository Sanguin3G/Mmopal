# Mmopal

Mmopal is a Windows desktop automation research project focused on Guild Wars 2 Trading Post and fishing workflows.

The repository contains two generations:

- `src/` is the new .NET 10 architecture: desktop control panel, restartable worker, typed GW2 domain logic, native integration boundaries, persistence, and optional licensing.
- The recovered Lua/native runtime remains available as a compatibility reference while the new worker is developed.

The new application is deliberately safe to start in dry-run mode. Live game interaction is not enabled by this scaffold until the native compatibility adapter and replay tests have been validated.

## Build

```powershell
dotnet restore Mmopal.sln
dotnet build Mmopal.sln
dotnet test Mmopal.sln
```

Use Rider for the .NET solution. Python tooling, if added later, belongs under `tools/vision-replay` and is not part of the live worker.

## Runtime data

User data is stored under `%LOCALAPPDATA%\Mmopal` by default. Do not commit API keys, license files, or runtime CSV/database files.
