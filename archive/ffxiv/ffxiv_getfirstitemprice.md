# core.FFXIV_GetFirstItemPrice

## Description

The `core.FFXIV_GetFirstItemPrice` function retrieves the price of the first
item chosen on the marketboard. This it to decide what price the lowest selling
person is putting the item for sale for.


## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`int`**: The price of the first item.

## Example

```lua
local itemPrice = core.FFXIV_GetFirstItemPrice({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
})
```
