# core.FFXIV_SetFaceDirection

## Description

The `core.FFXIV_SetFaceDirection` function sets the player's facing direction to the specified angles.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.
- **`left_right`** (float): The left-right angle.
- **`up_down`** (float): The up-down angle.

## Return value

- **`void`**

## Example

```lua
core.FFXIV_SetFaceDirection({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe",
    left_right = 1.0,
    up_down = 2.0
})
```
