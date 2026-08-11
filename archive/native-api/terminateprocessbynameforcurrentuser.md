# core.TerminateProcessByNameForCurrentUser

## Description

The `core.TerminateProcessByNameForCurrentUser` function attempts to terminate a
process with the specified name that is running under the current user's
account.

## Parameters

- **`process_name`** (string): The name of the process to terminate. This should
  be the name of the executable file (e.g., `"notepad.exe"`).

## Return value

- **`bool`**: Returns `true` if the process was successfully terminated;
  otherwise, returns `false`.

## Example

```lua
local was_terminated = core.TerminateProcessByNameForCurrentUser("notepad.exe")
```
