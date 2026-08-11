-- logger.lua
-- Lightweight log level + pretty formatting + core.Log patching

local M = {}

local LEVELS = { ERROR = 1, WARN = 2, INFO = 3, DEBUG = 4 }

local state = {
    level = LEVELS.INFO,
    info_mode = false,
    pretty = true,
    default_tag = "",
    original_core_log = nil,
}

local function level_from_string(s)
    if type(s) ~= "string" then return LEVELS.INFO end
    local u = string.upper(s)
    return LEVELS[u] or LEVELS.INFO
end

local function now_ts()
    local t = os.date("!*t")
    -- Compact format: just hours and minutes [HH:MM]
    return string.format("%02d:%02d", t.hour, t.min)
end

local function fmt_msg(tag, lvl, msg, color)
    -- core.Log already adds timestamps, so don't add formatting here
    -- Strip any existing tags like [GW2TP] from the message
    local cleanMsg = msg:gsub("%[%w+%]%s*", "")

    -- Add invisible color marker at start for UI detection, but strip it from display
    -- The UI will detect this and apply colors, then should strip it for display
    if color and color ~= "" then
        return "[" .. color .. "]" .. cleanMsg
    end
    return cleanMsg
end

local function should_emit(lvl, color)
    -- Honor level threshold
    if lvl > state.level then return false end

    -- Info mode: suppress noisy colors commonly used for verbose tracing
    if state.info_mode and color then
        local c = tostring(color)
        if c == "magenta" or c == "brightmagenta" or c == "blue" then
            return false
        end
    end
    return true
end

function M.configure(opts)
    opts = opts or {}
    if opts.level then state.level = level_from_string(opts.level) end
    if opts.info_mode ~= nil then state.info_mode = opts.info_mode and true or false end
    if opts.pretty ~= nil then state.pretty = opts.pretty and true or false end
    if opts.tag then state.default_tag = opts.tag end
end

-- Patch core.Log to respect info_mode and pretty formatting, while remaining backward-compatible
function M.patch_core(core_ref, tag)
    if not core_ref or type(core_ref.Log) ~= "function" then return end
    if not state.original_core_log then
        state.original_core_log = core_ref.Log
    end
    local default_tag = tag or state.default_tag or ""
    core_ref.Log = function(message, color, ...)
        local msg = tostring(message)
        -- Try to infer severity from color; default to DEBUG otherwise
        local lvl = LEVELS.DEBUG
        local c = tostring(color or "")
        if c == "red" or c == "brightred" then lvl = LEVELS.ERROR
        elseif c == "yellow" or c == "brightyellow" then lvl = LEVELS.WARN
        elseif c == "green" or c == "cyan" or c == "brightgreen" or c == "brightcyan" then lvl = LEVELS.INFO
        else lvl = LEVELS.DEBUG end

        if should_emit(lvl, color) then
            local formatted = fmt_msg(default_tag, lvl, msg, color)
            return state.original_core_log(formatted, color, ...)
        end
        -- Swallow log
        return false
    end
end

-- Explicit levelled logging helpers (bypass core patch if desired)
local function emit(lvl, tag, msg, color)
    if not should_emit(lvl, color) then return end
    local formatted = fmt_msg(tag or state.default_tag, lvl, msg, color)
    if state.original_core_log then
        state.original_core_log(formatted, color)
    elseif core and core.Log then
        core.Log(formatted, color)
    else
        print(formatted)
    end
end

function M.error(tag, msg) emit(LEVELS.ERROR, tag, msg, "brightred") end
function M.warn(tag, msg)  emit(LEVELS.WARN,  tag, msg, "brightyellow") end
function M.info(tag, msg)  emit(LEVELS.INFO,  tag, msg, "brightcyan") end
function M.debug(tag, msg) emit(LEVELS.DEBUG, tag, msg, "brightmagenta") end

return M

