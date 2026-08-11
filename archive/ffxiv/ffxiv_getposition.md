# core.FFXIV_GetPosition

## Description

The `core.FFXIV_GetPosition` function to retrieve player coordinates.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g.,
  `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`table`**

```lua
{
    x = <x_coordinate>,
    y = <y_coordinate>,
    z = <z_coordinate>
}
```

## Example

```lua
local position = core.FFXIV_GetPosition({processName = "game.exe", moduleName = "gameModule"})
core.Log("X: " .. position.x .. ", Y:" .. position.y .. ", Z:" .. position.z, "magenta")
```
