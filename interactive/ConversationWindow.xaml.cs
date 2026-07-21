using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AshaLive;

public partial class ConversationWindow : Window
{
    public event Action? Hidden;

    public ConversationWindow(ObservableCollection<ConversationMessage> messages)
    {
        InitializeComponent();
        ConversationList.ItemsSource = messages;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideConversation();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        HideConversation();
    }

    private void HideConversation()
    {
        Hide();
        Hidden?.Invoke();
    }
}
