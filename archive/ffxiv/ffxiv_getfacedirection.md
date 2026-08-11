# core.FFXIV_GetFaceDirection

## Description

The `core.FFXIV_GetFaceDirection` function retrieves the player's current facing
direction in terms of left-right and up-down angles.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g.,
  `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`table`**: A table with two keys:
  - **`left_right`** (float): The left-right angle.
  - **`up_down`** (float): The up-down angle.

## Example

```lua
local direction = core.FFXIV_GetFaceDirection({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
})
```
