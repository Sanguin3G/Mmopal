-- main.lua
local core                          = require("core")
local util                          = require("util")
local logger                        = require("logger")
local tp                            = require("lua-gw2.tradingbot.tradingpost")

-- Load .ini file with user settings
-- Construct path relative to current working directory
local currentDir = util.GetCurrentDirectory()
local settingsPath = currentDir .. "\\lua-gw2\\tradingbot\\settings.ini"
local settings = util.LoadSettings(settingsPath)

if not settings or not settings.Settings then
    core.Log("ERROR: Failed to load settings from " .. settingsPath, "red")
    core.Log("Please ensure settings.ini exists and is properly formatted", "red")
    core.Sleep(10000)
    error("Failed to load settings.ini")
end

-- GLOBAL VARIABLES
EXE_FILE                            = "Gw2-64.exe"
PROCESS_NAME                        = "Guild Wars 2"
EXE_FILEPATH                        = settings.Settings.EXE_FILEPATH or "C:/Program Files (x86)/Steam/steamapps/common/Guild Wars 2/Gw2-64.exe"
SCREENSHOTS                         = util.GetCurrentDirectory() .. "\\lua-gw2\\tradingbot\\screenshots\\"
PATH_CUSTOM_SHOPPINGLIST            = util.GetCurrentDirectory() .. "\\lua-gw2\\tradingbot\\custom_shoppinglist"
PATH_CURRENT_TRANSACTIONS           = util.GetCurrentDirectory() .. "\\lua-gw2\\tradingbot\\current_transactions"
BLACKLISTED_ITEMS_PATH              = util.GetCurrentDirectory() .. "\\lua-gw2\\tradingbot\\blacklisted_items.csv"
WEBSITE_SHOPPINGLIST_URL            = settings.Settings.WEBSITE_SHOPPINGLIST_URL
WINDOW_SIZE                         = { width = 1024, height = 768 }
DEBUG_PATH                          = util.GetCurrentDirectory() .. "\\debug\\"
DEBUG                               = false
DELAY                               = 0
USE_CUSTOM_SHOPPINGLIST             = util.toboolean(settings.Settings.USE_CUSTOM_SHOPPINGLIST)
USE_WEBSITE_SHOPPINGLIST            = util.toboolean(settings.Settings.USE_WEBSITE_SHOPPINGLIST)
CUSTOM_QUANTITY                     = tonumber(settings.Settings.CUSTOM_QUANTITY) or 1
WEBSITE_PAGES                       = tonumber(settings.Settings.WEBSITE_PAGES) or 1
BLTC_SHOPPINGLIST_PATH              = util.GetCurrentDirectory() .. "\\lua-gw2\\tradingbot\\shoppinglist_bltc"
GW2PROFITS_SHOPPINGLIST_PATH        = util.GetCurrentDirectory() .. "\\lua-gw2\\tradingbot\\shoppinglist_gw2profits"
TIME_BEFORE_CANCEL_SALE_ITEMS       = tonumber(settings.Settings.TIME_BEFORE_CANCEL_SALE_ITEMS)
TIME_BEFORE_CANCEL_ORDERED_ITEMS    = tonumber(settings.Settings.TIME_BEFORE_CANCEL_ORDERED_ITEMS)
API_KEY                             = settings.Settings.API_KEY
ITERATIONS_BFORE_SELLING            = tonumber(settings.Settings.ITERATIONS_BEFORE_SELLING)
ITERATIONS_BEFORE_RESTARTING_TP     = tonumber(settings.Settings.ITERATIONS_BEFORE_RESTARTING_TP)
ITERATIONS_BEFORE_RESELLING         = tonumber(settings.Settings.ITERATIONS_BEFORE_RESELLING)
ITERATIONS_BEFORE_GETINFO           = tonumber(settings.Settings.ITERATIONS_BEFORE_GETINFO)
MAX_PRICE_PER_ITEM                  = tonumber(settings.Settings.MAX_PRICE_PER_ITEM)
GOLD_THRESHOLD_RELIST               = tonumber(settings.Settings.GOLD_THRESHOLD_RELIST)
GOLD_THRESHOLD_NO_BUY               = tonumber(settings.Settings.GOLD_THRESHOLD_NO_BUY)
SELL_ONLY                           = util.toboolean(settings.Settings.SELL_ONLY)
PROFIT_THRESHOLD                    = tonumber(settings.Settings.PROFIT_THRESHOLD)
OVERBID_BUY_LISTINGS                = util.toboolean(settings.Settings.OVERBID_BUY_LISTINGS)
UNDERCUT_SELL_LISTINGS              = util.toboolean(settings.Settings.UNDERCUT_SELL_LISTINGS)
USING_GW2PROFITS                    = false -- will flip to true if website settings are sat
USING_GW2BLTC                       = false -- will flip to true if website settings are sat
VALIDATE_CURRENT_TRANSACTION_PRICES = util.toboolean(settings.Settings.VALIDATE_CURRENT_TRANSACTION_PRICES)
SLOW_INTERNET                       = util.toboolean(settings.Settings.SLOW_INTERNET)
RANDOM_CHARACTER_SELECTION          = util.toboolean(settings.Settings.RANDOM_CHARACTER_SELECTION)

-- Price mode: "ocr" (default) or "api"
local PRICE_MODE_RAW                = settings.Settings.PRICE_MODE and tostring(settings.Settings.PRICE_MODE) or ""
PRICE_MODE                          = string.lower(PRICE_MODE_RAW)
if PRICE_MODE == "api" or PRICE_MODE == "api-only" or PRICE_MODE == "off" then
    -- Disable manual denomination checks; rely on API data paths
    VALIDATE_CURRENT_TRANSACTION_PRICES = false
