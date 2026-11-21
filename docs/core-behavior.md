# Core behavior

REA inspected `core.dll` from the old distribution (the former `runtime/legacy` folder, since deleted; it remains in git history). This note records that inspection and the owned frame code that can be tested without the module. It is not recovered vision source, and it is not a claim that the new score matches the packed module.

## What the file is

`core.dll` is a packed native Lua module, about 8.3 MB, SHA-256 `78e1000e03368e56cfde9d23db38e69a7feae3831993ee2f013ad207da2aae8a`. REA's managed inspector does not apply: that path is for .NET assemblies such as `mmopal.dll`. Native pseudocode needs a decompiler engine. REA does not include one. It drives Ghidra, IDA, Hopper, or Kuna, and its setup does not install them.

Ghidra 12.1.4 PUBLIC is installed at `D:\Tools\ghidra_12.1.4_PUBLIC`. `GHIDRA_INSTALL_DIR` points there, and `REA_ANALYSIS_PROVIDER` is `ghidra`, for new processes. The JDK already on this machine, OpenJDK 25, is accepted. Hopper was not installed. This Grok session's MCP server does not see those variables until Grok is restarted. The command-line path already works.

The file has two exports. `luaopen_core` is ordinal 2 at `0x18000a5d0`. `core_SetLogCallback` is the other export. Image base `0x180000000`. The ordinary code sections have no raw bytes on disk. The payload sits in sections named `.9hM` (8,351,232 bytes) and `.r&D` (5,843,779 bytes). Earlier static evidence: imports `ev_c9555594afbdb95836b283de9de0caab402f09ea0cbdcfe64522de0bd4ca5258`, exports `ev_0b776a45ebf0599842635d65a8bb22724f8dec58a57858fc4508a0bc00ac0621`.

## What Ghidra saw

Ghidra 12.1.4 finished default auto-analysis on the file as it sits on disk. Overview evidence `ev_d97102566a47ac4472f3851d6db3eba94d7b7202bd61b0870c33844cd7d43c2d`: 2,527 procedures and 71 defined strings. Those procedures are not recovered vision code. Decompiling `luaopen_core` returns only bad instruction data (`halt_baddata()`). Evidence `ev_6fa00253abd671b6bc97bc4d2ac83ddaa63e2d2235bbe75682afbea9b36838d4`.

A search of that defined-string inventory returned 68 values. Evidence `ev_c7156a9e4c5762d474a537be5721de1dc36f470de900f666bbc1f0afba38790e`. Every returned value is part of the packer stub: `opencv_core4.dll`, `opencv_imgproc4.dll`, `opencv_imgcodecs4.dll`, `opencv_highgui4.dll`, `opencv_photo4.dll`, `tesseract55.dll`, `leptonica-1.85.0.dll`, `libcurl.dll`, `lua.dll`, plus Win32, GDI, and the Visual C++ runtime. Leftover symbols include `cv::imread`, `cv::countNonZero`, `cv::waitKey`, `cv::drawContours`, `cv::fastNlMeansDenoising`, `tesseract::TessBaseAPI::End`, `pixSetPixel`, `curl_easy_perform`, and `lua_createtable`, along with the two export names and an empty assembly manifest. One short token, `ZaDdlL`, is defined and is not a function name. Names such as `PerformOCRAndParse`, `ClickAtPosition`, `Image`, and `GW2_FishHack` are absent from that search. Ghidra lists defined strings only, so a miss there leaves the packed bytes unchecked.

Those import names show libraries the module was linked against. They do not show which functions run, or with which arguments. `GW2_FishHack` stays retired either way.

## Import directory and file bytes

A later REA pass read the PE tables of the same file, SHA-256 `78e1000e03368e56cfde9d23db38e69a7feae3831993ee2f013ad207da2aae8a`, without executing it and without opening Ghidra again.

The export directory is complete. Evidence `ev_0b776a45ebf0599842635d65a8bb22724f8dec58a57858fc4508a0bc00ac0621`, rows `ev_dcaf4adbf7791d8ac84f96b3d5cc2e8e71c21802c3016a009340856ec3357756`. `core_SetLogCallback` is ordinal 1 at `0x18000a5c0`. `luaopen_core` is ordinal 2 at `0x18000a5d0`. Neither export is forwarded. The directory timestamp is `4294967295`.

