# core.GW2_Scraper_BLTC

## Description

The `core.GW2_Scraper_BLTC` function is used to scrape data from a specified URL
and output the results to a CSV file.

## Parameters

- **`url`** (string): The URL of the webpage to scrape.
- **`outputFilePath`** (string): The file path where the scraped data will be
  saved as a CSV file.

## Return value

- **`bool`**: Returns `true` if the scraping and file writing operations were successful; otherwise, it raises an error.

## Example

```lua
local success = core.Scrape_BLTC("https://www.gw2bltc.com/en/tp/search?profit-pct-min=10&profit-pct-max=100&sold-day-min=150&bought-day-min=10&ipg=25&sort=profit", "shoppinglist.csv")
```

Content of shoppinglist.csv after function has run:

```csv
item_name;rarity;sell;buy;profit;roi;supply;demand;sold;offers;bought;bids
grow lamp;exotic;20g 69s 68c;15g 01s 20c;2g 58s 03c;17.19%;778;318;150;116;39;54
superior sigil of strength;exotic;7g 43s 23c;5g 06s 53c;1g 25s 22c;24.72%;447;883;334;255;243;294
```
