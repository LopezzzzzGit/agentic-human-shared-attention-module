using System.Windows;
using System.Windows.Controls;

namespace AshaLive;

public partial class SpeechVocabularyWindow : Window
{
    private readonly SpeechVocabularyStore _store;
    private readonly SpeechVocabularyContext _context;
    private string? _selectedEntryId;

    internal SpeechVocabularyWindow(
        SpeechVocabularyStore store,
        SpeechVocabularyContext context)
    {
        InitializeComponent();
        _store = store;
        _context = context;
        ScopeComboBox.SelectedIndex = 0;
        RefreshList();
        UpdateScopeStatus();
    }

    private void RefreshList()
    {
        var selectedId = _selectedEntryId;
        var items = _store.Entries
            .OrderBy(entry => entry.Scope)
            .ThenBy(entry => entry.Canonical, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new VocabularyListItem(
                entry,
                $"{entry.Canonical}\n{ScopeLabel(entry)}" +
                (entry.Aliases.Count == 0 ? string.Empty : $"\nAliases: {string.Join(", ", entry.Aliases)}")))
            .ToArray();
        VocabularyList.ItemsSource = items;
        VocabularyList.SelectedItem = items.FirstOrDefault(item =>
            string.Equals(item.Entry.Id, selectedId, StringComparison.Ordinal));
    }

    private void VocabularyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VocabularyList.SelectedItem is not VocabularyListItem item) return;
        _selectedEntryId = item.Entry.Id;
        CanonicalTextBox.Text = item.Entry.Canonical;
        AliasesTextBox.Text = string.Join(Environment.NewLine, item.Entry.Aliases);
        ScopeComboBox.SelectedIndex = item.Entry.Scope switch
        {
            SpeechVocabularyScope.Global => 0,
            SpeechVocabularyScope.Profile => 1,
            SpeechVocabularyScope.Project => 2,
            SpeechVocabularyScope.Session => 3,
            _ => 0,
        };
        ResultStatusText.Text = "Editing this local vocabulary entry.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var scope = SelectedScope();
            var scopeId = ScopeId(scope);
            var aliases = AliasesTextBox.Text
                .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var saved = _store.Confirm(
                CanonicalTextBox.Text,
                aliases,
                scope,
                scopeId,
                _selectedEntryId);
            _selectedEntryId = saved.Id;
            ResultStatusText.Text = $"Saved {saved.Canonical} locally.";
            RefreshList();
        }
        catch (Exception error)
        {
            ResultStatusText.Text = error.Message;
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _selectedEntryId = null;
        VocabularyList.SelectedItem = null;
        CanonicalTextBox.Clear();
        AliasesTextBox.Clear();
        ScopeComboBox.SelectedIndex = 0;
        ResultStatusText.Text = "Ready for a new word.";
        CanonicalTextBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedEntryId))
        {
            ResultStatusText.Text = "Select a saved word first.";
            return;
        }
        if (MessageBox.Show(
                this,
                "Delete this local speech-vocabulary entry?",
                "Delete word",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _store.Delete(_selectedEntryId);
        New_Click(sender, e);
        ResultStatusText.Text = "The word was removed.";
        RefreshList();
    }

    private void ScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateScopeStatus();
    }

    private void UpdateScopeStatus()
    {
        var scope = SelectedScope();
        ScopeStatusText.Text = scope switch
        {
            SpeechVocabularyScope.Global => "Available in all local ASHA conversations.",
            SpeechVocabularyScope.Profile when !string.IsNullOrWhiteSpace(_context.ProfileId) =>
                $"Only in profile {_context.ProfileId}.",
            SpeechVocabularyScope.Project when !string.IsNullOrWhiteSpace(_context.ProjectId) =>
                $"Only in project {_context.ProjectId}.",
            SpeechVocabularyScope.Session when !string.IsNullOrWhiteSpace(_context.SessionId) =>
                $"Only in session {_context.SessionId}.",
            _ => "This scope is unavailable until its context is active.",
        };
    }

    private SpeechVocabularyScope SelectedScope() =>
        ScopeComboBox.SelectedItem is ComboBoxItem item &&
        Enum.TryParse<SpeechVocabularyScope>(item.Tag?.ToString(), out var scope)
            ? scope
            : SpeechVocabularyScope.Global;

    private string? ScopeId(SpeechVocabularyScope scope) => scope switch
    {
        SpeechVocabularyScope.Global => null,
        SpeechVocabularyScope.Profile => _context.ProfileId,
        SpeechVocabularyScope.Project => _context.ProjectId,
        SpeechVocabularyScope.Session => _context.SessionId,
        _ => null,
    };

    private static string ScopeLabel(SpeechVocabularyEntry entry) =>
        entry.Scope == SpeechVocabularyScope.Global
            ? "Global"
            : $"{entry.Scope}: {entry.ScopeId}";

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record VocabularyListItem(SpeechVocabularyEntry Entry, string Display);
}
