# core.ResizeProcessWindow

## Description

The `core.ResizeProcessWindow` function resizes the window of the specified process to the given width and height.

## Parameters

- **`process_name`** (string): The name of the process whose window you want to resize.
- **`width`** (int): The desired width of the window.
- **`height`** (int): The desired height of the window.

## Return value

- **`void`**: The function does not return any value.

## Example

```lua
core.ResizeProcessWindow("notepad.exe", 800, 600)
```
