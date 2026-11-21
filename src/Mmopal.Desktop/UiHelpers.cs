using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mmopal.Desktop;

/// <summary>Small UI helpers shared by the main window and its pages.</summary>
internal static class UiHelpers
{
    internal static Brush ResolveBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) is Brush b ? b : fallback;

    internal static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool IsSelectedTag(ComboBox? combo, string tag) =>
        string.Equals((combo?.SelectedItem as ComboBoxItem)?.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);

    internal static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem comboItem && string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = comboItem;
                return;
            }
        }
    }

    internal static void SelectComboByContent(ComboBox combo, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    internal static void SelectComboByTag(ComboBox combo, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Shows <paramref name="temporaryText"/> on the button for a moment, then puts its content back.</summary>
    internal static async Task FlashButtonFeedbackAsync(Button? button, string temporaryText, int durationMs = 1600)
    {
        if (button is null) return;
        var dispatcher = button.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        object? originalContent = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                originalContent = button.Content;
                button.Content = temporaryText;
            });

            await Task.Delay(durationMs).ConfigureAwait(false);

            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            _ = dispatcher.InvokeAsync(() =>
            {
                if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    button.Content = originalContent;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
