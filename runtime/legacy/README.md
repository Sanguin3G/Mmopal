# Recovered legacy runtime

This directory preserves the original packaged mmopal runtime with its internal relative layout intact.

The legacy executable is still based on .NET 6 and is not rebuilt by the new solution. Run it from this directory if you need to compare behavior with the modernized worker:

```powershell
Set-Location $PSScriptRoot
./mmopal.exe
```

Do not edit this copy while creating compatibility fixtures. Treat it as the baseline artifact.