elseif PRICE_MODE == "ocr" then
    VALIDATE_CURRENT_TRANSACTION_PRICES = true
end
if PRICE_MODE ~= "" then
    core.Log("PRICE_MODE set to: " .. PRICE_MODE .. " (VALIDATE_CURRENT_TRANSACTION_PRICES=" .. tostring(VALIDATE_CURRENT_TRANSACTION_PRICES) .. ")", "cyan")
end


-- Logging and runtime modes
local LOG_LEVEL_RAW                 = settings.Settings.LOG_LEVEL or "INFO"
local INFO_MODE                     = util.toboolean(settings.Settings.INFO_MODE)
local PRETTY_LOGS                   = util.toboolean(settings.Settings.PRETTY_LOGS)
FAST_MODE                           = util.toboolean(settings.Settings.FAST_MODE)

-- Configure logger with settings
logger.configure({ level = LOG_LEVEL_RAW, info_mode = INFO_MODE, pretty = PRETTY_LOGS, tag = "GW2TP" })
logger.patch_core(core, "GW2TP")
if INFO_MODE then core.Log("Info mode enabled (showing concise messages)", "yellow") end
if FAST_MODE then core.Log("Fast mode enabled (reduced waits where safe)", "yellow") end

-- Rate limiting / scan cadence (fallback defaults if missing in settings.ini)
API_TRANSACTIONS_REFRESH_SECONDS    = tonumber(settings.Settings.API_TRANSACTIONS_REFRESH_SECONDS) or 60
API_BALANCE_REFRESH_SECONDS         = tonumber(settings.Settings.API_BALANCE_REFRESH_SECONDS) or 120
OVERBID_SCAN_INTERVAL_SECONDS       = tonumber(settings.Settings.OVERBID_SCAN_INTERVAL_SECONDS) or 30
OVERBID_SCAN_MAX_UPDATES_PER_PASS   = tonumber(settings.Settings.OVERBID_SCAN_MAX_UPDATES_PER_PASS) or 3
OVERBID_BUFFER_COPPER               = tonumber(settings.Settings.OVERBID_BUFFER_COPPER) or 1
OVERBID_FORCE_API_REFRESH_SECONDS   = tonumber(settings.Settings.OVERBID_FORCE_API_REFRESH_SECONDS) or 10
-- New: randomized copper buffers for overbid/undercut
OVERBID_BUFFER_MIN_COPPER           = tonumber(settings.Settings.OVERBID_BUFFER_MIN_COPPER) or 1
OVERBID_BUFFER_MAX_COPPER           = tonumber(settings.Settings.OVERBID_BUFFER_MAX_COPPER) or 5
UNDERCUT_BUFFER_MIN_COPPER          = tonumber(settings.Settings.UNDERCUT_BUFFER_MIN_COPPER) or 1
UNDERCUT_BUFFER_MAX_COPPER          = tonumber(settings.Settings.UNDERCUT_BUFFER_MAX_COPPER) or 5

-- GLOBAL VARIABLES
GLOBAL_ITEM_INDEX = 1 -- Track progress through items
GLOBAL_ITEM_LIST = nil -- Store the scraped items
HAS_SCRAPED = false -- Flag to indicate whether scraping has been done

GLOBAL_CUSTOM_ITEM_INDEX = 1 -- Track progress through custom list
GLOBAL_CUSTOM_ITEM_LIST = nil -- Store the custom shopping list items
HAS_CUSTOM_SCRAPED = false -- Flag for custom list scraping

SELL_ITERATION_INDEX = 0
TP_ITERATION_INDEX = 0
RESELL_ITERATION_INDEX = 0
GETINFO_ITERATION_INDEX = 0
USE_ARDUINO           = false  -- false: hardware clicks | true: normal windows mouse (SendInput)
LAST_OVERBID_SCAN_TS  = 0
LAST_OVERBID_TRANSACTIONS_FETCH_TS = 0
LAST_GLOBAL_TRANSACTIONS_REFRESH_TS = 0

-- Seed RNG once for randomized copper adjustments
if not __RNG_SEEDED__ then
    math.randomseed(os.time())
    __RNG_SEEDED__ = true
end

-- Cooperative pause/stop responsiveness across long waits
local __orig_Sleep = core.Sleep
core.Sleep = function(ms)
    local remaining = tonumber(ms) or 0
    if remaining <= 0 then return __orig_Sleep(0) end
    local step = 25
    while remaining > 0 do
        -- Check if bot should stop (responsive to stop button)
        if core.ShouldStop and core.ShouldStop() then
            core.Log("Stop requested, breaking sleep early", "yellow")
            return
        end
        local s = math.min(step, remaining)
        __orig_Sleep(s)
        remaining = remaining - s
    end
end

local __orig_Image = core.Image
core.Image = function(opts)
    opts = opts or {}
    local total = tonumber(opts.max_wait_time_ms) or 0
    if total <= 250 then
        return __orig_Image(opts)
    end
    local deadline = os.clock() + (total / 1000.0)
    local slice_ms = 25
    local status = false
    while os.clock() < deadline do
        local sub = {}
        for k, v in pairs(opts) do sub[k] = v end
        local remaining_ms = math.floor((deadline - os.clock()) * 1000)
        sub.max_wait_time_ms = math.max(10, math.min(slice_ms, remaining_ms))
        status = __orig_Image(sub)
        if status then break end
    end
    return status
end

-- Slice long coordinate searches to respect pause/resume
local __orig_GetCoordinates = core.GetCoordinates
if __orig_GetCoordinates then
    core.GetCoordinates = function(opts)
        opts = opts or {}
        local total = tonumber(opts.max_wait_time_ms) or 0
        if total <= 250 then
            return __orig_GetCoordinates(opts)
        end
        local deadline = os.clock() + (total / 1000.0)
        local slice_ms = 25
        local result = nil
        while os.clock() < deadline do
            local sub = {}
            for k, v in pairs(opts) do sub[k] = v end
            local remaining_ms = math.floor((deadline - os.clock()) * 1000)
            sub.max_wait_time_ms = math.max(10, math.min(slice_ms, remaining_ms))
            result = __orig_GetCoordinates(sub)
            if result then break end
        end
        return result
    end