The import directory is also complete: 30 DLL descriptors and 36 named functions, with no delay-load directory. Evidence `ev_c9555594afbdb95836b283de9de0caab402f09ea0cbdcfe64522de0bd4ca5258`. Most descriptors name one function. The vision and text libraries contribute one symbol each:

| DLL | Declared import |
| --- | --- |
| `opencv_core4.dll` | `cv::countNonZero` |
| `opencv_imgcodecs4.dll` | `cv::imread` |
| `opencv_imgproc4.dll` | `cv::drawContours` |
| `opencv_photo4.dll` | `cv::fastNlMeansDenoising` |
| `opencv_highgui4.dll` | `cv::waitKey` |
| `tesseract55.dll` | `tesseract::TessBaseAPI::End` |
| `leptonica-1.85.0.dll` | `pixSetPixel` |
| `libcurl.dll` | `curl_easy_perform` |
| `lua.dll` | `lua_createtable` |

`KERNEL32.dll` also declares `LoadLibraryA`, `GetProcAddress`, `GetModuleHandleA`, `GetModuleFileNameW`, `LocalAlloc`, `LocalFree`, `ExitProcess`, `GetVersion`, and `GlobalUnlock`. The remaining descriptors are one Win32 or C runtime function each, including `ReleaseDC`, `CharUpperBuffW`, `GetDeviceCaps`, `PathFileExistsW`, and `LookupAccountSidA`. These rows are the on-disk import directory. They do not show which calls run. `LoadLibraryA` and `GetProcAddress` can resolve more APIs after the module loads, and this pass did not see those names.

The TLS directory declares no callbacks. `AddressOfCallBacks` is `0x0` and the callback list is empty. Evidence `ev_aa7bf29e72e16ffb11129ff1f2f46e1944de77acdb0ef775efc9d862292735ec`. That is the absence of a TLS callback entry, not proof that `luaopen_core` skips unpacking. The load-config `GuardFlags` value includes `IMAGE_GUARD_CF_INSTRUMENTED`, which is a linker claim. The recorded CFG check pointer is `0x180c2e0ef`, in section `.9hM`.

File entropy is 7.9499 across 8,358,400 bytes. Evidence `ev_e2e99b2e98d9eb1b734c1dee42df2ac757e90c62addaeaff043c261cc27f903d`, parent `ev_93e7daeeebbb3443ab551ca7b0f44f6187ec2e50a13735c5ee5c123649898c88`. The longest high run is 6,512,640 bytes starting at file offset 102400. Entropy does not separate compression from encryption.

A direct read of those same bytes searched the ASCII and UTF-16LE spellings of `PerformOCRAndParse`, `ClickAtPosition`, `GetPixels`, `GetCoordinates`, `IsSimilar`, `KeyPress`, `SendString`, `GW2_GetCurrentTransactions`, `GW2_GetGoldBalance`, and `GW2_FishHack`. None of those spellings occur. The two export names and the import symbols above do occur as ASCII. An encoded copy inside the high-entropy range would not appear in that search.

One local load then listed the Lua table. `LegacyLuaSession` called `luaopen_core` and ran `next` over `core`. It did not call any function in that table. The module printed that it could not open `config.ini` beside the temporary process, so this load did not read a Guild Wars 2 API key. No unpacked image was written to disk. REA's debugger and Frida workflows remain unavailable on this Windows host, and the function bodies are still unrecovered.

Every returned value was a function:

