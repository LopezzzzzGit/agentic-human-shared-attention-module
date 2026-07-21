using System.Collections.ObjectModel;
using System.Windows;

namespace AshaLive;

public partial class TeachingReviewWindow : Window
{
    public ObservableCollection<TeachingTimelineItem> Items { get; }
    public event Action<IReadOnlyList<TeachingTimelineItem>>? CurationSaved;

    public TeachingReviewWindow(IEnumerable<TeachingTimelineItem> recording, IEnumerable<ConversationMessage> conversation, IEnumerable<TeachingTimelineItem> attention)
    {
        InitializeComponent();
        Items = new ObservableCollection<TeachingTimelineItem>(recording.Concat(TeachingRecording.ConversationItems(conversation)).Concat(attention).OrderBy(item => item.Timestamp));
        DataContext = this;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => CurationSaved?.Invoke(Items.Where(item => item.CanInclude).ToArray());
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