end

-- Helper: shorten sleeps slightly when FAST_MODE is enabled
local function FastSleep(ms)
    if FAST_MODE and ms >= 1000 and ms < 60000 then
        local reduced = math.floor(ms * 0.6)
        core.Sleep(reduced)
    else
        core.Sleep(ms)
    end
end


-- Helper: shorten sleeps slightly when FAST_MODE is enabled
if USE_ARDUINO then
    core.Log("Initializing Arduino mouse...", "cyan")

    local arduinoSuccess = core.ArduinoInitialize({ comPort = "COM9", baudRate = 115200 })

    if not arduinoSuccess then
        core.Log("Ensure your arduino runs on COM PORT 9", "red")
    end

    -- Enable Arduino mode (will error if Arduino not connected)
    core.SetArduinoMode(true)
else
    core.SetArduinoMode(false)
end

-- Function to check if it's time to relist the item (using UTC)
local function IsTimeToRelist(transactionCreatedTime, timeBeforeCancelHours)

    -- Parse the transaction created time (format: "2024-09-14T14:12:41+00:00" which is in UTC)
    local pattern = "(%d+)%-(%d+)%-(%d+)T(%d+):(%d+):(%d+)"
    local year, month, day, hour, min, sec = transactionCreatedTime:match(pattern)

    if not (year and month and day and hour and min and sec) then
        core.Log("Invalid transaction created time format: " .. transactionCreatedTime, "red")
        return false
    end

    -- Convert the parsed transaction time to UTC time using os.time with UTC flag
    local transactionTimeUTC = os.time({
        year = tonumber(year),
        month = tonumber(month),
        day = tonumber(day),
        hour = tonumber(hour),
        min = tonumber(min),
        sec = tonumber(sec),
        isdst = false -- Ensure it's UTC, not DST (Daylight Saving Time)
    })

    -- Get the current time in UTC
    local currentTimeUTC = os.time(os.date("!*t"))

    -- Calculate the time difference in hours
    local timeElapsed = (currentTimeUTC - transactionTimeUTC) / 3600 -- Convert seconds to hours

    -- Return true if the time elapsed is greater than or equal to the threshold
    return timeElapsed >= timeBeforeCancelHours
end


