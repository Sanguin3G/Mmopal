-- tradingpost.lua
local core = require("core")
local util = require("util")

local M = {}

-- Lightweight time-based caches to reduce API calls
local last_transactions_fetch_ts = 0
local last_balance_fetch_ts = 0
local cached_balance_gold = nil

function M.SelfHeal()
    core.Log("Trying to restart client. Will restart in 10 seconds", "red")
    core.Sleep(10000)
    core.TerminateProcessByNameForCurrentUser(EXE_FILE)
    StartTradingPostBot()
end


function M.LoadBlacklistedItems(filepath)
    local csvData = util.ReadFileContents(filepath)
    if not csvData then
        core.Log("Failed to read file: " .. filepath, "red")
        return {}
    end

    local parsedCSV = util.ParseCSV(csvData)
    local blacklist = {}

    -- Assuming the first row is the header and subsequent rows contain the item names
    for i = 1, #parsedCSV do
        local itemName = parsedCSV[i].ItemName  -- Adjust based on how util.ParseCSV returns rows
        if itemName and itemName ~= "" then
            local trimmed = itemName
            if util.Trim then
                trimmed = util.Trim(itemName)
            else
                trimmed = itemName:match("^%s*(.-)%s*$") or itemName
            end
            if trimmed ~= "" then
                local normalized = trimmed:lower()
                local wildcardPattern = normalized:gsub("%*", ".*")
                table.insert(blacklist, {
                    raw = trimmed,
                    normalized = normalized,
                    pattern = "^" .. wildcardPattern .. "$"
                })
            end
        end
    end

    return blacklist
end

-- function M.IsItemBlacklisted(itemName, blacklist)
--     for _, blacklistedItem in ipairs(blacklist) do

--         local str1 = string.lower(blacklistedItem)
--         local str2 = string.lower(itemName)
--         core.Log("Blacklist comparison, str1: " .. str1 .. " and str2: " .. str2, "magenta")
--         local similarityCheck = core.IsSimilar({
--             s1 = str1,
--             s2 = str2,
--             threshold = 0.8
--         })
--         if similarityCheck then
--             core.Log("itemName " .. itemName .. " is blacklisted", "cyan")
--             return true
--         end
--     end
--     return false
-- end

function M.IsItemBlacklisted(itemName, blacklist)
    if not itemName or itemName == "" or not blacklist or #blacklist == 0 then
        return false
    end

    local itemNameLower = itemName:lower()
    for _, entry in ipairs(blacklist) do
        if entry.pattern and string.match(itemNameLower, entry.pattern) then
            core.Log("itemName " .. itemName .. " matches blacklist pattern " .. entry.raw, "cyan")
            return true
        end

        if entry.normalized then
            local similarityCheck = core.IsSimilar({
                s1 = entry.normalized,
                s2 = itemNameLower,
                threshold = 0.8
            })
            if similarityCheck then
                core.Log("itemName " .. itemName .. " is blacklisted due to similarity check", "cyan")
                return true
            end
        end
    end
    return false
end


-- function M.GetTransactions()

--     core.Log("Getting transactions", "magenta")
--     local result = core.GW2_GetCurrentTransactions(API_KEY, PATH_CURRENT_TRANSACTIONS .. ".csv")

--     if result then
--         core.Log("Transactions retrieved successfully and saved to " .. PATH_CURRENT_TRANSACTIONS .. ".csv", "brightmagenta")
--     else
--         core.Log("Something went wrong when getting current transactions", "brightmagenta")
--     end
-- end

-- Function to split a CSV line by a delimiter (e.g., ';')
function split(line, delimiter)
    local result = {}
    for match in (line..delimiter):gmatch("(.-)"..delimiter) do
        table.insert(result, match)
    end
    return result
end

-- Function to read the CSV file into a Lua table, keeping the header separate
function readCSV(filename)
    local file = io.open(filename, "r")
    local header = nil
    local csvData = {}

    if file then
        -- Read the header separately to keep it intact
        header = split(file:read("*l"), ";")

        -- Read the rest of the data rows
        for line in file:lines() do
            table.insert(csvData, split(line, ";"))
        end
        file:close()
    else
        core.Log("Error: Could not open file " .. filename, "red")
    end

    return header, csvData
end

-- Function to sort the table by the 'Created' date (assuming it is the 5th column, index 5)
function sortByDate(csvData)
    table.sort(csvData, function(a, b)
        return a[5] < b[5]  -- Compare the 'Created' column (index 5)
    end)
end

-- Function to write the sorted data back to the CSV file, including the header
function writeCSV(filename, header, csvData)
    local file = io.open(filename, "w")

    if file then
        -- Write the header first
        file:write(table.concat(header, ";") .. "\n")

        -- Write the sorted data rows
        for _, row in ipairs(csvData) do
            file:write(table.concat(row, ";") .. "\n")
        end
        file:close()
    else
        core.Log("Error: Could not open file " .. filename, "red")
    end
end

-- Main function to read, sort, and write the CSV file
function M.SortTransactionsCSV()
    local header, csvData = readCSV(PATH_CURRENT_TRANSACTIONS .. ".csv")

    if header and csvData and #csvData > 0 then
        -- Step 1: Sort the data by the 'Created' column
        sortByDate(csvData)

        -- Step 2: Write the sorted data back to the CSV file, including the header
        writeCSV(PATH_CURRENT_TRANSACTIONS .. ".csv", header, csvData)
        core.Log("CSV sorted by 'Created' field and saved to " .. PATH_CURRENT_TRANSACTIONS .. ".csv", "brightmagenta")
    else
        core.Log("Error: No data found in CSV file", "red")
    end
end

function M.GetTransactions(retries)
    -- Respect a minimal refresh cadence to reduce API calls
    local now = os.time()
    local ttl = (API_TRANSACTIONS_REFRESH_SECONDS or 60)
    if last_transactions_fetch_ts ~= 0 and (now - last_transactions_fetch_ts) < ttl then
        core.Log("Using cached transactions (within TTL)", "magenta")
        -- Optionally keep the CSV sorted for stable downstream parsing
        M.SortTransactionsCSV()
        return true
    end

    core.Log("Getting transactions (API)", "magenta")

    if retries == nil then
        retries = 0
    elseif retries >= 10 then
        core.Log("Tried to get transactions over 10 times, something wrong. Trying to restart game", "red")
        M.SelfHeal()
        return false
    end

    local result, errMsg = core.GW2_GetCurrentTransactions(API_KEY, PATH_CURRENT_TRANSACTIONS .. ".csv")
    if result then
        core.Log("Transactions retrieved successfully and saved to " .. PATH_CURRENT_TRANSACTIONS .. ".csv", "brightmagenta")
        -- Sort the CSV after retrieval
        M.SortTransactionsCSV()
        last_transactions_fetch_ts = now
        return true
    else
        retries = retries + 1
        core.Log("Attempt " .. (retries) .. " failed with error: " .. tostring(errMsg), "magenta")
        core.Log("Waiting 60 seconds before retrying...", "magenta")
        core.Sleep(60000)
        return M.GetTransactions(retries)
    end
