# core.GW2_FishHack

## Description

The `core.GW2_FishHack` ensures you always catch a fish no matter the fishing
power.

## Parameters

- **`processName`** (string): The name of the GW2 process.
- **`activation`** (bool): Indicating whether to activate the hack or not
## Return value

- **`bool`**: Returns `true` if the operation (activation or deactivation) is
  successful; otherwise, returns `false`.

## Example

```lua
local success = core.GW2_FishHack({processName = PROCESS_NAME, activation = true})
if success then
    core.Log("Fish hack activated", "green")
else
    core.Log("Something went wrong when activating fish hack", "blue"
end
```
