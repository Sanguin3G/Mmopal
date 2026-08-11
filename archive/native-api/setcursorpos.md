# core.SetCursorPos

## Description

The `core.SetCursorPos` function moves the mouse cursor to the specified screen coordinates.

## Parameters

- **`x`** (int): The X-coordinate on the screen where the cursor should be moved.
- **`y`** (int): The Y-coordinate on the screen where the cursor should be moved.

## Return value

- **`bool`**: Returns `true` if the cursor was successfully moved; otherwise, returns `false`.

## Example

```lua
local success = core.SetCursorPos(100, 200)
```
