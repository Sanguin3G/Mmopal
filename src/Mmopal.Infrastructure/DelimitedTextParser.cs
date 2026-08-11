namespace Mmopal.Infrastructure;

public static class DelimitedTextParser
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> Parse(
        string text,
        char delimiter = ';')
    {
        var rows = ParseRows(text, delimiter);
        if (rows.Count == 0)
        {
            return Array.Empty<IReadOnlyDictionary<string, string?>>();
        }

        var headers = rows[0];
        var result = new List<IReadOnlyDictionary<string, string?>>(Math.Max(0, rows.Count - 1));
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < headers.Count; column++)
            {
                var header = headers[column] ?? string.Empty;
                if (header.Length == 0) continue;
                values[header] = column < row.Count ? row[column] : null;
            }

            result.Add(values);
        }

        return result;
    }

    public static IReadOnlyList<IReadOnlyList<string?>> ParseRows(string text, char delimiter = ';')
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new List<IReadOnlyList<string?>>();
        var row = new List<string?>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (!quoted && c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (!quoted && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => !string.IsNullOrEmpty(value)))
                {
                    rows.Add(row.ToArray());
                }

                row.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        row.Add(field.ToString());
        if (row.Any(value => !string.IsNullOrEmpty(value)) || rows.Count == 0)
        {
            rows.Add(row.ToArray());
        }

        return rows;
    }
}
