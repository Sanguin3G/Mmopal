# core.Image

## Description

The `core.Image` function searches for a specific image on the screen within a
defined area and can optionally perform actions like clicking or moving the
mouse.
## Parameters

- **`image_filepath`** (str): Path to the image file to search for on the
  screen.
- **`max_wait_time_ms`** (int): Max time (ms) to wait while searching for the
  image.
- **`click_type`** (str): Click type when image is found: `"leftclick"`,
  `"noclick"`.
- **`mouse_move`** (bool): Move mouse to image's position without clicking
  (`true`/`false`).
- **`match_score`** (float): Match accuracy (0 to 1). Higher value means more
  precise match.
- **`capture_x`** (int): X-coordinate of the capture area's top-left corner.
- **`capture_y`** (int): Y-coordinate of the capture area's top-left corner.
- **`capture_width`** (int): Width of the capture area on the screen.
- **`capture_height`** (int): Height of the capture area on the screen.
- **`wait_before`** (int): Time (ms) to wait before starting the image search.
- **`wait_after`** (int): Time (ms) to wait after the image is found or search
  completes.
- **`offset_x`** (int): Horizontal offset for clicking or moving the mouse.
- **`offset_y`** (int): Vertical offset for clicking or moving the mouse.
- **`print_match_score`** (bool): Print match score for debugging
  (`true`/`false`).
- **`debug`** (bool): Enable/disable debug mode (`true`/`false`).
- **`debug_path`** (str): Filepath to save debugging images/data if debug is
  enabled.
- **`speed_delay`** (int): Delay (ms) before performing action after image is
  found.
- **`random_click_pos`** (bool): Click a random position within the found image
  area (`true`/`false`).
- **`show_cursor`** (bool): Show cursor during operation (`true`/`false`).

## Return value

**Bool**: Returns `true` if the image is found and the specified action is
performed; otherwise, returns `false`.

## Example

```lua
local my_image = core.Image({
    image_filepath = "C:/screenshots/my_image.png",
    max_wait_time_ms = 5,
    click_type = "leftclick",
    mouse_move = false,
    match_score = 0.9,
    capture_x = 0,
    capture_y = 0,
    capture_width = 1024,
    capture_height = 720,
    wait_before = 0,
    wait_after = 0,
    offset_x = 0,
    offset_y = 0,
    print_match_score = false,
    debug = DEBUG,
    debug_path = DEBUG_PATH,
    speed_delay = 0,
    random_click_pos = false,
    show_cursor = false
})
```

In this example, the `core.Image` function is used to search for an image file
named my_image.png located in the C:\screenshots directory. The function will
search for this image within a specified capture area on the screen. The capture
area is defined by the coordinates (0, 0) for the top-left corner and has a
width of 1024 pixels and a height of 720 pixels.

The function will wait for a maximum of 5 milliseconds to find the image. If the
image is found, the function will perform a left-click ("leftclick") at the
image's location. The mouse will not move unless the image is found, as
indicated by mouse_move = false.

The match_score is set to 0.9, meaning that the function requires at least 90%
similarity between the target image and the image found on the screen to
consider it a match.