local function Resell(blacklistedItems)

    core.Log("Reselling", "magenta")

    tp.GetTransactions()

    local transactionsTable = ParseCurrentTransactionsFile(PATH_CURRENT_TRANSACTIONS .. ".csv")

    for _, transaction in ipairs(transactionsTable) do

        if tp.IsItemBlacklisted(transaction.ItemName, blacklistedItems) then
            core.Log("Item " .. transaction.ItemName .. " is blacklisted and will be skipped.", "cyan")
        else
            if transaction.Type == "Sell" then
                core.Log("Evaluating if we should sell " .. transaction.ItemName .. " at price: " .. tostring(transaction.Price), "yellow")
                if IsTimeToRelist(transaction.Created, TIME_BEFORE_CANCEL_SALE_ITEMS) then
                    core.Log("It is also time to cancel the item", "cyan")
                    if transaction.MinSellPrice < transaction.Price then
                        -- Assess profitability using API min prices before deciding to undercut again
                        local expected_profit = tp.CalculateDifference(tonumber(transaction.MinBuyPrice) or 0, tonumber(transaction.MinSellPrice) or 0)
                        core.Log("expected_profit (api spread): " .. tostring(expected_profit), "magenta")
                        if PRICE_MODE == "api" then
                            if PROFIT_THRESHOLD ~= nil and expected_profit <= PROFIT_THRESHOLD then
                                core.Log("Not profitable anymore by API; cancel listing and do not relist lower", "yellow")
                            else
                                core.Log("Still profitable by API; cancel to relist at competitive price", "green")
                            end
                        else
                            core.Log("We are not selling lowest, cancel item list", "cyan")
                        end

                        tp.CloseItemWindow()
                        tp.GoToTransactionsTab()
                        tp.SelectTransactionsSell()
                        if (tp.FocusSearchField()) then
                            core.SendString(util.Trim(transaction.ItemName))
                            core.Sleep(DELAY);
                        end
                        FastSleep(2000)
                        tp.GoToRestPosition()
                        tp.LoadMore()
                        tp.CancelOldItemBids()
                        tp.TakeItems()
                    else
                        core.Log("We are selling lowest, move on", "cyan")
                    end
                else
                    core.Log("Its not yet time to cancel item for sale", "cyan")
                end
            end

            if transaction.Type == "Buy" then
                core.Log("Updating current buy transaction: " .. transaction.ItemName .. " at price: " .. tostring(transaction.Price), "yellow")
                if IsTimeToRelist(transaction.Created, TIME_BEFORE_CANCEL_ORDERED_ITEMS) then
                    core.Log("It is time to relist " .. transaction.ItemName, "cyan")
                    if transaction.MinBuyPrice > transaction.Price then
                        core.Log("Someone is overbidding us, lets beat that", "cyan")

                        tp.CloseItemWindow()

                        if tp.AtTransactionsBuying() then
                            core.Log("Already there", "magenta")
                            tp.ResetSearchField()
                        else
                            tp.GoToTransactionsTab()
                            tp.SelectTransactionsBuy()
                        end

                        if (tp.FocusSearchField()) then
                            core.SendString(util.Trim(transaction.ItemName))
                            core.Sleep(DELAY);
                        end
                        
                        core.Log("Ensure page is refreshed", "green")
                        tp.ChooseCurrentTransactionSelling()
                        tp.ChooseCurrentTransactionBuy()
                        FastSleep(500)

                        tp.GoToRestPosition()
                        tp.CloseItemWindow()
                        tp.LoadMore()
                        tp.FocusFirstItem()

                        if tp.ItemWindowOpen() then

                            if VALIDATE_CURRENT_TRANSACTION_PRICES then

                                local sellPrice = tp.GetPrice()

                                tp.ItemGetBuyOrderPrice()

                                local buyPrice = tp.GetPrice()
                                core.Log("Total buyPrice copper: " .. tostring(buyPrice.total_copper), "magenta")
                                core.Log("Gold:   " .. tostring(buyPrice.gold), "magenta")
                                core.Log("Silver: " .. tostring(buyPrice.silver), "magenta")
                                core.Log("Copper: " .. tostring(buyPrice.copper), "magenta")

                                local fullItemPrice = ((buyPrice.total_copper/10000) * CUSTOM_QUANTITY)
                                if fullItemPrice > MAX_PRICE_PER_ITEM then
                                    core.Log("Actual buyprice: " .. tostring(fullItemPrice) .. "g exceeds MAX_PRICE_PER_ITEM: " .. tostring(MAX_PRICE_PER_ITEM) .. "g", "brightmagenta")
                                else
                                    local profit_percentage_dynamic = tp.CalculateDifference(buyPrice.total_copper, sellPrice.total_copper)
                                    core.Log("profit_percentage_dynamic: " .. tostring(profit_percentage_dynamic), "magenta")

                                    if profit_percentage_dynamic > 0 then
                                        core.Log("Profitable! Buy it!", "green")

                                        if transaction ~= nil and transaction.Quantity ~= nil then
                                            local roundedQuantity = math.floor(transaction.Quantity)  -- Round down the quantity

                                            core.Log("Using current transaction quantity amount: " .. roundedQuantity, "yellow")

                                            if roundedQuantity < 1 then
                                                roundedQuantity = 1  -- Ensure the quantity is at least 1
                                            elseif roundedQuantity > 250 then
                                                roundedQuantity = 250
                                            end

                                            tp.ItemSelectQuantity(roundedQuantity)
                                        else
                                            core.Log("Using default quantity amount: " .. CUSTOM_QUANTITY, "yellow")
                                            tp.ItemSelectQuantity(CUSTOM_QUANTITY)
                                        end

                                        if OVERBID_BUY_LISTINGS then
                                            tp.ApplyOverbidBuffer()
                                        end

                                        tp.PlaceOrder()
                                        tp.CloseStatusBox()
                                        tp.CancelOldItemBids()
                                        tp.TakeItems()
                                        -- Immediately re-check overbids to react fast
                                        local txs = ParseCurrentTransactionsFile(PATH_CURRENT_TRANSACTIONS .. ".csv")
                                        if CheckAndUpdateOverbids then
                                            CheckAndUpdateOverbids(txs, blacklistedItems)
                                        end
                                    else
                                        core.Log("Not profitable", "magenta")
                                        tp.CloseItemWindow()
                                        tp.CancelOldItemBids()
                                        tp.TakeItems()
                                    end
                                end
                            else

                                tp.ItemGetBuyOrderPrice()

                                local fullItemPrice = ((transaction.MinBuyPrice/10000) * CUSTOM_QUANTITY)

                                if fullItemPrice > MAX_PRICE_PER_ITEM then
                                    core.Log("Actual buyprice: " .. tostring(fullItemPrice) .. "g exceeds MAX_PRICE_PER_ITEM: " .. tostring(MAX_PRICE_PER_ITEM) .. "g", "brightmagenta")
                                else
                                    local profit_percentage_dynamic = tp.CalculateDifference(transaction.MinBuyPrice, transaction.MinSellPrice)
                                    core.Log("profit_percentage_dynamic: " .. tostring(profit_percentage_dynamic), "magenta")

                                    if profit_percentage_dynamic > 0 then
                                        core.Log("Profitable! Buy it!", "green")

                                        if transaction ~= nil and transaction.Quantity ~= nil then
                                            local roundedQuantity = math.floor(transaction.Quantity)  -- Round down the quantity

                                            core.Log("Using current transaction quantity amount: " .. roundedQuantity, "yellow")

                                            if roundedQuantity < 1 then
                                                roundedQuantity = 1  -- Ensure the quantity is at least 1
                                            elseif roundedQuantity > 250 then
                                                roundedQuantity = 250
                                            end

                                            tp.ItemSelectQuantity(roundedQuantity)
                                        else
                                            core.Log("Using default quantity amount: " .. CUSTOM_QUANTITY, "yellow")
                                            tp.ItemSelectQuantity(CUSTOM_QUANTITY)
                                        end

                                        if OVERBID_BUY_LISTINGS then
                                            tp.ApplyOverbidBuffer()
                                        end

                                        tp.PlaceOrder()
                                        tp.CloseStatusBox()
                                        tp.CancelOldItemBids()
                                        tp.TakeItems()
                                        -- Immediately re-check overbids to react fast
                                        local txs = ParseCurrentTransactionsFile(PATH_CURRENT_TRANSACTIONS .. ".csv")
                                        if CheckAndUpdateOverbids then
                                            CheckAndUpdateOverbids(txs, blacklistedItems)
                                        end
                                    else
                                        core.Log("Not profitable", "magenta")
                                        tp.CloseItemWindow()
                                        tp.CancelOldItemBids()
                                        tp.TakeItems()
                                    end
                                end
                            end
                        else
                            core.Log("Not going to relist", "brightmagenta")
                        end
                    else
                        core.Log("We are already highest bidder", "cyan")
                    end
                else
                    core.Log("Its not yet time to cancel and reoffer", "cyan")
                end
            end
        end
    end

    core.Log("We perhaps cancelling items, lets resell them, before ending the resell routine", "brightmagenta")
    -- tp.Sell()
