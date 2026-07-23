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
    public event Func<string, Task<bool>>? MessageSubmitted;

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

    private async void Send_Click(object sender, RoutedEventArgs e) => await SubmitComposerAsync();

    private async void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        await SubmitComposerAsync();
    }

    private async Task SubmitComposerAsync()
    {
        var text = ComposerTextBox.Text;
        if (string.IsNullOrWhiteSpace(text) || MessageSubmitted is null) return;
        SetComposerEnabled(false);
        try
        {
            if (await MessageSubmitted.Invoke(text)) ComposerTextBox.Clear();
        }
        finally
        {
            SetComposerEnabled(true);
            ComposerTextBox.Focus();
        }
    }

    public void SetComposerEnabled(bool enabled)
    {
        ComposerTextBox.IsEnabled = enabled;
        SendButton.IsEnabled = enabled;
    }

    public void ScrollToLatest()
    {
        if (ConversationList.Items.Count > 0)
            ConversationList.ScrollIntoView(ConversationList.Items[ConversationList.Items.Count - 1]);
    }

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
