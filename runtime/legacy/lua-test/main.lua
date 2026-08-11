-- Simple Arduino Mouse Click Test
-- Clicks at position 100,100 and exits

local core = require("core")

core.Log("Initializing Arduino mouse on COM9...", "magenta")
core.ArduinoInitialize({ comPort = "COM9", baudRate = 115200 })

core.Log("Clicking at position 100,100...", "magenta")

core.ArduinoClickAtPosition({
    X = 100,
    Y = 100,
    clickType = "arduino_leftclick",
    waitBefore = 100,
    waitAfter = 100
})

core.Log("Closing Arduino connection...", "magenta")


core.ArduinoClose()


core.Log("Done...", "magenta")
