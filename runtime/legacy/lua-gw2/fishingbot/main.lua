-- main.lua
local core                 = require("core")
-- Cooperative Sleep wrapper to respect global Pause
local __orig_Sleep = core and core.Sleep
if __orig_Sleep then
  core.Sleep = function(ms)
    local remaining = tonumber(ms) or 0
    if remaining <= 0 then return __orig_Sleep(0) end
    local step = 25
    while remaining > 0 do
      local s = math.min(step, remaining)
      __orig_Sleep(s)
      remaining = remaining - s
    end
  end
end
local util                 = require("util")

-- GLOBAL VARIABLES
EXE_FILE                   = "Gw2-64.exe"
PROCESS_NAME               = "Guild Wars 2"
EXE_FILEPATH               = "C:/Guild Wars 2/Gw2-64.exe"
CURRENT_DIR                = util.GetCurrentDirectory()
DEBUG_PATH                 = CURRENT_DIR .. "\\debug\\"
DEBUG                      = false
SCREENSHOTS                = CURRENT_DIR .. "\\lua-gw2\\fishingbot\\screenshots\\"
DELAY                      = 0
WINDOW_SIZE                = { width = 1024, height = 768 }

local function SwapToFishingGear()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "swap_weapons.png", max_wait_time_ms = 3, click_type = "noclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("Pressing J to swap weapons", "brightmagenta")
        core.KeyPress("J")
        core.Sleep(1000)
    else
        core.Log("We are likely already having fishing gear equipped", "brightmagenta")
    end
end

local function SelectCastLineSkill()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "cast_line.png", max_wait_time_ms = 4, click_type = "noclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 2000, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("Cast line skill selected", "brightmagenta")
        core.SetCursorPos(WINDOW_SIZE.width / 2, WINDOW_SIZE.height / 2) -- aim at the middle of the GW2 screen
        core.KeyPress("1")
    else
        core.Log("Could not find cast line skill", "brightmagenta")
    end
end


local function ReleaseLine()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "release_line.png", max_wait_time_ms = 5, click_type = "noclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("Relesing line", "brightmagenta")
        core.KeyPress("1")
        core.Sleep(1000)
        core.SetCursorPos(WINDOW_SIZE.width / 2, WINDOW_SIZE.height / 2) -- aim at the middle of the GW2 screen
        core.Sleep(1000)
    else
        core.Log("Could not find release line skill", "brightmagenta")
    end
end


local function SetHook()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "active_fishing.png", max_wait_time_ms = 100, click_type = "noclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })

    if status then
        core.Log("We are looking for the exclamation mark", "brightmagenta")
        while true do
            local pixels = core.GetPixels({ captureX = 360, captureY = 160, captureWidth = 320, captureHeight = 180, lowerBound = {160, 50, 50},  upperBound = {180, 255, 255}, showFrame = false, saveFrames = false, savePath = DEBUG_PATH})
            if pixels > 800 then
                core.Log("Exclamation mark found, setting hook!", "green")
                core.KeyPress("1")
                break
            end
        end
    end
end


local function WeAreCatchingFish()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "we_are_catching_fish.png", max_wait_time_ms = 3, click_type = "noclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("We are catching fish, wait", "brightmagenta")
        core.Sleep(12000)
    end
end



function StartFishingBot()
    core.Log("Start fishingbot", "yellow")
    util.AdjustProgramWindow(PROCESS_NAME, WINDOW_SIZE.width, WINDOW_SIZE.height)
    util.ResizeWindow(PROCESS_NAME, WINDOW_SIZE.width, WINDOW_SIZE.height)
    local success = core.GW2_FishHack({processName = PROCESS_NAME, activation = true})
    if success then
        core.Log("FishHack activated", "magenta")
        while true do
            SwapToFishingGear()
            SelectCastLineSkill()
            ReleaseLine()
            SetHook()
            WeAreCatchingFish()
        end
    else
        core.Log("FishHack deactivated", "magenta")
    end
end

StartFishingBot()