| Group | Names |
| --- | --- |
| Vision | `Image`, `GetCoordinates`, `GetPixels`, `IsSimilar`, `PerformOCRAndParse`, `DetectLetters`, `DetectNumbers`, `DetectSignificantChange`, `NumberSniffer`, `TextSniffer` |
| Input | `ClickAtPosition`, `ClickAndHoldMouseButton`, `SetCursorPos`, `KeyPress`, `SendString`, `SendStringByInput` |
| Account data | `GW2_GetCurrentTransactions`, `GW2_GetGoldBalance`, `GW2_GetListingPrices` |
| Clipboard and timing | `CopyTextToClipboard`, `GetTextFromClipboard`, `Sleep`, `Log`, `StopRequested` |
| Process | `IsProcessRunning`, `IsProcessRunningForCurrentUser`, `OpenProgram`, `PositionProcessWindowToTopLeft`, `ResizeProcessWindow`, `SetProcessAsForeground`, `TerminateProcessByName`, `TerminateProcessByNameForCurrentUser` |
| Arduino | `ArduinoInitialize`, `ArduinoClose`, `ArduinoClickAtPosition`, `SetArduinoMode` |

`GW2_FishHack`, `GW2_Scraper_BLTC`, `GW2_Scraper_GW2Profits`, and the `FFXIV_*` functions are also on that table. They stay retired. `StopRequested` is a function on the module table. `ShouldStop` is still the name the old host injected, and it was not one of the returned keys.

## Libraries for the replacement

The packed module stays a reference. The owned path uses current libraries:

| Job | Library |
| --- | --- |
| Template match and image preprocess | OpenCvSharp4, the OpenCV 4 binding. Match with `Cv2.MatchTemplate` and normalized correlation. |
| OCR | `Windows.Media.Ocr`, the reader shipped with Windows. The abandoned `tesseract55.dll` stays out of the new app. |
| Click and key | The existing `Win32InputController` (`SendInput`), used by Live mode only while Arm live input is checked. |
| Prices, transactions, and balance | The official Guild Wars 2 API. |

`Gw2OfficialDataProvider` fills each transaction's minimum sell and minimum buy from `v2/commerce/prices`: `sells.unit_price` is the lowest ask, and `buys.unit_price` is the highest bid. A missing side, or a unit price of zero or less, stays empty. `OrderBookReport` then says when a buy is behind that highest bid or a sell is behind that lowest ask. Those numbers do not place or cancel orders.

`FrameVision.FindTemplate` remains the dependency-free stand-in. Its score is `1 - mean absolute BGR difference / 255`. `FrameTemplateMatcher` uses OpenCvSharp4 4.13.0.20261107. Textured templates use `Cv2.MatchTemplate` with normalized correlation. A flat template has no correlation variance, so that case uses normalized squared difference and reports `1` minus that distance. Alpha is dropped before the search. `WindowsOcrReader` reads a saved frame with `Windows.Media.Ocr`. The Windows engine does not return a confidence score, so recognized text is reported as `1`.

## Five calls

| Call | Old packed module (removed) | Owned .NET 10 code |
| --- | --- | --- |
| Screen capture | No raw-frame export in `core.dll`. | `WindowsDesktopScreenshotProvider` returns top-down BGRA32. |
| Pixel count | `core.GetPixels` on the live screen. | `FrameVision.CountPixels` counts inclusive blue, green, and red bounds in a saved frame. Alpha is ignored. |
| Template match | `core.Image` / `core.GetCoordinates`. | `FrameTemplateMatcher` searches the supplied frame with OpenCV (`Cv2.ImRead`, supporting PNG/BMP). |
| OCR | `core.PerformOCRAndParse`. | `WindowsOcrReader` reads the supplied frame with Windows OCR. |
| Click and key | `core.ClickAtPosition` / `core.KeyPress`. | `Win32InputController` (`SendInput`), driven by `Gw2TradingPostUiDriver` in Live mode. Tests use a fake input controller and send no input. |

Screen observe always uses `FrameTradingPostUiProbe` and `Gw2OfficialDataProvider` in process.

## What stays

`Mmopal.LegacyBridge` has been removed and the solution no longer loads `core.dll`. The old `runtime/legacy` distribution has been deleted from the repository. Template PNGs now live in `assets/templates/tradingpost`. Screen observe uses owned capture, OpenCV, Windows OCR, and the official Guild Wars 2 API. Live mode sends input only while armed; see [gw2-trading-controller.md](gw2-trading-controller.md). `GW2_FishHack` stays retired.