end

-- Ensure the current transactions CSV starts clean (header only)
function M.ClearCurrentTransactionsCSV()
    local filename = PATH_CURRENT_TRANSACTIONS .. ".csv"
    local file = io.open(filename, "w")
    if file then
        file:write("Type;ItemName;Price;Quantity;Created;MinSellPrice;MinBuyPrice\n")
        file:close()
        core.Log("Cleared current transactions CSV: " .. filename, "magenta")
        last_transactions_fetch_ts = 0
    else
        core.Log("Failed to clear transactions CSV: " .. filename, "red")
    end
end

function M.GetCurrentBalance(retries)

    -- Respect a minimal refresh cadence to reduce API calls
    local now = os.time()
    local ttl = (API_BALANCE_REFRESH_SECONDS or 120)
    if last_balance_fetch_ts ~= 0 and (now - last_balance_fetch_ts) < ttl and cached_balance_gold ~= nil then
        core.Log("Using cached balance (within TTL)", "magenta")
        return cached_balance_gold
    end

    core.Log("Getting current balance (API)", "magenta")

    if retries == nil then
        retries = 0
    elseif retries >= 10 then
        core.Log("Tried to get current balance over 10 times, something wrong. Trying to restart game", "red")
        M.SelfHeal()
        return false
    end

    local success, goldOrError, silver, copper = core.GW2_GetGoldBalance(API_KEY)
    if success then
        local totalGold = goldOrError + (silver / 100) + (copper / 10000)
        core.Log("Current gold balance: " .. goldOrError, "brightmagenta")
        cached_balance_gold = goldOrError
        last_balance_fetch_ts = now
        return cached_balance_gold
    else
        local errMsg = goldOrError  -- goldOrError contains the error message in this case

        retries = retries + 1
        core.Log("Attempt " .. (retries) .. " failed with error: " .. tostring(errMsg), "magenta")
        core.Log("Waiting 60 seconds before retrying...", "magenta")
        core.Sleep(60000)
        return M.GetCurrentBalance(retries)
    end
end

-- Force next GetTransactions call to fetch from API (ignore TTL)
function M.InvalidateTransactionsCache()
    last_transactions_fetch_ts = 0
end

function M.GetTransactionsForce()
    last_transactions_fetch_ts = 0
    return M.GetTransactions(0)
end

function M.ItemFoundInSellTransactions(itemTable, transactionsTable)
    core.Log("Checking sell transactions for item: " .. itemTable.ItemName, "yellow")

    for _, transaction in ipairs(transactionsTable) do
        if transaction.Type == "Sell" and transaction.ItemName == itemTable.ItemName then
            core.Log("Item " .. itemTable.ItemName .. " is already in sell transactions.", "yellow")

            M.CloseItemWindow()
            M.GoToTransactionsTab()
            M.SelectTransactionsSell()
            M.UpdateItemForSale(itemTable, transaction)

            return true
        end
    end
    core.Log("Item " .. itemTable.ItemName .. " is not in sell transactions.", "yellow")
    return false
end

function M.ItemFoundInBuyTransactions(itemTable, transactionsTable)
    core.Log("Checking sell transactions for item: " .. itemTable.ItemName, "yellow")

    for _, transaction in ipairs(transactionsTable) do
        -- Ensure we only check for buy transactions
        if transaction.Type == "Buy" and transaction.ItemName == itemTable.ItemName then
            core.Log("Item " .. itemTable.ItemName .. " is already in buy transactions", "yellow")
            return true
        end
    end
    core.Log("Item " .. itemTable.ItemName .. " is not in buy transactions.", "yellow")
    return false
end