end

-- Function to process each trading post item
local function ProcessTradingPostItem(itemTable, transactionsTable, blacklistedItems)

    -- Ensure we can iterate even when there are no current transactions
    if transactionsTable == nil then
        core.Log("No current transactions available; skipping transaction checks for this item", "yellow")
        transactionsTable = {}
    end

    local current_gold = tp.GetCurrentBalance()
    if current_gold == nil then
        core.Log("Somethings wrong when fetching GetCurrentBalance, waiting 20 seconds before retrying", "red")
        core.Sleep(20000)
        return
    elseif current_gold < GOLD_THRESHOLD_NO_BUY then
        core.Log("Current gold (" .. tostring(current_gold) .. ") is lower than GOLD_THRESHOLD_NO_BUY (" .. tostring(GOLD_THRESHOLD_NO_BUY) .. "). We will not buy/sell items, not even relist before we are above threshold.", "yellow")
        tp.ReOpenTP()
        tp.TakeItems()
        core.Log("Waiting 2 minutes before taking items/gold from TP", "yellow")
        core.Sleep(120000)
        return
    end

    if TP_ITERATION_INDEX >= ITERATIONS_BEFORE_RESTARTING_TP then
        tp.ReOpenTP()
        TP_ITERATION_INDEX = 0
    end

    if SELL_ITERATION_INDEX >= ITERATIONS_BFORE_SELLING then
        tp.Sell()
        SELL_ITERATION_INDEX = 0
    end

    if RESELL_ITERATION_INDEX >= ITERATIONS_BEFORE_RESELLING then
        Resell(blacklistedItems)
        RESELL_ITERATION_INDEX = 0
    end

    for _, transaction in ipairs(transactionsTable) do

        if string.lower(transaction.ItemName) == string.lower(itemTable.ItemName) then

            if tp.IsItemBlacklisted(transaction.ItemName, blacklistedItems) then
                core.Log("Item " .. itemTable.ItemName .. " is blacklisted and will be skipped.", "cyan")
            else
                core.Log("Plan is to buy item  " .. transaction.ItemName .. ", but we wont buy if we already buy or sell it", "yellow")
                if transaction.Type == "Sell" then
                    core.Log("We are already selling " .. itemTable.ItemName .. " at price price: " .. tostring(transaction.Price), "yellow")

                    if IsTimeToRelist(transaction.Created, TIME_BEFORE_CANCEL_SALE_ITEMS) then
                        core.Log("Item was found in items we are currently selling", "brightmagenta")

                        if transaction.MinSellPrice < transaction.Price then
                            core.Log("We are not selling lowest, consider reselling", "brightmagenta")
                            tp.CloseItemWindow()
                            tp.GoToTransactionsTab()
                            tp.SelectTransactionsSell()
                            -- tp.OpenRarityTab()
                            -- tp.ChooseRarity(util.Trim(itemTable.rarity))
                            if (tp.FocusSearchField()) then
                                core.SendString(itemTable.ItemName);
                                core.Sleep(DELAY);
                            end
                            FastSleep(2000)
                            tp.LoadMore()
                            tp.CancelItemForSale(itemTable, transaction)
                        else
                            core.Log("We are already selling lowest", "brightmagenta")
                        end
                    else
                        core.Log("Its not yet time to resell", "brightmagenta")
                    end
                end

                if transaction.Type == "Buy" then
                    core.Log("We are already bidding on item " .. itemTable.ItemName .. " at price: " .. tostring(transaction.Price), "yellow")
                    if IsTimeToRelist(transaction.Created, TIME_BEFORE_CANCEL_ORDERED_ITEMS) then
                        core.Log("Item was found in items we are currently buying", "brightmagenta")
                        if transaction.MinBuyPrice > transaction.Price then
                            core.Log("Someone is overbidding us, lets beat that", "cyan")

                            tp.CloseItemWindow()

                            if tp.AtTransactionsBuying() then
                                core.Log("Already there", "magenta")
                                tp.ResetSearchField()
                            else
                                tp.GoToTransactionsTab()
                                tp.SelectTransactionsBuy()
                            end

                            if (tp.FocusSearchField()) then
                                -- core.SendString(util.Trim(itemTable.ItemName));
                                core.SendString(itemTable.ItemName);
                                core.Sleep(DELAY);
                            end
                            FastSleep(2000)
                        else
                            core.Log("We are already highest bidder", "brightmagenta")
                        end
                    else
                        core.Log("Its not yet time to cancel and reoffer", "brightmagenta")
                    end
                end
                return
            end
        end
    end

    local current_gold = tp.GetCurrentBalance()
    if SELL_ONLY then
        core.Log("We are only keeping orders up-to-date and selling items", "brightmagenta")
    else
        if current_gold == nil then
            core.Log("Gold value could not be fetched, report this. We continue for now", "red")
        end

        if current_gold ~= nil and current_gold < GOLD_THRESHOLD_RELIST then
            core.Log("Current gold (" .. tostring(current_gold) .. ") is lower than GOLD_THRESHOLD_RELIST (" .. tostring(GOLD_THRESHOLD) .. "). We will not buy items, only relist before threshold is met.", "yellow")
        else
            tp.BuyItem(itemTable, blacklistedItems)
        end
    end
end

