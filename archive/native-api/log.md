# core.Log

## Description

Logs a message to the console with optional color formatting and saves the output to a log file in the bot-folder.

## Parameters

- **`message`** (string): The text to log to the console.
- **`color`** (string): The color to display the message in. Available colors:
    * `"black"`
    * `"red"`
    * `"green"`
    * `"yellow"`
    * `"blue"`
    * `"magenta"`
    * `"cyan"`
    * `"white"`
    * `"lightgray"`
    * `"darkgray"`
    * `"brightred"`
    * `"brightgreen"`
    * `"brightyellow"`
    * `"brightblue"`
    * `"brightmagenta"`
    * `"brightcyan"`
    * `"brightwhite"`

## Return value

- **`void`**: The function does not return any value.

## Example

```lua
core.Log("Sup dudes!", "magenta")  -- Logs "Sup dudes!" in magenta color
```
