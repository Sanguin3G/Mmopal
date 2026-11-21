using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Mmopal.Contracts;
using Mmopal.Gw2;
using static Mmopal.Desktop.UiHelpers;

namespace Mmopal.Desktop.Pages;

/// <summary>Lists &amp; Blacklist: the Custom Shopping List and the item blacklist editors.</summary>
public partial class ListsPage : UserControl
{
    private readonly ObservableCollection<ItemCandidate> _customShoppingList = new();
    private readonly ObservableCollection<string> _blacklist = new();
    private MainWindow? _host;

    public ListsPage()
    {
        InitializeComponent();
        CustomShoppingListGrid.ItemsSource = _customShoppingList;
        BlacklistListBox.ItemsSource = _blacklist;
    }

    private MainWindow Host => _host ?? throw new InvalidOperationException("The page is not attached to the main window.");

    private bool Loading => _host is null || _host.IsLoadingUi;

    internal void Attach(MainWindow host) => _host = host;

    internal ObservableCollection<ItemCandidate> CustomShoppingList => _customShoppingList;

    internal ObservableCollection<string> Blacklist => _blacklist;

    private void CustomItemInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddCustomItem_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BlacklistInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddBlacklistItem_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AddCustomItem_Click(object sender, RoutedEventArgs e)
    {
        var name = NullIfBlank(CustomItemNameBox.Text);
        if (name is null) return;
        var rarity = (CustomItemRaritySelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Basic";
        var qty = long.TryParse(CustomItemQtyBox.Text.Trim(), out var parsedQty) && parsedQty > 0 ? parsedQty : 1;

        AddOrReplaceCustomCandidate(new ItemCandidate(name, Rarity: rarity, Quantity: qty));
        CustomItemNameBox.Clear();
        UpdateListCounts();
        Host.RefreshReadinessChecklist();
    }

    private void RemoveCustomItem_Click(object sender, RoutedEventArgs e)
    {
        if (CustomShoppingListGrid.SelectedItem is not ItemCandidate selected) return;
        _customShoppingList.Remove(selected);
        OperatorListsStore.SaveShoppingList(MainWindow.CustomShoppingListPath, _customShoppingList);
        UpdateListCounts();
        Host.RefreshReadinessChecklist();
    }

    private async void ImportCustomList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Custom Shopping List",
            Filter = "CSV or JSON (*.csv;*.json)|*.csv;*.json|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Host) != true) return;

        try
        {
            var loaded = await CandidatesLoader.LoadCandidatesAsync(dialog.FileName);
            foreach (var item in loaded)
            {
                AddOrReplaceCustomCandidate(item);
            }

            UpdateListCounts();
            Host.RefreshReadinessChecklist();
            Host.AddLog($"Imported {loaded.Count} items into Custom Shopping List.");
        }
        catch (Exception exception)
        {
            Host.AddLog($"ERROR importing custom list: {exception.Message}");
        }
    }

    private void ExportCustomList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Custom Shopping List CSV",
            FileName = "custom_shoppinglist.csv",
            Filter = "CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(Host) != true) return;

        OperatorListsStore.ExportShoppingListCsv(dialog.FileName, _customShoppingList);
        Host.AddLog($"Exported {_customShoppingList.Count} custom items to {dialog.FileName}.");
    }

    private void UseCustomListForSession_Click(object sender, RoutedEventArgs e)
    {
        OperatorListsStore.SaveShoppingList(MainWindow.CustomShoppingListPath, _customShoppingList);
        Host.UseCustomListOnStartCheckBox.IsChecked = true;
        Host.SaveOperatorSettingsIfValid();
        Host.RefreshReadinessChecklist();
        Host.StatusText.Text = $"Custom Shopping List ({_customShoppingList.Count} items) ready for next session.";
        Host.AddLog($"Custom Shopping List ({_customShoppingList.Count} items) enabled for Start session.");
    }

    private void AddBlacklistItem_Click(object sender, RoutedEventArgs e)
    {
        var entry = NullIfBlank(BlacklistInputBox.Text);
        if (entry is null) return;
        AddBlacklistEntry(entry);
        BlacklistInputBox.Clear();
        UpdateListCounts();
    }

    private void RemoveBlacklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (BlacklistListBox.SelectedItem is not string selected) return;
        _blacklist.Remove(selected);
        OperatorListsStore.SaveBlacklist(MainWindow.BlacklistStorePath, _blacklist);
        UpdateListCounts();
    }

    private async void ImportBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Blacklist",
            Filter = "CSV or JSON (*.csv;*.json;*.txt)|*.csv;*.json;*.txt|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Host) != true) return;

        try
        {
            var entries = await CandidatesLoader.LoadBlacklistAsync(dialog.FileName);
            foreach (var entry in entries)
            {
                AddBlacklistEntry(entry);
            }

            UpdateListCounts();
            Host.AddLog($"Imported {entries.Count} blacklist entries.");
        }
        catch (Exception exception)
        {
            Host.AddLog($"ERROR importing blacklist: {exception.Message}");
        }
    }

    private void ExportBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Blacklist CSV",
            FileName = "blacklisted_items.csv",
            Filter = "CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(Host) != true) return;

        OperatorListsStore.ExportBlacklistCsv(dialog.FileName, _blacklist);
        Host.AddLog($"Exported {_blacklist.Count} blacklist entries to {dialog.FileName}.");
    }

    internal void AddOrReplaceCustomCandidate(ItemCandidate candidate)
    {
        var normalized = ItemNameNormalizer.Normalize(candidate.ItemName);
        for (var i = 0; i < _customShoppingList.Count; i++)
        {
            if (ItemNameNormalizer.Normalize(_customShoppingList[i].ItemName) == normalized)
            {
                _customShoppingList[i] = candidate;
                OperatorListsStore.SaveShoppingList(MainWindow.CustomShoppingListPath, _customShoppingList);
                UpdateListCounts();
                return;
            }
        }

        _customShoppingList.Add(candidate);
        OperatorListsStore.SaveShoppingList(MainWindow.CustomShoppingListPath, _customShoppingList);
        UpdateListCounts();
    }

    internal void AddBlacklistEntry(string entry)
    {
        var trimmed = entry.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;
        if (_blacklist.Any(existing => existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) return;
        _blacklist.Add(trimmed);
        OperatorListsStore.SaveBlacklist(MainWindow.BlacklistStorePath, _blacklist);
        UpdateListCounts();
    }

    private void UpdateListCounts()
    {
        if (CustomListCountText is not null)
        {
            CustomListCountText.Text = $"{_customShoppingList.Count} item{(_customShoppingList.Count == 1 ? string.Empty : "s")}";
        }

        if (BlacklistCountText is not null)
        {
            BlacklistCountText.Text = $"{_blacklist.Count} blocked";
        }
    }

    internal void LoadLists(
        Func<IReadOnlyList<ItemCandidate>> loadCustomList,
        Func<IReadOnlyList<string>> loadBlacklist)
    {
        _customShoppingList.Clear();
        foreach (var item in loadCustomList()) _customShoppingList.Add(item);
        _blacklist.Clear();
        foreach (var entry in loadBlacklist()) _blacklist.Add(entry);
        UpdateListCounts();
    }
}
