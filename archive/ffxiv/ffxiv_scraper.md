# core.FFXIV_Scraper

## Description

The `core.FFXIV_Scraper` is used save profitable items to buy into a .csv file.

## Parameters

- **`payload_str`** (string): A JSON-formatted string containing the instructions for what data to scrape from the game.
- **`output_file_path`** (string): The file path where the scraped data will be saved.

## Return value

- **`int`**: Returns an integer value representing the result of the scrape operation. This value can indicate success or failure, depending on the implementation details.

## Example

```lua
MAX_BUYOUT_PRICE_PER_UNIT = 200000
MAX_QUANTITY_PER_UNIT     = 10
ROI_THRESHOLD             = 30
SCAN_HOURS                = 360
SALE_AMOUNT               = 250
AVERAGE_PRICE_PER_UNIT    = 2500
MINIMUM_PROFIT_AMOUNT     = 2500
MINIMUM_STACK_SIZE        = 1
HIGH_QUALITY              = "false"
HOME_WORLD                = "Raiden"
REGION_WIDE               = "false"
INCLUDE_VENDOR            = "false"
SHOW_OUT_STOCK            = "false"
FILTERS                   = "[0]"

PAYLOAD = string.format([[{
    "preferred_roi": %d,
    "min_profit_amount": %d,
    "min_desired_avg_ppu": %d,
    "min_stack_size": %d,
    "hours_ago": %d,
    "min_sales": %d,
    "hq": %s,
    "home_server": "%s",
    "filters": %s,
    "region_wide": %s,
    "include_vendor": %s,
    "show_out_stock": %s
}]],
    ROI_THRESHOLD,
    MINIMUM_PROFIT_AMOUNT,
    AVERAGE_PRICE_PER_UNIT,
    MINIMUM_STACK_SIZE,
    SCAN_HOURS,
    SALE_AMOUNT,
    tostring(HIGH_QUALITY),
    tostring(HOME_WORLD),
    tostring(FILTERS),
    tostring(REGION_WIDE),
    tostring(INCLUDE_VENDOR),
    tostring(SHOW_OUT_STOCK)
)

SCRAPED_DATA_PATH = "shoppinglist.csv"

core.FFXIV_Scraper(PAYLOAD, SCRAPED_DATA_PATH)
```

Content of shoppinglist.csv after function has run:

```csv
roi;home_server_price;item_id;profit_amount;real_name;sale_rates;server;datacenter
98;5479;36190;5140;horse chestnut log;1.1154;lich;light
88;4499;5669;3774;savage aim materia i;1.0205;lich;light
```
