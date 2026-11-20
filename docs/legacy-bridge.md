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
| Local input (unwired) | `Win32InputController` (`SendInput`, not constructed by the worker) |

`runtime/legacy/` stays on disk only as an unreferenced archive of the original distribution. Neither `Mmopal.Desktop` nor `Mmopal.Worker` loads `core.dll`, `lua.dll`, `tesseract55.dll`, or any file from `runtime/legacy/`.

Do not put a real GW2 API key in committed configuration. Supply it through `MMOPAL_Native__Gw2ApiKey`.
