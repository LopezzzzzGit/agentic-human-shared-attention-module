using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AshaLive;

public partial class SessionLibraryWindow : Window
{
    public event Action<string>? ContinueRequested;
    public event Action? NewSessionRequested;

    public SessionLibraryWindow(IReadOnlyList<SessionLibraryItem> sessions)
    {
        InitializeComponent();
        SessionList.ItemsSource = sessions;
        LibraryStatusText.Text = sessions.Count == 0 ? "No retained sessions yet." : $"{sessions.Count} retained session{(sessions.Count == 1 ? string.Empty : "s")}.";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ContinueButton.IsEnabled = SessionList.SelectedItem is SessionLibraryItem;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionLibraryItem selected) return;
        ContinueRequested?.Invoke(selected.Id);
    }

    private void NewSession_Click(object sender, RoutedEventArgs e) => NewSessionRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record SessionLibraryItem(
    string Id,
    string Title,
    string ProjectTitle,
    DateTime UpdatedAt,
    bool IsActive,
    bool IsClosed)
{
    public string ProjectAndState => $"{ProjectTitle} · {(IsActive ? "Active now" : IsClosed ? "Saved" : "Available")}";
    public string UpdatedDisplay => $"Updated {UpdatedAt:g}";
}
