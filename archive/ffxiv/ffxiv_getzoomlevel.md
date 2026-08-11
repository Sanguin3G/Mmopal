# core.FFXIV_GetZoomLevel

## Description

The `core.FFXIV_GetZoomLevel` function retrieves the current camera zoom level.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`float`**: The current zoom level of the camera.

## Example

```lua
local zoomLevel = core.FFXIV_GetZoomLevel({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
})
```
