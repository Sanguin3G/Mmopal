# core.FFXIV_Teleport

## Description

The `core.FFXIV_Teleport` function teleports the player to the specified
coordinates in the game.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g.,
  `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.
- **`x`** (float): The X-coordinate to teleport to.
- **`y`** (float): The Y-coordinate to teleport to.
- **`z`** (float): The Z-coordinate to teleport to.

## Return value

- **`void`**

## Example

```lua
core.FFXIV_Teleport({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe",
    x = 10.0,
    y = 20.0,
    z = 30.0
})
```
