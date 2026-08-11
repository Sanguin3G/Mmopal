# core.FFXIV_SetForSaleGetFirstListedItem

## Description

The `core.FFXIV_SetForSaleGetFirstListedItem` function sets first listed item for
sale when a retainer i open. This is used when adjusting prices for relisting in
terms of deciding whether to adjust price or not to ensure we are lowest selling
on the marketboard.

## Parameters

A table consisting of:

- **`process_name`** (string): The name of the process (e.g., `"ffxiv_dx11.exe"`).
- **`module_name`** (string): The name of the module within the process.
- **`value`** (int): The new price

## Return value

- **`int`**: The price of the first item.

## Example

```lua
local itemPrice = core.FFXIV_ForSaleGetFirstListedItem({
    process_name = "ffxiv_dx11.exe",
    module_name = "ffxiv_dx11.exe"
    value = 5
})
```
