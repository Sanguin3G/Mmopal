# core.OpenProgram

## Description

The `core.OpenProgram` function opens a program with the specified command-line arguments.

## Parameters

- **`program_path`** (string): The full path to the executable file.
- **`command_line_args`** (string): The command-line arguments to pass to the program.

## Return value

- **`void`**: The function does not return any value.

## Example

```lua
core.OpenProgram("C:/Program Files (x86)/Program/program.exe", "-windowed")
```
