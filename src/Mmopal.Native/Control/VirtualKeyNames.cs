namespace Mmopal.Native;

public readonly record struct KeyEventPlan(ushort VirtualKey, bool KeyUp);

/// <summary>
/// Turns a chord such as <c>ctrl+shift+o</c> into virtual-key presses. It does not send input.
/// </summary>
public static class VirtualKeyNames
{
    public static IReadOnlyList<ushort> ParseChord(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw new ArgumentException("A key chord is required.", nameof(chord));
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("A key chord is required.", nameof(chord));
        var keys = new ushort[parts.Length];
        for (var i = 0; i < parts.Length; i++) keys[i] = ParseToken(parts[i]);
        return keys;
    }

    public static IReadOnlyList<KeyEventPlan> Plan(string chord)
    {
        var keys = ParseChord(chord);
        var events = new KeyEventPlan[keys.Count * 2];
        for (var i = 0; i < keys.Count; i++) events[i] = new KeyEventPlan(keys[i], false);
        for (var i = 0; i < keys.Count; i++) events[keys.Count + i] = new KeyEventPlan(keys[keys.Count - 1 - i], true);
        return events;
    }

    private static ushort ParseToken(string token)
    {
        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z') return character;
            if (character is >= '0' and <= '9') return character;
        }

        return token.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "space" => 0x20,
            "escape" or "esc" => 0x1B,
            "backspace" => 0x08,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "shift" => 0x10,
            "ctrl" or "control" => 0x11,
            "alt" => 0x12,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            _ => throw new ArgumentException($"The key '{token}' is not a supported virtual key.", nameof(token))
        };
    }
}
