using System.Windows;

namespace AshaLive;

public partial class QuitDialog : Window
{
    public QuitDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Quit_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
