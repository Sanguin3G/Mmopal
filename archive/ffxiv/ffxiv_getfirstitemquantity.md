# core.FFXIV_GetFirstItemQuantity

## Description

The `core.FFXIV_GetFirstItemQuantity` function retrieves the quantity of the
first item chosen on the marketboard. This it to decidewhat amount the lowest
selling person is putting the item for sale for.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`int`**: The quantity of the first item.

## Example

```lua
local itemQuantity = core.FFXIV_GetFirstItemQuantity({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
})
```
