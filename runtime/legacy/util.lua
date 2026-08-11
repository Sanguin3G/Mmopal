-- util.lua
local core = require("core")

local M = {}

-- Function to read the entire content of a file
function M.ReadFile(filePath)
    local file = io.open(filePath, "r")
    if not file then return nil end
    local content = file:read("*all")
    file:close()
    return content
end

-- Function to split a string by a given delimiter
function M.Split(inputStr, sep)
    if sep == nil then
        sep = "%s"
    end
    local t = {}
    for str in string.gmatch(inputStr, "([^"..sep.."]+)") do
        table.insert(t, str)
    end
    return t
end

function M.ReadFileContents(filePath)
    local file = io.open(filePath, "r")  -- Open file in read mode
    if not file then return nil end  -- Return nil if file cannot be opened

    local content = file:read("*a")  -- Read entire file content
    file:close()  -- Close the file

    return content
end

function M.ParseCSV(csv)
    if not csv then
        core.Log("CSV content is nil", "red")
        return nil
    end

    core.Log("Starting CSV Parsing", "blue")

    local data = {}

    -- Split CSV into lines
    local lines = {}
    for line in csv:gmatch("[^\r\n]+") do
        -- Sanitize line: Replace &mdash and other special entities (you can add more replacements here)
        line = line:gsub("&mdash;", "-")
        table.insert(lines, line)
    end

    -- Log how many lines were found
    core.Log("Number of lines found: " .. tostring(#lines), "blue")

    if #lines == 0 then
        core.Log("No lines found in CSV", "red")
        return nil
    end

    -- Extract header line
    local headers = {}
    core.Log("Extracting headers from the first line", "blue")
    for header in lines[1]:gmatch("[^;]+") do
        table.insert(headers, header)
        -- core.Log("Header found: " .. header, "green")
    end

    -- Extract data lines
    core.Log("Parsing data rows", "blue")
    for i = 2, #lines do
        local line = lines[i]
        -- core.Log("Parsing line " .. tostring(i) .. ": " .. line, "yellow")

        local rowData = {}
        local j = 1
        local valueCount = 0

        for value in line:gmatch("[^;]+") do
            valueCount = valueCount + 1
            local header = headers[j]

            -- Debug if a header is missing or the value count doesn't match
            if not header then
                -- core.Log("Missing header for value: " .. value, "red")
                break  -- Stop processing the row since there's a mismatch
            end

            -- core.Log("Processing value for header '" .. header .. "': " .. value, "green")

            -- Convert to number for Quantity and MinimumProfit
            if header == "Quantity" or header == "MinimumProfit" then
                rowData[header] = tonumber(value)
                -- core.Log("Converted to number: " .. tostring(rowData[header]), "blue")
            else
                rowData[header] = value  -- Leave as string
            end
            j = j + 1
        end

        -- Check if the number of values matches the number of headers
        if valueCount ~= #headers then
            -- core.Log("Warning: Number of values (" .. valueCount .. ") does not match the number of headers (" .. #headers .. ")", "orange")
        end

        -- Add row data to the overall data table
        table.insert(data, rowData)
        -- core.Log("Row data added: " .. tostring(rowData), "blue")
    end

    core.Log("CSV parsing complete", "blue")
    return data
end

function M.Trim(s)
    return s:match('^()%s*$') and '' or s:match('^%s*(.*%S)')
end

function M.LoadSettings(filename)
    local settings = {}
    local section

    -- Open the file
    local file = io.open(filename, "r")
    if not file then
        print("Failed to open settings file:", filename)
        return nil
    end

    -- Iterate through each line in the file
    for line in file:lines() do
        -- Trim whitespace from the line
        line = M.Trim(line)

        -- Skip lines that are comments or empty
        if line:match("^#") or line:match("^;") or line == "" then
            -- Skip comment or empty lines
        else
            -- Check if the line starts a new section
            local s = line:match("^%[([^%]]+)%]$")
            if s then
                section = s
                settings[section] = {}
            else
                -- Handle inline comments (strip out anything after # or ;)
                line = line:match("^[^#;]*"):gsub("%s*$", "")

                -- Parse key-value pairs
                local key, value = line:match("^([%w_]+)%s-=%s-(.+)$")
                if key and value and section then
                    -- Trim any surrounding quotes from values
                    value = value:gsub('^"(.-)"$', '%1')
                    value = M.Trim(tostring(value))
                    settings[section][key] = value
                end
            end
        end
    end

    file:close()
    return settings
end

function M.toboolean(value)
    -- Convert string "true" to true and "false" to false, case insensitive
    local v = tostring(value):lower()  -- Ensure the value is a string and convert to lowercase
    if v == "true" then
        return true
    elseif v == "false" then
        return false
    else
        return nil  -- Return nil if it's neither "true" nor "false"
    end
end


function M.GetIntFromClipboard()
    local clipboard_contents = core.GetTextFromClipboard()

    if not clipboard_contents then
        return nil  -- Return nil if clipboard is empty
    end

    -- Attempt to convert clipboard_contents to an integer
    local number_value = tonumber(clipboard_contents)

    if number_value then
        return number_value
    else
        return nil  -- Return nil if conversion fails
    end
end

function M.SetProcessAsForeground(processName)
    core.Log("Setting proces " .. processName .. " in the foreground", 5)
    core.SetProcessAsForeground(processName)
end

function M.PositionProcessWindowToTopLeft(processName)
    core.Log("PositionProcessWindowToTopLeft", 5)
    core.PositionProcessWindowToTopLeft(processName)
end

function M.AdjustProgramWindow(process_name, width, height)
    core.SetProcessAsForeground(process_name)
    core.PositionProcessWindowToTopLeft(process_name)
end

function M.ResizeWindow(process_name, width, height)
    core.ResizeProcessWindow(process_name, width, height)
end

function M.Sleep(milliseconds)
    core.Sleep(milliseconds)
end

function M.GetCurrentDirectory()
    local handle = io.popen("cd")
    if handle then
        local result = handle:read("*a")
        handle:close()
        if result then
            return result:match("^%s*(.-)%s*$")
        end
    end
    return nil  -- Return nil if unable to get current directory
end

return M
