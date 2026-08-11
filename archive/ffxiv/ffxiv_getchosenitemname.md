# core.FFXIV_GetChosenItemName

## Description

The `core.FFXIV_GetChosenItemName` function retrieves the item name found in
search results window

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.

## Return value

- **`string`**: The name of the item hovedered after finding an item on the MB

## Example

```lua
local itemName = core.FFXIV_GetChosenItemName({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
})
```
