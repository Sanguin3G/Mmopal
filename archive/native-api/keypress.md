# core.KeyPress

## Description

The `core.KeyPress` function is used to simulate pressing a specific key or key
combination on the keyboard.

## Parameters

- **`key`** (string): The key or key combination to be pressed. Supported values
  include:
    - `"Enter"`: Simulates pressing the `Enter` key.
    - `"Backspace"`: Simulates pressing the `Backspace` key.
    - `"Delete"`: Simulates pressing the `Delete` key.
    - `"Esc"`: Simulates pressing the `Esc` key.
    - `"L"`: Simulates pressing the `L` key.
    - `"J"`: Simulates pressing the `J` key.
    - `"1"`: Simulates pressing the `1` key.
    - `"ShiftC"`: Simulates pressing the `Shift+C` key combination.
    - `"ShiftD"`: Simulates pressing the `Shift+D` key combination.
    - `"ShiftL"`: Simulates pressing the `Shift+L` key combination.
    - `"ShiftU"`: Simulates pressing the `Shift+U` key combination.
    - `"CtrlA"`: Simulates pressing the `Ctrl+A` key combination.
    - `"CtrlC"`: Simulates pressing the `Ctrl+C` key combination.
    - `"CtrlV"`: Simulates pressing the `Ctrl+V` key combination.
    - More keys can be added as needed.


## Return value

- **`void`**: This function does not return any values.

## Example

```lua
core.KeyPress("Enter")
core.KeyPress("ShiftL")
core.KeyPress("CtrlV")
```
