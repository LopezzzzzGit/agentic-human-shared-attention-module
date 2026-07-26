using System.Windows;

namespace AshaLive;

public partial class TemporarySessionDialog : Window
{
    internal TemporarySessionDecision Decision { get; private set; } =
        TemporarySessionDecision.Cancel;

    public TemporarySessionDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Decision = TemporarySessionDecision.Cancel;
        DialogResult = false;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Decision = TemporarySessionDecision.Discard;
        DialogResult = true;
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Decision = TemporarySessionDecision.Keep;
        DialogResult = true;
    }
}
