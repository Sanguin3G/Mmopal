# core.FFXIV_SetZoomLevel

## Description

The `core.FFXIV_SetZoomLevel` function sets the camera zoom level to the specified value.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.
- **`value`** (float): The desired zoom level.

## Return value

- **`void`**

## Example

```lua
core.FFXIV_SetZoomLevel({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe",
    value = 2.0
})
```
