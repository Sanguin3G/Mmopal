# core.FFXIV_GetItemName

## Description

The `core.FFXIV_GetItemName` function retrieves the name of a marked item on the
marketboard

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`string`**: The name of the item hovedered after finding an item on the MB

## Example

```lua
local itemName = core.FFXIV_GetItemName({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
})
```