-- Function to write data to a CSV file
local function WriteCSV(filePath, data)
    local file = io.open(filePath, "w")
    if not file then
        core.Log("Failed to open file for writing: " .. filePath, "red")
        return false
    end

    if #data == 0 then
        core.Log("No data to write to CSV.", "red")
        return false
    end

    local headers = { "ItemName", "Rarity", "Sell", "Buy", "Profit", "ROI", "Supply", "Demand", "Sold", "Offers", "Bought", "Bids" }
    -- core.Log("Writing headers: " .. table.concat(headers, ";"), "blue")
    file:write(table.concat(headers, ";") .. "\n")

    for _, row in ipairs(data) do
        local rowData = {}
        for _, header in ipairs(headers) do
            table.insert(rowData, row[header] or "n/a")
        end
        -- core.Log("Writing row: " .. table.concat(rowData, ";"), "green")
        file:write(table.concat(rowData, ";") .. "\n")
    end

    file:close()
    core.Log("Data successfully written to " .. filePath, "blue")
    return true
end

-- Function to scrape pages and write to CSV
local function ScrapePagesAndWriteToCSV()
    local allItems = {}
    for page = 1, WEBSITE_PAGES do
        local pageURL = WEBSITE_SHOPPINGLIST_URL .. "&page=" .. tostring(page)
        local pageFilePath = BLTC_SHOPPINGLIST_PATH .. "_page_" .. page .. ".csv"
        core.Log("Scraping page: " .. pageURL, "blue")

        local success = core.GW2_Scraper_BLTC(pageURL, pageFilePath)

        if success then
            core.Log("Page " .. page .. " scraping complete", "magenta")
            local csvData = util.ReadFileContents(pageFilePath)
            if not csvData then
                core.Log("Failed to read file: " .. pageFilePath, "red")
                return
            end

            local data = util.ParseCSV(csvData)
            if not data then
                core.Log("Failed to parse CSV data for page " .. tostring(page), "red")
                return
            end

            for _, itemTable in ipairs(data) do
                table.insert(allItems, itemTable)
            end
            core.Log("Page " .. page .. " data successfully parsed and added", "blue")
        else
            core.Log("Scraping failed for page " .. tostring(page), "red")
            break
        end

        FastSleep(5000)
    end

    if #allItems > 0 then
        local writeSuccess = WriteCSV(BLTC_SHOPPINGLIST_PATH .. ".csv", allItems)
        if writeSuccess then
            core.Log("All pages scraped and concatenated successfully, see " .. BLTC_SHOPPINGLIST_PATH .. ".csv", "magenta")
        else
            core.Log("Failed to write concatenated data to CSV", "red")
        end
    else
        core.Log("No items found on any pages", "red")
    end

    return allItems
end

