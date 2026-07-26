using System.Windows;
using System.Windows.Threading;

namespace AshaLive;

public partial class ActionApprovalWindow : Window
{
    private readonly ApprovalTransaction _transaction;
    private readonly DispatcherTimer _timer;

    internal ActionApprovalWindow(ApprovalTransaction transaction)
    {
        _transaction = transaction;
        InitializeComponent();
        SummaryText.Text = transaction.Summary;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += Timer_Tick;
    }

    public bool Approved { get; private set; }

    internal void CancelFromRuntime()
    {
        Approved = false;
        if (IsVisible) DialogResult = false;
        else Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Left = Math.Max(SystemParameters.WorkArea.Left + 12, SystemParameters.WorkArea.Right - ActualWidth - 24);
        Top = SystemParameters.WorkArea.Top + 24;
        UpdateExpiry();
        _timer.Start();
        Activate();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_transaction.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            Approved = false;
            DialogResult = false;
            return;
        }
        UpdateExpiry();
    }

    private void UpdateExpiry()
    {
        var remaining = Math.Max(
            0,
            (int)Math.Ceiling((_transaction.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
        ExpiryText.Text = $"Expires in {remaining} seconds";
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_transaction.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            DialogResult = false;
            return;
        }
        Approved = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Approved = false;
        DialogResult = false;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}
