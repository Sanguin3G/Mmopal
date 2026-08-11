# core.FFXIV_GetItemsForSale

## Description

The `core.FFXIV_GetItemsForSale` function retrieves slots available in an
open retainer window.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g.,
  `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`int`**: The available slots. If there are no items for sale,
  `FFXIV_GetItemsForSale` will return 0 (out of 20). If retainer is full,
  `FFXIV_GetItemsForSale` will return 20 (20 out of 20).

## Example

```lua
local slots_available = core.FFXIV_GetItemsForSale({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
})
```
