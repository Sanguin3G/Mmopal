# core.GetPixels

## Description

The `core.GetPixels` function captures a specified area of the screen, filters
the pixels based on color bounds, and returns the number of pixels within the
specified color range. This function is useful for detecting specific colors or
objects within a defined screen area.

## Parameters

- **`captureX`** (int): The X-coordinate of the top-left corner of the capture
  area on the screen.
- **`captureY`** (int): The Y-coordinate of the top-left corner of the capture
  area on the screen.
- **`captureWidth`** (int): The width of the capture area on the screen.
- **`captureHeight`** (int): The height of the capture area on the screen.
- **`lowerBound`** (table): A table containing three numbers representing the
  lower bound for the color filter in the BGR color space (Blue, Green, Red).
- **`upperBound`** (table): A table containing three numbers representing the
  upper bound for the color filter in the BGR color space (Blue, Green, Red).
- **`showFrame`** (bool): A boolean value indicating whether to display the
  captured frame for debugging purposes.
- **`saveFrames`** (bool): A boolean value indicating whether to save the
  captured frames.
- **`savePath`** (str): The directory path where the frames should be saved.

## Return value

- **`int`**: Returns the number of pixels that fall within the specified color
  range.

## Example

```lua
local amount_of_pixels = core.GetPixels({
    captureX = 360,
    captureY = 160,
    captureWidth = 320,
    captureHeight = 180,
    lowerBound = {0, 0, 100},   -- Lower bound for color filtering in BGR (e.g., dark red)
    upperBound = {50, 50, 255}, -- Upper bound for color filtering in BGR (e.g., bright red)
    showFrame = false,          -- Show the captured frame
    saveFrames = true,          -- Save the frames to disk for debug inspection
    savePath = "C:/myfolder"
})

core.Log("Amount of pixels: " .. tostring(amount_of_pixels), "blue")
```