function M.IsTradingPostOpen()
    core.Log("IsTradingPostOpen", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "tradingpost_tab.png", max_wait_time_ms = 10, click_type = "noclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 400, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        return true
    end
    return false
end

-- Ensure we are logged in and the Trading Post UI is open
function M.EnsureTradingPostReady(retries)
    if retries == nil then retries = 0 end
    if retries > 3 then
        core.Log("Failed to ensure Trading Post is ready after retries", "red")
        M.SelfHeal()
        return false
    end

    M.ErrorBoxCheck()

    if not M.IsTradingPostOpen() then
        core.Log("Trading Post not open; attempting to open/login", "yellow")
        -- Try to bring up the TP quickly first
        M.OpenTradingPost()
        core.Sleep(1000)
    end

    if not M.IsTradingPostOpen() then
        -- Fall back to full login flow which loops until TP is open
        M.Login()
    end

    if not M.IsTradingPostOpen() then
        core.Log("Trading Post still not open; retrying...", "yellow")
        core.Sleep(2000)
        return M.EnsureTradingPostReady(retries + 1)
    end

    return true
end

function M.OpenTradingPost()
    core.Log("OpenTradingPost", "magenta")
    if not M.IsTradingPostOpen() then

        local ingame = core.Image({ image_filepath = SCREENSHOTS .. "ingame_1.png", max_wait_time_ms = 100, click_type = "noclick", mouse_move = true, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
        -- if ingame then
            core.Log("We are ingame", "magenta")
            local status = core.Image({ image_filepath = SCREENSHOTS .. "interact.png", max_wait_time_ms = 20, click_type = "leftclick", mouse_move = true, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
            
            if status then
                core.Log("Pressing Interact", "magenta")
            end
            core.Image({ image_filepath = SCREENSHOTS .. "tradingpost_tab.png", max_wait_time_ms = 20, click_type = "leftclick", mouse_move = true, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
        -- end
    end
    core.Image({ image_filepath = SCREENSHOTS .. "black_lion.png", max_wait_time_ms = 10, click_type = "noclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
end

function M.SelectTransactionsBuy()
    core.Log("SelectTransactionsBuy", "magenta")
    -- Ensure TP is open before interacting with tabs
    if not M.EnsureTradingPostReady() then return end
    core.Image({ image_filepath = SCREENSHOTS .. "transactions.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    core.Sleep(DELAY)
    M.ResetFilters()
    core.Image({ image_filepath = SCREENSHOTS .. "transactions_current.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 3000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    core.Sleep(DELAY)
    core.Image({ image_filepath = SCREENSHOTS .. "transactions_buying.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 3000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    core.Sleep(DELAY)
end

function M.ChooseCurrentTransactionBuy()
    core.Log("ChooseBuying", "magenta")
    core.Image({ image_filepath = SCREENSHOTS .. "transactions_buying.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 3000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
end


function M.AtTransactionsBuying()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "at_transactions_buying.png", max_wait_time_ms = 3, click_type = "noclick", mouse_move = false, match_score = 0.98, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("At buy transactions", "magenta")
        return true
    end
end


function M.ResetSearchField()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "cog.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.95, capture_x = 0, capture_y = 0, capture_width = 320, capture_height = 460, wait_before = 0, wait_after = 500, offset_x = -100, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("Clicked search field", "magenta")
        core.KeyPress("CtrlA")
        core.Sleep(250)
        core.KeyPress("Backspace")
    else
        core.Log("Could not find cog", "magenta", "red")
        M.SelfHeal()
    end
end

function M.GoToTransactionsTab()
    -- Ensure TP is open first
    if not M.EnsureTradingPostReady() then
        core.Log("Could not ensure Trading Post readiness", "red")
        return false
    end

    -- Try clicking the transactions tab with a few retries
    for i = 1, 3 do
        local status = core.Image({ image_filepath = SCREENSHOTS .. "transactions.png", max_wait_time_ms = 200, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 200, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
        if status then
            core.Log("Went to transaction tab", "magenta")
            return true
        end
        core.Sleep(500)
    end

    core.Log("Could not go to transactions tab after retries", "red")
    core.Sleep(2000)
    return false
end

function M.TransactionsTabChooseCurrent()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "transactions_current.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.95, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 3000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("Unfolded current transactions", "brightmagenta")
        return true
    end

    core.Log("Already unfolded current transactions", "brightmagenta")
    core.Sleep(5000)
    return false
end

function M.SelectTransactionsSell()
    core.Log("SelectTransactionsSell", "magenta")
    -- Ensure TP is open before interacting with tabs
    if not M.EnsureTradingPostReady() then return end
    core.Image({ image_filepath = SCREENSHOTS .. "transactions.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    core.Sleep(DELAY)
    M.ResetFilters()
    core.Image({ image_filepath = SCREENSHOTS .. "transactions_current.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 3000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    core.Sleep(DELAY)
    core.Image({ image_filepath = SCREENSHOTS .. "transactions_selling.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 3000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    core.Sleep(DELAY)
end

function M.ChooseCurrentTransactionSelling()
    core.Log("ChooseSelling", "magenta")
    core.Image({ image_filepath = SCREENSHOTS .. "transactions_selling.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 3000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
end

function M.ReOpenTP()
    M.CloseItemWindow()
    M.CloseAllWindows()
    M.OpenTradingPost()
    M.TakeItems()
end

function M.ResetFilters(retries)
    core.Log("ResetFilters", "magenta")

    if retries == nil then
        retries = 0
    elseif retries >= 20 then
        core.Log("Tried to reset filters more than 20 times. Will restart", "red")
        M.SelfHeal()
    end

    M.ErrorBoxCheck()

    local resetA = core.Image({ image_filepath = SCREENSHOTS .. "reset_filter_A.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 900, capture_height = 300, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    local resetB = core.Image({ image_filepath = SCREENSHOTS .. "reset_filter_B.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 900, capture_height = 300, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })

    if resetA or resetB then
        retries = retries + 1
        return M.ResetFilters(retries)
    end
end

function M.GoToRestPosition()
    core.Log("GoToRestPosition", "magenta")
    core.Image({ image_filepath = SCREENSHOTS .. "black_lion.png", max_wait_time_ms = 5, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    core.Image({ image_filepath = SCREENSHOTS .. "rest_position.png", max_wait_time_ms = 5, click_type = "doubleleftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
end

function M.TakeItems()
    core.Log("TakeItems", "magenta")
    core.Image({ image_filepath = SCREENSHOTS .. "take_all.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = true, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
end

local function startsWith(str, prefix)
    return string.sub(str, 1, #prefix):lower() == prefix:lower()
end

function M.Login()
    core.Log("Logging in to Guild Wars 2", "magenta")

    if not core.IsProcessRunningForCurrentUser(EXE_FILE) then
        if startsWith(EXE_FILEPATH, "steam") then
            core.Log("Opening through steam: " .. EXE_FILEPATH, 5)
            os.execute('start "" "' .. EXE_FILEPATH .. '"')
        else
            core.Log("Opening exe: " .. EXE_FILEPATH, 5)
            core.OpenProgram(EXE_FILEPATH, "-windowed")
        end
    else
        core.Log(EXE_FILE .. " is running", "magenta")
        util.AdjustProgramWindow(PROCESS_NAME, WINDOW_SIZE.width, WINDOW_SIZE.height)

        core.Image({ image_filepath = SCREENSHOTS .. "login_launcher.png", max_wait_time_ms = 1, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1920, capture_height = 1080, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
        core.Image({ image_filepath = SCREENSHOTS .. "accept_agreement.png", max_wait_time_ms = 1, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1920, capture_height = 1080, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
        core.Image({ image_filepath = SCREENSHOTS .. "remind_me_later.png", max_wait_time_ms = 1, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1920, capture_height = 1080, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })

        local play = core.Image({ image_filepath = SCREENSHOTS .. "play.png", max_wait_time_ms = 1, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1920, capture_height = 1080, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })

        core.KeyPress("Enter")

        M.OpenTradingPost()
    end

    core.Sleep(5000)

    if not M.IsTradingPostOpen() then
        return M.Login()
    end
end

function M.OpenCog()
    core.Log("Open Cog", "magenta")
    local found = core.Image({ image_filepath = SCREENSHOTS .. "cog.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 320, capture_height = 460, wait_before = 0, wait_after = 1000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if found then
        core.Log("Opened cog", "magenta")
    else
        core.Log("Could not open cog", "magenta", "red")
        M.SelfHeal()
    end
end

function M.ClickRarityDropdown()
    core.Log("Open rarity dropdown", "magenta")
    local found = core.Image({ image_filepath = SCREENSHOTS .. "open_rarity.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 320, capture_height = 460, wait_before = 0, wait_after = 1000, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if found then
        core.Log("Opened rarity dropdown", "magenta")
    else
        core.Log("Could not open cog", "magenta", "red")
        M.SelfHeal()
    end
end


function M.OpenRarityTab()
    core.Log("OpenRarityTab", "magenta")
    M.OpenCog()
    M.ClickRarityDropdown()
end

function M.ChooseRarity(rarity)
    core.Log("ChooseRarity", "magenta")
    -- core.Sleep(1000)
    local rarityMap = {
        allrarities = "rarity_all.png",
        basic = "rarity_basic.png",
        white = "rarity_basic.png",
        fine = "rarity_fine.png",
        blue = "rarity_fine.png",
        masterwork = "rarity_masterwork.png",
        green = "rarity_masterwork.png",
        rare = "rarity_rare.png",
        yellow = "rarity_rare.png",
        exotic = "rarity_exotic.png",
        orange = "rarity_exotic.png",
        ascended = "rarity_ascended.png",
        pink = "rarity_ascended.png",
        legendary = "rarity_legendary.png",
        purple = "rarity_legendary.png",
    }

    local lowercaseRarity = string.lower(rarity)
    local imageFile = rarityMap[lowercaseRarity] or "rarity_all.png"

    core.Image({ image_filepath = SCREENSHOTS .. imageFile, max_wait_time_ms = 50, click_type = "leftclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 320, capture_height = 460, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
end

function M.FocusSearchField()
    core.Log("FocusSearchField", "magenta")
    local foundSearchField = core.Image({ image_filepath = SCREENSHOTS .. "search.png", max_wait_time_ms = 50, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 320, capture_height = 460, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if foundSearchField then
        local statusFocus = core.Image({ image_filepath = SCREENSHOTS .. "search_focused.png", max_wait_time_ms = 50, click_type = "noclick", mouse_move = false, match_score = 0.75, capture_x = 0, capture_y = 0, capture_width = 320, capture_height = 460, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
        return true
    else
        return false
    end
end

function M.NoItemsFound()
    core.Log("Checking if items were found or not", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "no_items.png", max_wait_time_ms = 25, click_type = "noclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        return true
    else
        return false
    end
end

function M.SelectBuyTab()
    core.Log("SelectBuyTab", "magenta")

    local buytab_open = core.Image({ image_filepath = SCREENSHOTS .. "buy_items_tab.png", max_wait_time_ms = 50, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 400, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if buytab_open then
        core.Log("Item tab is open", "magenta")
    else
        M.SelfHeal()
    end
end

function M.FocusFirstItem()
    core.Log("FocusFirstItem", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "item.png", max_wait_time_ms = 50, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 1000, wait_after = 0, offset_x = 0, offset_y = 30, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Focused first item", "magenta")
    else
        core.Log("Could not focus first item!", "red")
        M.SelfHeal()
    end
end

function M.ItemWindowOpen()

    core.Log("ItemWindowOpen", "magenta")

    M.ErrorBoxCheck()

    local status = core.Image({ image_filepath = SCREENSHOTS .. "currentBuyers.png", max_wait_time_ms = 200, click_type = "noclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if (status) then
        core.Log("ItemWindowOpen found", "magenta")
        return true
    else
        core.Log("ItemWindowOpen wasn't found", "magenta")
        return false
    end
end

function M.CloseAllWindows(retries)
    core.Log("CloseAllWindows", "magenta")

    if retries == nil then
        retries = 0
    elseif retries >= 20 then
        core.Log("Tried to close windows more than 20 times, resetting", "magenta")
        M.SelfHeal()
    end

    local status = core.Image({ image_filepath = SCREENSHOTS .. "exit_window.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 400, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if (status) then
        retries = retries + 1
        return M.CloseAllWindows(retries)
    end
end

function M.CloseItemWindow(retries)
    core.Log("CloseItemWindow", "magenta")

    if retries == nil then
        retries = 0
    elseif retries >= 10 then
        core.Log("Something went wrong within CloseItemWindow, > 10 retries. Restart game", "red")
        M.SelfHeal()
    end

    local status = core.Image({ image_filepath = SCREENSHOTS .. "exit_item_window.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 800, capture_height = 400, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if (status) then
        retries = retries + 1
        return M.CloseItemWindow(retries)
    end
end

function M.FocusSellTab()
    core.Log("FocusSellTab", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "items_tab.png", max_wait_time_ms = 50, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 655, capture_y = 0, capture_width = WINDOW_SIZE.width - 655, capture_height = 300, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Found selltab", "magenta")
    else
        core.Log("Did not find sell tab", "red")
        M.SelfHeal()
    end
end

function M.ItemSelectQuantity(quantity)

    if quantity == nil then
        core.Log("quantity is nil, setting to 1 to avoid program from breaking", "red")
        quantity = 1
        core.Sleep(15000)
    end

    if M.ItemWindowOpen() then
        local status =  core.Image({ image_filepath = SCREENSHOTS .. "quantity.png", max_wait_time_ms = 40, click_type = "leftclick", mouse_move = true, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 112, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })

        if status then
            core.Sleep(250)
            core.KeyPress("CtrlA")
            core.Sleep(250)
            core.KeyPress("Backspace")
            core.Log("Sending string " .. tostring(quantity), "blue")
            core.CopyTextToClipboard(tostring(quantity))
            core.KeyPress("CtrlV")
        end
    end
end

function M.ToCurrency(copper)
    -- Check for negative input
    if copper < 0 then
        error("Invalid input: Copper amount cannot be negative")
    end

    -- Calculate gold, silver, and copper amounts
    local gold = math.floor(copper / 10000)
    local silver = math.floor((copper % 10000) / 100)
    local copper_remainder = copper % 100

    -- Ensure values are within valid ranges
    gold = math.max(0, math.min(9999, gold))
    silver = math.max(0, math.min(99, silver))
    copper_remainder = math.max(0, math.min(99, copper_remainder))

    return gold, silver, copper_remainder
end

function M.ToCopper(gold, silver, copper)
    if gold == nil or silver == nil or copper == nil then
        return nil
    elseif gold < 0 or gold > 9999 or silver < 0 or silver > 99 or copper < 0 or copper > 99 then
        return nil
    end

    local total_copper = gold * 10000 + silver * 100 + copper

    return total_copper
end

function M.GetPrice()

    core.Log("GetPrice", "magenta")
    local copper_point = core.GetCoordinates({ name = SCREENSHOTS .. "price_2.png", outname = DEBUG_PATH .. "copper_point_upper.png", debug = false, max_wait_time_ms = 300, offset_x = 252, offset_y = -11, capture_width = 1024, capture_height = 768, match_score = 0.75, print_match_score = true })

    local prices = {}

    -- Retrieve copper price
    core.ClickAtPosition({ X = copper_point.x, Y = copper_point.y, clickType = "doubleleftclick", waitBefore = 100, waitAfter = 100, speedDelay = 0 })
    core.Sleep(50)
    core.KeyPress("CtrlA")
    core.KeyPress("CtrlC")
    prices.copper = util.GetIntFromClipboard()

    -- Retrieve silver price
    core.ClickAtPosition({ X = copper_point.x - 80, Y = copper_point.y, clickType = "doubleleftclick", waitBefore = 100, waitAfter = 100, speedDelay = 0 })
    core.Sleep(50)
    core.KeyPress("CtrlA")
    core.KeyPress("CtrlC")
    prices.silver = util.GetIntFromClipboard()

    -- Retrieve gold price
    core.ClickAtPosition({ X = copper_point.x - 185, Y = copper_point.y, clickType = "doubleleftclick", waitBefore = 100, waitAfter = 100, speedDelay = 0 })
    core.Sleep(50)
    core.KeyPress("CtrlA")
    core.KeyPress("CtrlC")
    prices.gold = util.GetIntFromClipboard()
    -- Convert prices to total copper value
    prices.total_copper = M.ToCopper(prices.gold, prices.silver, prices.copper)

    if prices.total_copper == nil or prices.copper == nil or prices.silver == nil or prices.gold == nil then
        core.Log("Something went wrong with GetPrice, trying to restart in 20 seconds...", "red")
        core.Sleep(20000)
        M.SelfHeal()
    end

    return prices
end

function M.CalculateDifference(buy_price, sell_price)

    core.Log("=============", "magenta")
    core.Log("buy_price:  " .. tostring(buy_price), "magenta")
    core.Log("sell_price: " .. tostring(sell_price), "magenta")

    -- Calculate the Listing Fee (5% of the Sell Price)
    local listing_fee = math.floor(sell_price * 0.05 + 0.5)  -- rounding to nearest copper
    core.Log("listing_fee: " .. tostring(listing_fee), "magenta")

    -- Calculate the Exchange Fee (10% of the Sell Price)
    local exchange_fee = math.floor(sell_price * 0.10 + 0.5)  -- rounding to nearest copper
    core.Log("exchange_fee: " .. tostring(exchange_fee), "magenta")
    -- Calculate the Net Amount Received after fees
    local net_amount_received = sell_price - listing_fee - exchange_fee
    core.Log("net_amount_received: " .. tostring(net_amount_received), "magenta")
    -- Calculate the Profit
    local profit = net_amount_received - buy_price
    core.Log("profit: " .. tostring(profit), "magenta")

    -- Calculate the Profit Percentage
    local profit_percentage = (profit / buy_price) * 100
    core.Log("profit_percentage: " .. tostring(profit_percentage), "magenta")

    core.Log("=============", "magenta")

    return profit_percentage
end

function M.ItemGetBuyOrderPrice()
    core.Log("ItemGetBuyOrderPrice", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "currentBuyers.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 50, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if not status then
        core.Log("Didn't find ItemGetBuyOrderPrice", "red")
        M.SelfHeal()
    end
end

function M.LoadMore()
    core.Log("Load More", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "load_more.png", max_wait_time_ms = 3, click_type = "leftclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Pressed Load More", "magenta")
        return true
    end
    return false
end

function M.ItemGetSellOrderPrice()
    core.Log("ItemGetBuyOrderPrice", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "currentSellers.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.95, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 2000, wait_after = 0, offset_x = 0, offset_y = 50, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if not status then
        core.Log("Didn't find ItemGetSellOrderPrice", "red")
        M.SelfHeal()
    end
end

function M.SetOneCopperMore()
    core.Log("SetOneCopperMore", "magenta")
    -- core.Sleep(5000)
    local status = core.Image({ image_filepath = SCREENSHOTS .. "arrow_up.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = true, match_score = 0.90, capture_x = 550, capture_y = 0, capture_width = WINDOW_SIZE.width - 550, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("Increase copper with 1", "magenta")
        -- core.Sleep(5000000000)
    else
        core.Log("Could not increase copper with 1", "red")
    end
    -- core.Sleep(10000)
end

-- Increase buyer price by a configurable copper buffer (>=1)
function M.SetCopperMore(n)
    n = tonumber(n) or 1
    if n <= 1 then
        return M.SetOneCopperMore()
    end
    core.Log("SetCopperMore: +" .. tostring(n) .. "c", "magenta")
    for i = 1, n do
        local status = core.Image({ image_filepath = SCREENSHOTS .. "arrow_up.png", max_wait_time_ms = 50, click_type = "leftclick", mouse_move = true, match_score = 0.90, capture_x = 550, capture_y = 0, capture_width = WINDOW_SIZE.width - 550, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
        if not status then
            core.Log("Failed to increase copper on step " .. tostring(i), "red")
            break
        end
    end
end

-- Decrease seller price by a configurable copper buffer (>=1)
function M.SetCopperLess(n)
    n = tonumber(n) or 1
    if n <= 1 then
        return M.SetOneCopperLess()
    end
    core.Log("SetCopperLess: -" .. tostring(n) .. "c", "magenta")
    for i = 1, n do
        local status = core.Image({ image_filepath = SCREENSHOTS .. "arrow_down.png", max_wait_time_ms = 50, click_type = "leftclick", mouse_move = false, match_score = 0.90, capture_x = 550, capture_y = 0, capture_width = WINDOW_SIZE.width - 550, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
        if not status then
            core.Log("Failed to decrease copper on step " .. tostring(i), "red")
            break
        end
    end
end

-- Random helper (falls back to 1 if inputs invalid)
function M.RandomCopper(minCopper, maxCopper)
    local minv = tonumber(minCopper) or 1
    local maxv = tonumber(maxCopper) or minv
    if maxv < minv then maxv = minv end
    if minv < 1 then minv = 1 end
    return math.random(minv, maxv)
end

-- Apply a randomized overbid buffer
function M.ApplyOverbidBuffer()
    local step = M.RandomCopper(OVERBID_BUFFER_MIN_COPPER or 1, OVERBID_BUFFER_MAX_COPPER or 1)
    M.SetCopperMore(step)
end

-- Apply a randomized undercut buffer, revert if minimum unit price hit
function M.ApplyUndercutBuffer()
    local step = M.RandomCopper(UNDERCUT_BUFFER_MIN_COPPER or 1, UNDERCUT_BUFFER_MAX_COPPER or 1)
    M.SetCopperLess(step)
    if M.MinimumUnitPriceReached and M.MinimumUnitPriceReached() then
        core.Log("Minimum unit price reached while undercutting, reverting change", "yellow")
        M.SetCopperMore(step)
    end
end

function M.SetOneCopperLess()
    core.Log("SetOneCopperLess", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "arrow_down.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.90, capture_x = 550, capture_y = 0, capture_width = WINDOW_SIZE.width - 550, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false, show_cursor = false })
    if status then
        core.Log("Decrease copper with 1", "magenta")
    else
        core.Log("Could not decrease copper with 1", "red")
    end
    -- core.Sleep(10000)
end

function M.PlaceOrder()
    core.Log("PlaceOrder", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "placeOrder.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 250, wait_after = 250, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Placed order", "magenta")
    else
        core.Log("Could not find place order button", "red")
    end
    -- core.Sleep(10000)
end

function M.CloseStatusBox()
    core.Log("CloseStatusBox", "magenta")
    -- local successMessage = core.Image({ image_filepath = SCREENSHOTS .. "success.png", max_wait_time_ms = 20, click_type = "noclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    -- if successMessage then
    --     local closeButton = core.Image({ image_filepath = SCREENSHOTS .. "close.png", max_wait_time_ms = 20, click_type = "leftclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    -- else
        M.CloseItemWindow()
    -- end
end


function M.ErrorBoxCheck()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "ok.png", max_wait_time_ms = 3, click_type = "noclick", mouse_move = false, match_score = 0.90, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 50, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Error box found, restart client", "red")
        M.SelfHeal()
    end
end

function M.MinimumUnitPriceReached()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "minimum_unit_price_reached.png", max_wait_time_ms = 10, click_type = "noclick", mouse_move = false, match_score = 0.90, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Minimum unit price reached", "red")
        return true
    end
    return false
end

function M.CompareLastDigits(copperPrice1, copperPrice2)
    -- Convert the input numbers to strings
    local str1 = tostring(copperPrice1)
    local str2 = tostring(copperPrice2)

    -- Get the last digits of the strings
    local lastDigit1 = tonumber(str1:sub(-1))
    local lastDigit2 = tonumber(str2:sub(-1))

    -- Compare the last digits
    if lastDigit1 ~= lastDigit2 then
        core.Log("The last digits are DIFFERENT: " .. lastDigit1 .. " and " .. lastDigit2, "blue")
        return true
    else
        core.Log("The last digits are the SAME: " .. lastDigit1 .. " and " .. lastDigit2, "blue")
        return false
    end
end

function M.ListItemForSale()
    core.Log("PlaceOrder", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "ListThisItem.png", max_wait_time_ms = 100, click_type = "leftclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
end

function M.SellInstantly()
    local status = core.Image({ image_filepath = SCREENSHOTS .. "sell_instantly.png", max_wait_time_ms = 3, click_type = "leftclick", mouse_move = false, match_score = 0.7, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Had to sell instantly", "red")
        core.Sleep(10000)
    end
end

function M.ChooseMaxQuantity()
    core.Log("Trying to choose max quantity", "magenta")
    local status = core.Image({ image_filepath = SCREENSHOTS .. "choose_max_quantity.png", max_wait_time_ms = 3, click_type = "leftclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status then
        core.Log("Max quantity chosen", "magenta")
    end

    local status_sell = core.Image({ image_filepath = SCREENSHOTS .. "choose_max_quantity_sell.png", max_wait_time_ms = 3, click_type = "leftclick", mouse_move = false, match_score = 0.9, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 768, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if status_sell then
        core.Log("Max quantity chosen, selling", "magenta")
    end
end

----------------------------------------------------------------------------------------------------------------------------------------------
-- WRAPPER FUNCTIONS
----------------------------------------------------------------------------------------------------------------------------------------------

function M.CheckBuyTransaction()
    core.Log("Checking buy transactions, assuming we are already inside transactionstab with item info sat...", "magenta")
    M.GoToRestPosition()
    M.OpenCog()
    M.SelectTransactionsBuy()
    core.Sleep(2000);
end

function M.CheckSellTransaction(itemTable)
    core.Log("Checking sell transactions for item: " .. itemTable.ItemName, "yellow")

    M.CloseItemWindow()
    M.ResetFilters()
    M.TakeItems()
    M.SelectTransactionsSell()
    M.OpenRarityTab()
    M.ChooseRarity(util.Trim(itemTable.Rarity))
    if (M.FocusSearchField()) then
        core.SendString(util.Trim(itemTable.ItemName));
        core.Sleep(DELAY);
    end
    core.Sleep(2000);
end

function M.CancelOldItemBids()
    core.Log("CancelOldItemBids...", "magenta")
    M.GoToRestPosition()

    local cancelButtonFoundClicked = core.Image({ image_filepath = SCREENSHOTS .. "transactions_cancel.png", max_wait_time_ms = 50, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 325, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if cancelButtonFoundClicked then
        M.GoToRestPosition()

        local confirmBtnClicked = core.Image({ image_filepath = SCREENSHOTS .. "transactions_confirm.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 325, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
        if confirmBtnClicked then
            return M.CancelOldItemBids()
        end
    end
end

function M.CancelFirstItem()
    core.Log("CancelFirstItem", "magenta")
    M.GoToRestPosition()
    M.CloseItemWindow()

    local click_cancel_button = core.Image({ image_filepath = SCREENSHOTS .. "cancel.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 400, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if click_cancel_button then
        core.Log("Clicked cancel button", "magenta")
        M.GoToRestPosition()
        local click_confirm_button = core.Image({ image_filepath = SCREENSHOTS .. "confirm.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.8, capture_x = 0, capture_y = 0, capture_width = 1024, capture_height = 400, wait_before = 0, wait_after = 500, offset_x = 0, offset_y = 0, print_match_score = false, debug = false, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
        if click_confirm_button then
            core.Log("Clicked confirm button", "magenta")
        else
            core.Log("Could not find confirm button", "red")
        end
    else
        core.Log("Could not find cancel button", "red")
    end
end

function M.BuyItem(itemTable, blacklistedItems)
    core.Log("Buying item: " .. itemTable.ItemName, "yellow")

    if M.IsItemBlacklisted(itemTable.ItemName, blacklistedItems) then
        core.Log("Item " .. itemTable.ItemName .. " is blacklisted and will be skipped.", "cyan")

        M.GoToRestPosition()
        M.CloseItemWindow()

        M.TakeItems()
        M.GoToRestPosition()
    else
        M.GoToRestPosition()
        M.CloseItemWindow()

        M.TakeItems()
        M.GoToRestPosition()

        M.SelectBuyTab()
        M.ResetFilters()
        M.OpenRarityTab()
        M.ChooseRarity(util.Trim(itemTable.Rarity))

        M.GoToRestPosition()
        if (M.FocusSearchField()) then
            core.SendString(util.Trim(itemTable.ItemName));
            if SLOW_INTERNET then
                core.Sleep(5000)
            end
        end

        M.GoToRestPosition()
        M.FocusFirstItem()
        if M.ItemWindowOpen() then
            if VALIDATE_CURRENT_TRANSACTION_PRICES then 

                core.Log("Getting sellPrice", "magenta")
                local sellPrice = M.GetPrice()

                core.Log("Getting ItemGetBuyOrderPrice", "magenta")
                M.ItemGetBuyOrderPrice()

                core.Log("Getting buyPrice", "magenta")
                local buyPrice = M.GetPrice()
                core.Log("Total buyPrice copper: " .. tostring(buyPrice.total_copper), "magenta")
                core.Log("Gold:   " .. tostring(buyPrice.gold), "magenta")
                core.Log("Silver: " .. tostring(buyPrice.silver), "magenta")
                core.Log("Copper: " .. tostring(buyPrice.copper), "magenta")

                local actual_profit = M.CalculateDifference(buyPrice.total_copper, sellPrice.total_copper)
                core.Log("actual_profit: " .. tostring(actual_profit), "magenta")

                if actual_profit > PROFIT_THRESHOLD then
                    local fullItemPrice = ((buyPrice.total_copper/10000) * CUSTOM_QUANTITY)
                    if fullItemPrice > MAX_PRICE_PER_ITEM then
                        core.Log("Actual buyprice: " .. tostring(fullItemPrice) .. "g exceeds MAX_PRICE_PER_ITEM: " .. tostring(MAX_PRICE_PER_ITEM) .. "g", "brightmagenta")
                    else
                        core.Log("Website shoppinglist, profitable! Buy it!", "green")

                        if itemTable ~= nil and itemTable.Quantity ~= nil then
                            local roundedQuantity = math.floor(itemTable.Quantity)  -- Round down the quantity

                            core.Log("Using GW2Profits quantity amount: " .. roundedQuantity, "yellow")

                            if roundedQuantity < 1 then
                                roundedQuantity = 1  -- Ensure the quantity is at least 1
                            elseif roundedQuantity > 250 then
                                roundedQuantity = 250
                            end

                            M.ItemSelectQuantity(roundedQuantity)
                        else
                            core.Log("Using default quantity amount: " .. CUSTOM_QUANTITY, "yellow")
                            M.ItemSelectQuantity(CUSTOM_QUANTITY)
                        end

                        if OVERBID_BUY_LISTINGS then
                            M.ApplyOverbidBuffer()
                        end

                        M.PlaceOrder()
                        M.CloseStatusBox()
                    end
                else
                    core.Log("Not profitable", "magenta")
                end
            else
                core.Log("Not going to validate, trusting API", "magenta")
                M.ItemGetBuyOrderPrice()

                -- TODO: Add profit check from API (shoppinglist -> Buy (needs to be converted to copper))
                -- TODO: Add MAX_PRICE_PER_ITEM check
                -- TODO: Add quantity check
                

                         
                core.Log("Using default quantity amount: " .. CUSTOM_QUANTITY, "yellow")
                M.ItemSelectQuantity(CUSTOM_QUANTITY)
                      
                if OVERBID_BUY_LISTINGS then
                    M.ApplyOverbidBuffer()
                end

                M.PlaceOrder()
                M.CloseStatusBox()
            end
        end
    end

    M.GoToRestPosition()
    M.CloseItemWindow()
end

function M.UpdateOrderedItem(itemTable, transaction)
    core.Log("Updating item " .. itemTable.ItemName .. " in transactions, will relist...", "yellow")
    M.GoToRestPosition()
    M.CloseItemWindow()

    M.LoadMore()
    M.FocusFirstItem()

    if M.ItemWindowOpen() then

        if VALIDATE_CURRENT_TRANSACTION_PRICES then
            core.Log("We are validating current transaction prices", "magenta")

            local sellPrice = M.GetPrice()

            M.ItemGetBuyOrderPrice()

            local buyPrice = M.GetPrice()
            core.Log("Total buyPrice copper: " .. tostring(buyPrice.total_copper), "magenta")
            core.Log("Gold:   " .. tostring(buyPrice.gold), "magenta")
            core.Log("Silver: " .. tostring(buyPrice.silver), "magenta")
            core.Log("Copper: " .. tostring(buyPrice.copper), "magenta")

            local minBuyPrice_web = transaction.MinBuyPrice
            local minSellPrice_web = transaction.MinSellPrice
            local total_copper = buyPrice.total_copper

            core.Log("minBuyPrice_web: " .. tostring(minBuyPrice_web), "cyan")
            core.Log("minSellPrice_web: " .. tostring(minSellPrice_web), "cyan")
            core.Log("total_copper: " .. tostring(total_copper), "cyan")

            if tonumber(minBuyPrice_web) ~= tonumber(total_copper) then
                core.Log("Actual price and API is not aligned!", "red")
                core.Sleep(10000)
            end

            local profit_percentage_dynamic = M.CalculateDifference(buyPrice.total_copper, sellPrice.total_copper)
            core.Log("profit_percentage_dynamic: " .. tostring(profit_percentage_dynamic), "magenta")

            if itemTable.MinimumProfit ~= nil and profit_percentage_dynamic > itemTable.MinimumProfit then

                local fullItemPrice = ((buyPrice.total_copper/10000) * itemTable.Quantity)
                if fullItemPrice > MAX_PRICE_PER_ITEM then
                    core.Log("Actual buyprice: " .. tostring(fullItemPrice) .. "g exceeds MAX_PRICE_PER_ITEM: " .. tostring(MAX_PRICE_PER_ITEM) .. "g", "brightmagenta")
                else
                    core.Log("Customlist, profitable! Buy it!", "green")
                    M.ItemSelectQuantity(itemTable.Quantity);
                    if OVERBID_BUY_LISTINGS then
                        M.ApplyOverbidBuffer()
                    end
                    M.PlaceOrder()
                    M.CloseStatusBox()
                    M.CancelOldItemBids()
                end

            elseif profit_percentage_dynamic > PROFIT_THRESHOLD then

                local fullItemPrice = ((buyPrice.total_copper/10000) * transaction.Quantity)
                if fullItemPrice > MAX_PRICE_PER_ITEM then
                    core.Log("Actual buyprice: " .. tostring(fullItemPrice) .. "g exceeds MAX_PRICE_PER_ITEM: " .. tostring(MAX_PRICE_PER_ITEM) .. "g", "brightmagenta")
                else
                    core.Log("Website shoppinglist, profitable! Buy it!", "green")

                    local roundedQuantity = math.floor(transaction.Quantity)  -- Round down the quantity

                    core.Log("Using current transaction quantity amount: " .. roundedQuantity, "yellow")

                    if roundedQuantity < 1 then
                        roundedQuantity = 1  -- Ensure the quantity is at least 1
                    elseif roundedQuantity > 250 then
                        roundedQuantity = 250
                    end

                    M.ItemSelectQuantity(roundedQuantity)

                    if OVERBID_BUY_LISTINGS then
                        M.ApplyOverbidBuffer()
                    end

                    M.PlaceOrder()
                    M.CloseStatusBox()
                    M.CancelOldItemBids()
                end
            else
                core.Log("Not profitable", "magenta")
                M.CloseItemWindow()
                M.CancelOldItemBids()
            end
        else
            core.Log("We are not validating current transaction prices", "magenta")

            M.ItemGetBuyOrderPrice()

            local profit_percentage_dynamic = M.CalculateDifference(transaction.MinBuyPrice, transaction.MinSellPrice)
            core.Log("profit_percentage_dynamic: " .. tostring(profit_percentage_dynamic), "magenta")

            if itemTable.MinimumProfit ~= nil and profit_percentage_dynamic > itemTable.MinimumProfit then

                local fullItemPrice = ((transaction.MinBuyPrice/10000) * itemTable.Quantity)
                if fullItemPrice > MAX_PRICE_PER_ITEM then
                    core.Log("Actual buyprice: " .. tostring(fullItemPrice) .. "g exceeds MAX_PRICE_PER_ITEM: " .. tostring(MAX_PRICE_PER_ITEM) .. "g", "brightmagenta")
                else
                    core.Log("Customlist, profitable! Buy it!", "green")
                    M.ItemSelectQuantity(itemTable.Quantity);
                    if OVERBID_BUY_LISTINGS then
                        M.ApplyOverbidBuffer()
                    end
                    M.PlaceOrder()
                    M.CloseStatusBox()
                    M.CancelOldItemBids()
                end

            elseif profit_percentage_dynamic > PROFIT_THRESHOLD then

                local fullItemPrice = ((transaction.MinBuyPrice/10000) * transaction.Quantity)
                if fullItemPrice > MAX_PRICE_PER_ITEM then
                    core.Log("Actual buyprice: " .. tostring(fullItemPrice) .. "g exceeds MAX_PRICE_PER_ITEM: " .. tostring(MAX_PRICE_PER_ITEM) .. "g", "brightmagenta")
                else
                    core.Log("Website shoppinglist, profitable! Buy it!", "green")


                    local roundedQuantity = math.floor(transaction.Quantity)  -- Round down the quantity

                    core.Log("Using current transaction quantity amount: " .. roundedQuantity, "yellow")

                    if roundedQuantity < 1 then
                        roundedQuantity = 1  -- Ensure the quantity is at least 1
                    elseif roundedQuantity > 250 then
                        roundedQuantity = 250
                    end

                    M.ItemSelectQuantity(roundedQuantity)

                    if OVERBID_BUY_LISTINGS then
                        M.ApplyOverbidBuffer()
                    end

                    M.PlaceOrder()
                    M.CloseStatusBox()
                    M.CancelOldItemBids()
                end
            else
                core.Log("Not profitable", "magenta")
                M.CloseItemWindow()
                M.CancelOldItemBids()
            end
        end
    else
        core.Log("Not going to relist", "brightmagenta")
    end

    M.GoToRestPosition()
    M.CloseItemWindow()
end

-- Variant that assumes the caller has already filtered the transactions list
-- by the specific item (via search field) and is on the Buy tab.
-- Avoids resetting UI state, and bails out if the item is no longer present.
-- (Reverted) Removed UpdateOrderedItemFromSearch; use UpdateOrderedItem

function M.GetLastDigitOfFirstItem()
    core.Log("Getting last digit from first item", "magenta")

    local coords_before = core.GetCoordinates({ name = SCREENSHOTS .. "last_digit_detection.png", outname = DEBUG_PATH .. "last_digit_detection_debug_before.png", debug = false, max_wait_time_ms = 5, offset_x = 36, offset_y = 30, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, match_score = 0.95, print_match_score = false })
    local coords_after = core.GetCoordinates({ name = SCREENSHOTS .. "last_digit_detection.png", outname = DEBUG_PATH .. "last_digit_detection_debug_after.png", debug = false, max_wait_time_ms = 5, offset_x = 48, offset_y = 60, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, match_score = 0.95, print_match_score = false })

    local result = core.PerformOCRAndParse({x1 = coords_before.x, y1 = coords_before.y, x2 = coords_after.x, y2 = coords_after.y, preprocessOption = 6 })
    if result then
        local number = result.number

        if number == nil then
            core.Log("Error: OCR did not return valid number", "red")
            return -1
        end

        number = number:gsub(",", "")
        if number:match("^%d$") then
            return tonumber(number)
        else
            return -1
        end
    else
        core.Log("Error: OCR function did not return a result", "red")
        return -1
    end
end

function M.SortByOldestDate()
    core.Log("Sorting by oldest date", "magenta")

    local no_date_set = core.Image({ image_filepath = SCREENSHOTS .. "date_no_sorting.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.95, capture_x = 0, capture_y = 0, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if no_date_set then
        core.Log("No date set, sorting by date", "magenta")
    else
        core.Log("Date sat", "magenta")
    end


    local find_newest_date = core.Image({ image_filepath = SCREENSHOTS .. "newest_date_found.png", max_wait_time_ms = 10, click_type = "leftclick", mouse_move = false, match_score = 0.95, capture_x = 0, capture_y = 0, capture_width = WINDOW_SIZE.width, capture_height = WINDOW_SIZE.height, wait_before = 0, wait_after = 0, offset_x = 0, offset_y = 0, print_match_score = false, debug = DEBUG, debug_path = DEBUG_PATH, speed_delay = 0, random_click_pos = false })
    if find_newest_date then
        core.Log("Newest date found, sort by oldest date", "magenta")
    else
        core.Log("Already sorted by oldest date", "magenta")
    end
end

function M.CancelItemForSale(itemTable, transaction)
    core.Log("Updating item for sale, assuming item is 100% found", "yellow")
    M.GoToRestPosition()
    M.CloseItemWindow()

    M.LoadMore()

    M.FocusFirstItem()

    if M.ItemWindowOpen() then

        local actual_price = tonumber(transaction.Price)
        local new_price = M.GetPrice()

        core.Log("Total sellPrice copper: " .. tostring(new_price.total_copper), "magenta")
        print(actual_price)

        if actual_price > new_price.total_copper then
            core.Log("Price changed, our selling price is higher than lowest seller. Cancel", "magenta")

            M.CloseItemWindow()
            M.CancelOldItemBids()
        else
            M.CloseItemWindow()
        end
    else
        core.Log("ItemWindow not open in UpdateItemForSale", "red")
        M.SelfHeal()
    end
end

function M.UpdateItemsForSale()
    core.Log("Updating items for sale", "magenta")
    M.GoToRestPosition()
    M.CloseItemWindow()
    M.SelectTransactionsSell()

    if M.NoItemsFound() then
        core.Log("No items for found for relisting, moving on", "magenta")
    else

        M.SortByOldestDate()

        local last_digit = M.GetLastDigitOfFirstItem()
        core.Log("Last digit, our item: " .. last_digit, "blue")

        M.LoadMore()
        M.FocusFirstItem()

        if M.ItemWindowOpen() then

            local sellPrice = M.GetPrice()
            local priceChanged = M.CompareLastDigits(last_digit, sellPrice.copper)
            if priceChanged then
                core.Log("Price changed, cancel", "magenta")
                M.CancelFirstItem()
                core.Log("Rerunning UpdateItemsForSale", "magenta")
                return M.UpdateItemsForSale()
            else
                core.Log("Price didnt chance, do not cancel", "magenta")
                M.CloseItemWindow()
            end
        else
            core.Log("ItemWindow not open in UpdateItemsForSale", "red")
            core.Sleep(30000)
        end
    end
end

function M.Sell()
    core.Log("Selling", "yellow")

    M.CloseItemWindow()
    M.ResetFilters()
    M.GoToRestPosition()
    M.FocusSellTab()

    if M.NoItemsFound() then
        core.Log("No item found", "magenta")
    else
        core.Log("Items to sell", "magenta")
        M.FocusFirstItem()
        if PRICE_MODE ~= "api" then
            -- OCR mode: compute profitability using on-screen prices before undercutting
            -- Get current sell price (before any undercut) and current top buy price
            M.ItemGetSellOrderPrice()
            local sellPrice = M.GetPrice()
            M.ItemGetBuyOrderPrice()
            local buyPrice = M.GetPrice()

            -- Guard against missing/invalid prices to avoid bad math
            if not sellPrice or not sellPrice.total_copper or not buyPrice or not buyPrice.total_copper or buyPrice.total_copper <= 0 then
                core.Log("Unable to read valid prices for profitability check; skipping listing", "red")
                M.CloseItemWindow()
                return M.Sell()
            end

            -- Compute expected profit at current visible prices; if not profitable, skip listing
            local profit_percentage_dynamic = M.CalculateDifference(buyPrice.total_copper, sellPrice.total_copper)
            core.Log("profit_percentage_dynamic (pre-undercut): " .. tostring(profit_percentage_dynamic), "magenta")

            if PROFIT_THRESHOLD ~= nil and profit_percentage_dynamic <= PROFIT_THRESHOLD then
                core.Log("Not profitable to flip at current prices; skipping listing", "yellow")
                M.CloseItemWindow()
                return M.Sell()
            end
        else
            -- API mode: do not use OCR; listing decisions handled by API-driven resell flow
            core.Log("PRICE_MODE=api: skipping OCR-based profitability check in Sell()", "cyan")
        end

        -- Proceed to list
        M.ItemGetSellOrderPrice()
        M.ChooseMaxQuantity()

        if UNDERCUT_SELL_LISTINGS then
            M.ApplyUndercutBuffer()
        end

        M.ListItemForSale()
        M.SellInstantly() -- this is needed to not get stuck, if item was not listable
        M.CloseItemWindow()
        return M.Sell()
    end

    M.CloseItemWindow()
    M.ResetFilters()
end

return M
