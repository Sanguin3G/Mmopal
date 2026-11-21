# Legacy bridge (removed)

`Mmopal.LegacyBridge` and all runtime dependencies on the old packed `core.dll` Lua module have been removed from the .NET 10 solution.

## Current owned replacements

| Boundary | Owned .NET 10 implementation |
| --- | --- |
| Screen capture | `WindowsDesktopScreenshotProvider` (Win32 GDI32 top-down BGRA32) |
| Template search | `FrameTemplateMatcher` (`OpenCvSharp4`) with templates in `assets/templates/tradingpost` |
| OCR | `WindowsOcrReader` (`Windows.Media.Ocr`) |
| Trading Post UI probe | `FrameTradingPostUiProbe` + `ScreenTradingPostObservationReader` |
| GW2 orders & wallet | `Gw2OfficialDataProvider` (official GW2 v2 API with full pagination) |
| Local input | `Win32InputController` (`SendInput`, used by the worker in armed Live mode) |

The original distribution that lived in `runtime/legacy/` has been deleted from the repository; it remains in git history. Neither `Mmopal.Desktop` nor `Mmopal.Worker` loads `core.dll`, `lua.dll`, `tesseract55.dll`, or any other file from it.

Do not put a real GW2 API key in committed configuration. Supply it through `MMOPAL_Native__Gw2ApiKey`.
