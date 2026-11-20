# GW2 observation and replay

The Trading Post implementation has two explicit sources:

- `replay` (the desktop default): uses an inline market payload or a JSONL observation fixture.
- `screen` (alias `observe`): reads the screen with owned Win32 capture (`WindowsDesktopScreenshotProvider`), OpenCV template matching (`FrameTemplateMatcher`), and Windows OCR (`WindowsOcrReader`), and reads all pages of open orders plus coin balance from the official Guild Wars 2 API.

Screen observation never calls click, keyboard, text-input, clipboard, Arduino, order, cancel, or relist operations, and never loads `core.dll`. If the Trading Post is not detected, `TradingPostMarketReader` removes the balance from the controller input so candidate actions become safe non-actions.

## Desktop workflow

Use the source selector in the desktop window:

1. Start with **Replay data** and an optional **Candidates path** (JSON or CSV) and/or **Fixture path** (JSONL).
2. Use **Screen observe** for a read-only live screen pass. Supply the API key through `MMOPAL_Native__Gw2ApiKey`.
3. Enter a **Recording path** to append JSONL observations. Enable **Capture frames** to store one raw `BGRA32` artifact per cycle beside the fixture.

The frame artifacts are intentionally raw and are accompanied by the capture region and stride in the native frame model. They are useful for offline tools; the controller does not load them during replay.

## Worker payload

The `start` command accepts a JSON `payload`:

```json
{
  "source": "replay",
  "candidatesPath": "D:/Mmopal/candidates.json",
  "fixturePath": "D:/Mmopal/fixtures/gw2.jsonl",
  "recordPath": null,
  "captureFrames": false,
  "captureDirectory": null
}
```

For a read-only screen observation pass:

```json
{
  "source": "screen",
  "candidatesPath": "D:/Mmopal/candidates.csv",
  "recordPath": "D:/Mmopal/fixtures/gw2-session.jsonl",
  "captureFrames": true,
  "captureDirectory": "D:/Mmopal/fixtures/gw2-session.frames",
  "visibleItemName": "Example Item",
  "templateRoot": "assets/templates/tradingpost"
}
```

The `screen` source does not enable live execution. `RunMode.Live` sends no input.

## Fixture format

Each JSONL line is a versioned `TradingObservationFixtureRecord` containing a typed `TradingPostObservation` and an optional relative frame artifact path. Both `ReplayTradingPostObservationReader` and `ReplayMarketReader` rebase observation timestamps to the current clock on each cycle so long-running replay sessions never go stale.
