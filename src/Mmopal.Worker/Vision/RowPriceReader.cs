using Mmopal.Contracts;

namespace Mmopal.Worker.Vision;

/// <summary>
/// Tells whether a list row shows a given price, such as "1 [gold] 30 [silver] 10 [copper]" in My Transactions. The coin
/// icons split the number (<see cref="CoinPriceReader"/> finds them); each number is then drawn and compared like a name
/// (<see cref="DrawnNameReader"/>). Reading those rows by digit shape alone found a price on 4 of 24 frames.
/// </summary>
public static class RowPriceReader
{
    public static bool Shows(AutomationFrame frame, CaptureRegion row, long priceCopper, double em)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (priceCopper <= 0)
        {
            return false;
        }

        var (gold, silver, copper) = (priceCopper / 10_000, priceCopper / 100 % 100, priceCopper % 100);
        // The game leaves out leading zero coins: 48s 9c is "48 [silver] 9 [copper]".
        var expected = new List<(CoinPriceReader.Coin Coin, long Value)>();
        if (gold > 0) expected.Add((CoinPriceReader.Coin.Gold, gold));
        if (gold > 0 || silver > 0) expected.Add((CoinPriceReader.Coin.Silver, silver));
        expected.Add((CoinPriceReader.Coin.Copper, copper));

        foreach (var line in CoinPriceReader.SegmentLines(frame, row))
        {
            if (line.Count != expected.Count)
            {
                continue;
            }

            var all = true;
            for (var i = 0; i < line.Count && all; i++)
            {
                var group = line[i];
                if (group.Coin != expected[i].Coin) // digits that touch ("23") are one blob, so their count says nothing
                {
                    all = false;
                    continue;
                }

                var inkLeft = group.Digits.Min(digit => digit.X);
                var inkTop = group.Digits.Min(digit => digit.Y);
                all = DrawnNameReader.CompareAtInk(frame, inkLeft, inkTop, em, expected[i].Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).IsProof;
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }
}