function ParseCurrentTransactionsFile(filePath)
    local fileContent = util.ReadFileContents(filePath)
    if not fileContent then
        core.Log("Failed to read current transactions file: " .. filePath, "red")
        return nil
    end

    local transactionsTable = util.ParseCSV(fileContent)

    if not transactionsTable or #transactionsTable == 0 then
        core.Log("No transactions found in the file: " .. filePath, "yellow")
        return nil
    end

    -- Normalize fields for robust comparisons
    for _, tx in ipairs(transactionsTable) do
        if tx.Type then tx.Type = tostring(tx.Type):gsub("^%s+", ""):gsub("%s+$", "") end
        if tx.ItemName then tx.ItemName = tostring(tx.ItemName):gsub("^%s+", ""):gsub("%s+$", "") end
        if tx.Price then tx.Price = tonumber(tx.Price) or tx.Price end
        if tx.MinBuyPrice then tx.MinBuyPrice = tonumber(tx.MinBuyPrice) or tx.MinBuyPrice end
        if tx.MinSellPrice then tx.MinSellPrice = tonumber(tx.MinSellPrice) or tx.MinSellPrice end
        if tx.Quantity then tx.Quantity = tonumber(tx.Quantity) or tx.Quantity end
    end

    core.Log("Successfully parsed " .. tostring(#transactionsTable) .. " transactions.", "green")
    return transactionsTable
end

-- Helpers to revalidate transactions when user manually cancels in-game
-- (Reverted) No freshness helpers; use API refresh cadence instead

-- Proactively scan current buy orders for overbids and update a few per pass
function CheckAndUpdateOverbids(transactionsTable, blacklistedItems)
    if not transactionsTable or #transactionsTable == 0 then return end

    local now = os.time()
    if LAST_OVERBID_SCAN_TS ~= 0 and (now - LAST_OVERBID_SCAN_TS) < OVERBID_SCAN_INTERVAL_SECONDS then
        return
    end

    -- Quickly determine if we have any outstanding buys
    local hasBuys = false
    for _, tx in ipairs(transactionsTable) do
        if tx.Type == "Buy" then hasBuys = true break end
    end
    if not hasBuys then return end

    -- Force-refresh transactions more frequently during overbid scans (bounded)
    if LAST_OVERBID_TRANSACTIONS_FETCH_TS == 0 or (now - LAST_OVERBID_TRANSACTIONS_FETCH_TS) >= OVERBID_FORCE_API_REFRESH_SECONDS then
        tp.GetTransactionsForce()
        LAST_OVERBID_TRANSACTIONS_FETCH_TS = now
        transactionsTable = ParseCurrentTransactionsFile(PATH_CURRENT_TRANSACTIONS .. ".csv") or transactionsTable
    end

    local updates = 0
    local inspected = 0
    for _, tx in ipairs(transactionsTable) do
        if tx.Type == "Buy" and tonumber(tx.MinBuyPrice) and tonumber(tx.Price) and (tonumber(tx.MinBuyPrice) > tonumber(tx.Price)) then
            if not tp.IsItemBlacklisted(tx.ItemName, blacklistedItems) then
                core.Log("Overbid detected for " .. tx.ItemName .. ": our " .. tostring(tx.Price) .. ", min " .. tostring(tx.MinBuyPrice), "cyan")

                -- Navigate to the buy tab and update this item
                tp.CloseItemWindow()
                if not tp.AtTransactionsBuying() then
                    tp.GoToTransactionsTab()
                    tp.SelectTransactionsBuy()
                else
                    tp.ResetSearchField()
                end

                if tp.FocusSearchField() then
                    core.SendString(tx.ItemName)
                    core.Sleep(DELAY)
                end
                core.Sleep(500)
                tp.LoadMore()

                core.Log("Ensure page is refreshed", "green")
                tp.ChooseCurrentTransactionSelling()
                tp.ChooseCurrentTransactionBuy()
                FastSleep(500)

                -- Construct a minimal itemTable compatible with UpdateOrderedItem
                local itemTable = { ItemName = tx.ItemName, Quantity = tx.Quantity }
                tp.UpdateOrderedItem(itemTable, tx)

                updates = updates + 1
                if updates >= OVERBID_SCAN_MAX_UPDATES_PER_PASS then
                    break
                end
            end
        else
            if tx.Type == "Buy" and inspected < 3 then
                core.Log("Buy OK: " .. tx.ItemName .. " our " .. tostring(tx.Price) .. ", min " .. tostring(tx.MinBuyPrice), "magenta")
                inspected = inspected + 1
            end
        end
    end

    if updates > 0 then
        LAST_OVERBID_SCAN_TS = now
    else
        core.Log("No overbids detected in this scan", "magenta")
    end
end

function has_internet()
    local handle = io.popen("ping -n 1 www.google.com")
    local result = handle:read("*a")
    handle:close()
    return result:find("TTL=") ~= nil  -- Checks if the ping succeeded
end

local function DetectWebsite()
    local gw2profits_found = string.find(WEBSITE_SHOPPINGLIST_URL, "gw2profits.com")
    if gw2profits_found then
        core.Log("Using GW2Profits", "magenta")
        USING_GW2PROFITS = true
    else
        local gw2bltc_found = string.find(WEBSITE_SHOPPINGLIST_URL, "gw2bltc.com")
        if gw2bltc_found then 
            core.Log("Using GW2BLTC", "magenta")
            USING_GW2BLTC = true
        else
            core.Log("Please insert a website in the settings folder", "red")
            core.Sleep(60000)
        end
    end
end

-- Main function to start the trading post bot
function StartTradingPostBot()
    core.Log("Starting GW2 Tradingpost Bot", "yellow")
    core.Log("Press Ctrl+Shift+P to stop the bot", "yellow")

    util.AdjustProgramWindow(PROCESS_NAME, WINDOW_SIZE.width, WINDOW_SIZE.height)
    util.ResizeWindow(PROCESS_NAME, WINDOW_SIZE.width, WINDOW_SIZE.height)

    -- Initial setup
    local blacklistedItems = tp.LoadBlacklistedItems(BLACKLISTED_ITEMS_PATH)
    DetectWebsite()
    
    -- On fresh start, clear and force-refresh transactions to avoid stale CSV
    tp.ClearCurrentTransactionsCSV()
    tp.GetTransactionsForce()
    tp.GetCurrentBalance()
    LAST_GLOBAL_TRANSACTIONS_REFRESH_TS = os.time()

    -- Quick early pass to catch any immediate overbids
    local earlyTx = ParseCurrentTransactionsFile(PATH_CURRENT_TRANSACTIONS .. ".csv")
    if CheckAndUpdateOverbids then
        CheckAndUpdateOverbids(earlyTx, blacklistedItems)
    end

    if not tp.IsTradingPostOpen() then

        core.Log("Checking if tradingpost is open", "brightmagenta")
        tp.ErrorBoxCheck()
        tp.Login()
    else
        core.Log("Trading if tradingpost is open", "brightmagenta")

        if USE_CUSTOM_SHOPPINGLIST then
            -- Only load custom shopping list if we haven't already done so
            if not HAS_CUSTOM_SCRAPED then
                local csvData = util.ReadFileContents(PATH_CUSTOM_SHOPPINGLIST .. ".csv")
                if csvData then
                    GLOBAL_CUSTOM_ITEM_LIST = util.ParseCSV(csvData)
                    HAS_CUSTOM_SCRAPED = true
                else
                    core.Log("Failed to read custom shopping list", "red")
                    return
                end
            end

            -- Resume processing the custom list from the current index
            if GLOBAL_CUSTOM_ITEM_LIST then
                for i = GLOBAL_CUSTOM_ITEM_INDEX, #GLOBAL_CUSTOM_ITEM_LIST do
                    -- Check if we should stop at the beginning of each iteration
                    if core.ShouldStop and core.ShouldStop() then
                        core.Log("Stop requested, exiting bot", "yellow")
                        return
                    end

                    local itemTable = GLOBAL_CUSTOM_ITEM_LIST[i]
                    core.Log("\nIndex: " .. tostring(i) .. " of " .. tostring(#GLOBAL_CUSTOM_ITEM_LIST) .. ", item: " .. itemTable.ItemName .. " (Custom shoppinglist)\n", "yellow")

                    tp.ErrorBoxCheck()

                    -- Keep transactions fresh on a time schedule regardless of iteration counts
                    if (os.time() - LAST_GLOBAL_TRANSACTIONS_REFRESH_TS) >= API_TRANSACTIONS_REFRESH_SECONDS then
                        tp.GetTransactionsForce()
                        LAST_GLOBAL_TRANSACTIONS_REFRESH_TS = os.time()
                    end

                    if GETINFO_ITERATION_INDEX >= ITERATIONS_BEFORE_GETINFO then
                        tp.GetTransactions()
                        tp.GetCurrentBalance()
                        GETINFO_ITERATION_INDEX = 0
                    end

                    -- Parse current transactions file
                    local transactionsTable = ParseCurrentTransactionsFile(PATH_CURRENT_TRANSACTIONS .. ".csv")

                    -- Opportunistically update any overbids before handling this item
                    if CheckAndUpdateOverbids then
                        CheckAndUpdateOverbids(transactionsTable, blacklistedItems)
                    end

                    core.Log("Current GLOBAL_CUSTOM_ITEM_INDEX: " .. tostring(GLOBAL_CUSTOM_ITEM_INDEX), "magenta")
                    core.Log("Current #GLOBAL_ITEM_LIST: " .. tostring(#GLOBAL_CUSTOM_ITEM_LIST), "magenta")
                    core.Sleep(500)

                    -- Process the item
                    ProcessTradingPostItem(itemTable, transactionsTable, blacklistedItems)

                    -- Update the global index after processing
                    GLOBAL_CUSTOM_ITEM_INDEX = i
                    SELL_ITERATION_INDEX = SELL_ITERATION_INDEX + 1
                    TP_ITERATION_INDEX = TP_ITERATION_INDEX + 1
                    RESELL_ITERATION_INDEX = RESELL_ITERATION_INDEX + 1
                    GETINFO_ITERATION_INDEX = GETINFO_ITERATION_INDEX + 1
                end


                -- Reset when the custom list is fully processed
                if GLOBAL_CUSTOM_ITEM_INDEX >= #GLOBAL_CUSTOM_ITEM_LIST then
                    GLOBAL_CUSTOM_ITEM_INDEX = 1
                    GLOBAL_CUSTOM_ITEM_LIST = nil
                    HAS_CUSTOM_SCRAPED = false
                    core.Log("Finished processing all items in the custom shopping list. Ready for next round.", "green")
                end
            end
        end

        if USE_WEBSITE_SHOPPINGLIST then

            if not HAS_SCRAPED then
                if USING_GW2BLTC then
                    core.Log("Scraping GW2BLTC", "magenta")
                    local allItems = ScrapePagesAndWriteToCSV()
                    if allItems then
                        GLOBAL_ITEM_LIST = allItems -- Store the scraped items globally
                        HAS_SCRAPED = true -- Set the flag to indicate scraping is done
                    else
                        core.Log("Failed to read gw2bltcs shopping list", "red")
                        return
                    end
                elseif USING_GW2PROFITS then
                    core.Log("Scraping GW2Profits", "magenta")
                    core.GW2_Scraper_GW2Profits(WEBSITE_SHOPPINGLIST_URL, GW2PROFITS_SHOPPINGLIST_PATH .. ".csv")
                    local csvData = util.ReadFileContents(GW2PROFITS_SHOPPINGLIST_PATH .. ".csv")
                    if csvData then
                        GLOBAL_ITEM_LIST = util.ParseCSV(csvData)
                        HAS_SCRAPED = true
                    else
                        core.Log("Failed to read gw2profits shopping list", "red")
                        return
                    end

                end
            end

            -- Resume processing from the current index
            if GLOBAL_ITEM_LIST then
                for i = GLOBAL_ITEM_INDEX, #GLOBAL_ITEM_LIST do
                    -- Check if we should stop at the beginning of each iteration
                    if core.ShouldStop and core.ShouldStop() then
                        core.Log("Stop requested, exiting bot", "yellow")
                        return
                    end

                    local itemTable = GLOBAL_ITEM_LIST[i]
                    core.Log("\nIndex: " .. tostring(i) .. " of " .. tostring(#GLOBAL_ITEM_LIST) .. ", item: " .. itemTable.ItemName .. " (website shoppinglist)\n", "yellow")

                    tp.ErrorBoxCheck()

                    -- Keep transactions fresh on a time schedule regardless of iteration counts
                    if (os.time() - LAST_GLOBAL_TRANSACTIONS_REFRESH_TS) >= API_TRANSACTIONS_REFRESH_SECONDS then
                        tp.GetTransactionsForce()
                        LAST_GLOBAL_TRANSACTIONS_REFRESH_TS = os.time()
                    end

                    if GETINFO_ITERATION_INDEX >= ITERATIONS_BEFORE_GETINFO then
                        tp.GetTransactions()
                        tp.GetCurrentBalance()
                        GETINFO_ITERATION_INDEX = 0
                    end

                    -- Parse current transactions file
                    local transactionsTable = ParseCurrentTransactionsFile(PATH_CURRENT_TRANSACTIONS .. ".csv")

                    -- Opportunistically update any overbids before handling this item
                    if CheckAndUpdateOverbids then
                        CheckAndUpdateOverbids(transactionsTable, blacklistedItems)
                    end

                    -- Process the item
                    ProcessTradingPostItem(itemTable, transactionsTable, blacklistedItems)

                    -- Update the global index after processing
                    GLOBAL_ITEM_INDEX = i
                    SELL_ITERATION_INDEX = SELL_ITERATION_INDEX + 1
                    TP_ITERATION_INDEX = TP_ITERATION_INDEX + 1
                    RESELL_ITERATION_INDEX = RESELL_ITERATION_INDEX + 1
                    GETINFO_ITERATION_INDEX = GETINFO_ITERATION_INDEX + 1
                end

                -- Reset when the list is fully processed
                if GLOBAL_ITEM_INDEX >= #GLOBAL_ITEM_LIST then
                    GLOBAL_ITEM_INDEX = 1
                    GLOBAL_ITEM_LIST = nil
                    HAS_SCRAPED = false
                    core.Log("Finished processing all items. Ready for next round.", "green")
                end
            end
        end

    end

    -- Check if we should stop before restarting the loop
    if core.ShouldStop and core.ShouldStop() then
        core.Log("Stop requested, bot stopped", "yellow")
        return
    end

    return StartTradingPostBot()
end

-- Start the bot
StartTradingPostBot()
