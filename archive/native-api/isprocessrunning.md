# core.IsProcessRunning

## Description

The `core.IsProcessRunning` function checks if a process with the specified name
is currently running under the current user.

## Parameters

- **`process_name`** (string): The name of the process to check.

## Return value

- **`bool`**: Returns `true` if the process is running; otherwise, returns
  `false`.

## Example

```lua
local is_running = core.IsProcessRunning("notepad.exe")
```
