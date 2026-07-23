using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfLine = System.Windows.Shapes.Line;
using WpfPolyline = System.Windows.Shapes.Polyline;
using SharedInference;

namespace AshaLive;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int WmNchittest = 0x0084;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmKeyDown = 0x0100;
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const uint VkEscape = 0x1B;
    private const int GwlpHwndParent = -8;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const int HtTransparent = -1;
    private const int MarkHotkeyId = 20260720;
    private const int MoveHotkeyId = 20260721;
    private const int ControlsHotkeyId = 20260722;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkA = 0x41;
    private const uint VkM = 0x4D;
    private const uint GaRoot = 2;
    private const string PersonalDesktopProjectId = "personal-desktop";
    private const string ControlPresenceMarkId = "asha-control-presence";
    private readonly ObservableCollection<LiveMark> _marks = [];
    private readonly ObservableCollection<ConversationMessage> _conversationMessages = [];
    private readonly MicrophoneCapture _microphone = new();
    private readonly AshaVoiceSession _voiceSession = new();
    private readonly ScreenObserver _screenObserver = new();
    private readonly DesktopAwarenessCoordinator _awarenessCoordinator = new();
    private readonly AshaPreferences _preferences;
    private readonly DispatcherTimer _tapToListenTimer;
    private readonly DispatcherTimer _conversationBoundaryTimer;
    private readonly DispatcherTimer _cueEditEventTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly string _repositoryRoot;
    private ConversationWindow? _conversationWindow;
    private SessionLibraryWindow? _sessionLibraryWindow;
    private HwndSource? _source;
    private bool _markHotkeyRegistered;
    private Point _dragOrigin;
    private bool _dragPending;
    // The controls live in WPF's separate Popup window.  Its local coordinates
    // change while it moves, so dragging must use stable desktop coordinates.
    private Point _controlsDragOriginScreen;
    private double _controlsStartHorizontalOffset;
    private double _controlsStartVerticalOffset;
    private bool _controlsDragInProgress;
    private bool _controlsPanelManuallyPositioned;
    private bool _cueEditing;
    private bool _processingCueEditEvents;
    private bool _labelCommitInProgress;
    private string? _labelEditingCueId;
    private string? _labelEditingText;
    private IntPtr _cueDrawHook;
    private LowLevelMouseProc? _cueDrawMouseProc;
    private IntPtr _cueDrawKeyboardHook;
    private LowLevelKeyboardProc? _cueDrawKeyboardProc;
    private bool _cueDrawingArmed;
    private bool _cueDrawingStarted;
    private string? _cueDrawingKind;
    private NativePoint _cueDrawingStart;
    private CueDrawingPreviewWindow? _cueDrawingPreview;
    private bool _conversationActive;
    private bool _voiceCapturing;
    private bool _voiceTurnInFlight;
    private bool _heardSpeech;
    private int _speechCandidateFrames;
    private double _ambientMicrophoneEnergy = 0.001;
    private DateTime _lastSpeechAtUtc;
    private bool _reallyQuitting;
    private bool _shownTrayHint;
    private bool _applyingPreferences;
    private ComputerControlLease? _controlLease;
    private Process? _teachRecorder;
    private string? _latestRecordingPath;
    private string? _latestCuratedRecordingPath;
    private TeachingReviewWindow? _teachingReviewWindow;
    private string? _activeSessionId;
    private string? _activeSessionTitle;
    private bool _activeSessionNeedsTitle;
    private VisualEvidenceBundle? _latestVisionEvidence;
    private bool _shareVisionOnNextTurn;
    private DesktopAwarenessContext? _liveAwarenessContext;
    private LocalScreenChange? _pendingLiveScreenChange;
    private bool _liveAwarenessRefreshInFlight;
    private DateTime _lastLiveAwarenessRefreshUtc;
    private CancellationTokenSource? _voiceTurnCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _sessionWriteGate = new(1, 1);
    private readonly SemaphoreSlim _memoryRefreshGate = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        _preferences = AshaPreferences.Load();
        _activeSessionId = _preferences.ActiveSessionId;
        MarkList.ItemsSource = _marks;
        AttachedChatList.ItemsSource = _conversationMessages;
        AttachedChatPanel.Visibility = Visibility.Collapsed;
        OpenChatButton.Visibility = Visibility.Visible;
        _repositoryRoot = FindRepositoryRoot();
        _trayIcon = CreateTrayIcon();
        StatusText.Text = _voiceSession.IsGroqConfigured
            ? "Tap the orb to start listening."
            : "Configure Groq, then restart ASHA.";
        ProviderStatusText.Text = DescribeProvider();
        ApplyPreferencesToUi();
        OrbSurface.SetPresenceState(OrbPresenceState.Idle);
        // Wait out Windows' double-click interval so the familiar double-click
        // menu gesture never accidentally becomes a voice turn.
        _tapToListenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime + 40) };
        _tapToListenTimer.Tick += TapToListenTimer_Tick;
        _conversationBoundaryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _conversationBoundaryTimer.Tick += ConversationBoundaryTimer_Tick;
        _cueEditEventTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _cueEditEventTimer.Tick += CueEditEventTimer_Tick;
        _microphone.EnergyChanged += energy => Dispatcher.BeginInvoke(() =>
        {
            if (!_voiceCapturing) return;
            OrbSurface.SetAudioEnergy(energy);
            ObserveIncomingSpeech(energy);
        });
        _awarenessCoordinator.SceneChanged += scene => Dispatcher.BeginInvoke(() => ShowAwarenessScene(scene));
        _screenObserver.MeaningfulChange += change => Dispatcher.BeginInvoke(() => QueueLiveAwarenessRefresh(change));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        if (_source is null) return;
        _source.AddHook(WindowMessageHook);
        _markHotkeyRegistered = RegisterHotKey(_source.Handle, MarkHotkeyId, ModControl | ModAlt, VkM);
        RegisterHotKey(_source.Handle, MoveHotkeyId, ModControl | ModAlt | ModShift, VkM);
        RegisterHotKey(_source.Handle, ControlsHotkeyId, ModControl | ModAlt, VkA);
        if (!_markHotkeyRegistered) StatusText.Text = "Ctrl+Alt+M is already in use. Free it to place a visual cue.";
        // Computer-control permission is intentionally process-local. Its
        // presence frame must therefore never survive a restart and imply a
        // permission that is no longer active.
        try { await RunAshaAsync("clear", ControlPresenceMarkId); }
        catch (Exception error) { Log($"Could not clear stale control-presence frame: {error.Message}"); }
        _ = RestoreActiveSessionAsync();
    }

    private IntPtr WindowMessageHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNchittest && IsOutsideOrb(lParam))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        if (message != WmHotkey) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case MarkHotkeyId:
                if (_cueDrawingArmed)
                    CancelCueDrawing("Cue drawing cancelled.");
                else if (SelectedCueKind() is "box" or "arrow")
                    BeginCueDrawing(SelectedCueKind());
                else
                    _ = MarkAtCurrentMouseAsync();
                handled = true;
                break;
            case MoveHotkeyId:
                _ = MoveSelectedToCurrentMouseAsync();
                handled = true;
                break;
            case ControlsHotkeyId:
                ToggleControls();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOutsideOrb(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
        var point = PointFromScreen(screen);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        const double hitRadius = 122;
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return (dx * dx) + (dy * dy) > hitRadius * hitRadius;
    }

    private void Orb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _tapToListenTimer.Stop();
            OrbSurface.ReleaseMouseCapture();
            ToggleControls();
            e.Handled = true;
            return;
        }

        // In free-conversation mode the centre is an explicit end-session
        // control. ASHA decides individual turn boundaries from your speech;
        // the person no longer has to tap after every sentence.
        if (_conversationActive)
        {
            if (IsInOrbCore(e.GetPosition(OrbSurface)))
            {
                _tapToListenTimer.Stop();
                _ = EndConversationAsync();
                e.Handled = true;
                return;
            }

            e.Handled = true;
            return;
        }

        _dragOrigin = e.GetPosition(this);
        _dragPending = true;
        OrbSurface.CaptureMouse();
        e.Handled = true;
    }

    private void Orb_MouseMove(object sender, MouseEventArgs e)
    {
        if (_voiceCapturing) return;
        if (!_dragPending || !OrbSurface.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        var delta = point - _dragOrigin;
        if (Math.Abs(delta.X) <= 3 && Math.Abs(delta.Y) <= 3) return;

        // Let Windows own the actual move loop. Moving a transparent WPF
        // window from coordinates relative to itself produces a feedback loop
        // and visible jitter.
        _tapToListenTimer.Stop();
        _dragPending = false;
        OrbSurface.ReleaseMouseCapture();
        try { DragMove(); }
        catch (InvalidOperationException) { /* mouse was released before the native loop began */ }
    }

    private static bool IsInOrbCore(Point point)
    {
        const double coreRadius = 54;
        const double surfaceCenter = 122;
        var dx = point.X - surfaceCenter;
        var dy = point.Y - surfaceCenter;
        return (dx * dx) + (dy * dy) <= coreRadius * coreRadius;
    }

    private void Orb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_voiceCapturing) return;
        if (OrbSurface.IsMouseCaptured) OrbSurface.ReleaseMouseCapture();
        if (!_dragPending) return;
        _dragPending = false;
        // Wait briefly so a double-click can continue to open the menu rather
        // than starting a voice turn on its first click.
        _tapToListenTimer.Start();
        e.Handled = true;
    }

    private void Orb_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ToggleControls();
        e.Handled = true;
    }

    private void ToggleControls()
    {
        if (ControlsPopup.IsOpen)
        {
            ControlsPopup.IsOpen = false;
            return;
        }

        // Open after the initiating mouse gesture has completely finished.
        // A transparent top-level window otherwise lets that same gesture be
        // observed as an immediate outside-click by the popup layer.
        Dispatcher.BeginInvoke(OpenControlsInDesktopCorner);
    }

    private void OpenControlsInDesktopCorner()
    {
        ControlsPopup.Placement = PlacementMode.AbsolutePoint;
        if (!_controlsPanelManuallyPositioned)
        {
            ControlsPopup.HorizontalOffset = Math.Max(14, SystemParameters.WorkArea.Right - 360);
            ControlsPopup.VerticalOffset = SystemParameters.WorkArea.Top + 18;
        }
        ControlsPopup.IsOpen = true;
    }

    private void ControlsPopup_Opened(object sender, EventArgs e)
    {
        // A WPF Popup is normally owned by the orb's native window. Windows
        // guarantees owned windows appear above their owner, which is exactly
        // the wrong order here. Detach this deliberately independent menu,
        // then arrange both windows in the topmost band with the orb last.
        Dispatcher.BeginInvoke(BringOrbAboveControls, DispatcherPriority.ApplicationIdle);
    }

    private void BringOrbAboveControls()
    {
        if (_source is null || !IsVisible) return;

        const uint flags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow;
        if (ControlsPopup.Child is not null && PresentationSource.FromVisual(ControlsPopup.Child) is HwndSource popupSource)
        {
            _ = SetWindowLongPtr(popupSource.Handle, GwlpHwndParent, IntPtr.Zero);
            var exStyle = GetWindowLongPtr(popupSource.Handle, GwlExStyle).ToInt64();
            _ = SetWindowLongPtr(popupSource.Handle, GwlExStyle, new IntPtr(exStyle & ~WsExNoActivate));
            _ = SetWindowPos(popupSource.Handle, HwndTopmost, 0, 0, 0, 0, flags);
        }

        _ = SetWindowPos(_source.Handle, HwndTopmost, 0, 0, 0, 0, flags);
    }

    private void ControlsPopup_ContentMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Keep the keyboard focus in the popup (for text inputs), then put the
        // transparent orb visual back in front without activating it.
        Dispatcher.BeginInvoke(BringOrbAboveControls, DispatcherPriority.ApplicationIdle);
    }

    private void HideControls_Click(object sender, RoutedEventArgs e) => ControlsPopup.IsOpen = false;

    private void ControlsHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ControlsPopup.IsOpen) return;
        _controlsDragInProgress = true;
        _controlsDragOriginScreen = ControlsDragHandle.PointToScreen(e.GetPosition(ControlsDragHandle));
        _controlsStartHorizontalOffset = ControlsPopup.HorizontalOffset;
        _controlsStartVerticalOffset = ControlsPopup.VerticalOffset;
        ControlsDragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void ControlsHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_controlsDragInProgress || e.LeftButton != MouseButtonState.Pressed) return;

        var current = ControlsDragHandle.PointToScreen(e.GetPosition(ControlsDragHandle));
        var workArea = SystemParameters.WorkArea;
        // Keep the heading reachable even if the panel is taller than the screen.
        ControlsPopup.HorizontalOffset = Math.Clamp(_controlsStartHorizontalOffset + current.X - _controlsDragOriginScreen.X, workArea.Left + 10, workArea.Right - 80);
        ControlsPopup.VerticalOffset = Math.Clamp(_controlsStartVerticalOffset + current.Y - _controlsDragOriginScreen.Y, workArea.Top + 8, workArea.Bottom - 46);
        _controlsPanelManuallyPositioned = true;
    }

    private void ControlsHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_controlsDragInProgress) return;
        _controlsDragInProgress = false;
        if (ControlsDragHandle.IsMouseCaptured) ControlsDragHandle.ReleaseMouseCapture();
        BringOrbAboveControls();
        e.Handled = true;
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e)
    {
        AttachedChatPanel.Visibility = Visibility.Visible;
        OpenChatButton.Visibility = Visibility.Collapsed;
    }

    private void CloseAttachedChat_Click(object sender, RoutedEventArgs e)
    {
        AttachedChatPanel.Visibility = Visibility.Collapsed;
        OpenChatButton.Visibility = Visibility.Visible;
    }

    private void DetachChat_Click(object sender, RoutedEventArgs e)
    {
        AttachedChatPanel.Visibility = Visibility.Collapsed;
        OpenChatButton.Visibility = Visibility.Collapsed;

        _conversationWindow ??= CreateConversationWindow();
        if (!_conversationWindow.IsVisible)
        {
            _conversationWindow.Left = Math.Max(SystemParameters.WorkArea.Left + 18, SystemParameters.WorkArea.Right - _conversationWindow.Width - 26);
            _conversationWindow.Top = Math.Max(SystemParameters.WorkArea.Top + 18, SystemParameters.WorkArea.Bottom - _conversationWindow.Height - 38);
            _conversationWindow.Show();
        }
        _conversationWindow.Activate();
    }

    private ConversationWindow CreateConversationWindow()
    {
        var window = new ConversationWindow(_conversationMessages) { Topmost = Topmost };
        window.Hidden += () => Dispatcher.BeginInvoke(() => OpenChatButton.Visibility = Visibility.Visible);
        window.MessageSubmitted += SubmitTypedTurnAsync;
        return window;
    }

    private async void AttachedSend_Click(object sender, RoutedEventArgs e) => await SubmitAttachedComposerAsync();

    private async void AttachedComposer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        await SubmitAttachedComposerAsync();
    }

    private async Task SubmitAttachedComposerAsync()
    {
        var text = AttachedComposerTextBox.Text;
        if (await SubmitTypedTurnAsync(text)) AttachedComposerTextBox.Clear();
        AttachedComposerTextBox.Focus();
    }

    private void SetComposerBusy(bool busy)
    {
        AttachedComposerTextBox.IsEnabled = !busy;
        AttachedSendButton.IsEnabled = !busy;
        _conversationWindow?.SetComposerEnabled(!busy);
    }

    private void AttachedChatResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        AttachedChatPanel.Height = Math.Clamp(AttachedChatPanel.Height + e.VerticalChange, 170, 540);
    }

    private void Profile_Click(object sender, RoutedEventArgs e) => TogglePanel(ProfilePanel, SettingsPanel);

    private async void Sessions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sessions = await ReadSessionLibraryAsync();
            _sessionLibraryWindow?.Close();
            var window = new SessionLibraryWindow(sessions) { Owner = this, Topmost = Topmost };
            _sessionLibraryWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_sessionLibraryWindow, window)) _sessionLibraryWindow = null;
            };
            window.ContinueRequested += sessionId => _ = ContinueSessionAsync(sessionId, window);
            window.NewSessionRequested += () => _ = StartNewSessionFromLibraryAsync(window);
            window.Left = Math.Max(SystemParameters.WorkArea.Left + 18, SystemParameters.WorkArea.Right - window.Width - 28);
            window.Top = Math.Max(SystemParameters.WorkArea.Top + 18, SystemParameters.WorkArea.Top + 74);
            window.Show();
            window.Activate();
        }
        catch (Exception error)
        {
            SessionStatusText.Text = $"Could not open sessions: {ShortReason(error)}";
            Log($"Session library error: {error.Message}");
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => TogglePanel(SettingsPanel, ProfilePanel);

    private void TogglePanel(FrameworkElement target, FrameworkElement other)
    {
        var shouldShow = target.Visibility != Visibility.Visible;
        target.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        other.Visibility = Visibility.Collapsed;
    }

    private void ProfileChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value } || !Enum.TryParse<AshaProfile>(value, out var profile)) return;
        _preferences.Profile = profile;
        _preferences.Save();
        ApplyPreferencesToUi();
        Log($"Profile selected: {ProfileDisplayName(profile)}.");
        StatusText.Text = ProfileStatus(profile);
    }

    private async void Session_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_activeSessionId))
        {
            await EndCurrentSessionAsync();
            return;
        }

        await StartNewSessionAsync();
    }

    private async Task StartNewSessionAsync()
    {
        try
        {
            try { await RunAshaAsync("project", "create", "Personal desktop", "--id", PersonalDesktopProjectId); }
            catch { /* The local default project normally already exists. */ }

            var sessionId = $"desktop-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..31];
            var title = $"Shared attention {DateTime.Now:yyyy-MM-dd HH:mm}";
            await RunAshaAsync("session", "start", "--project", PersonalDesktopProjectId, "--title", title, "--id", sessionId);
            _conversationMessages.Clear();
            _voiceSession.ResetConversationMemory();
            SetActiveSession(sessionId, title);
            _activeSessionNeedsTitle = true;
            await RecordActiveSessionEventAsync("session.started", "A new local shared-attention session started.");
            StatusText.Text = "Session memory is active. Talk, point, or start a teaching demonstration.";
            Log("New retained session started; its full transcript and semantic log remain local.");
        }
        catch (Exception error)
        {
            SessionStatusText.Text = ShortReason(error);
            Log($"Could not start the shared-attention session: {error.Message}");
        }
    }

    private async Task EndCurrentSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId)) return;
        if (_teachRecorder is { HasExited: false })
        {
            SessionStatusText.Text = "Finish the teaching recording with Esc before ending this session.";
            return;
        }

        var endingId = _activeSessionId;
        try
        {
            if (_controlLease is not null)
                await StopControlLeaseAsync(
                    "The active computer-control lease ended with its shared-attention session.");
            await RefreshSessionMemoryAsync(endingId, forceCompression: true);
            await RecordActiveSessionEventAsync("session.ended", "The person ended this shared-attention session.");
            await _sessionWriteGate.WaitAsync();
            try { await RunAshaAsync("session", "close", endingId); }
            finally { _sessionWriteGate.Release(); }

            ClearActiveSession();
            SessionStatusText.Text = "Session saved and closed. Open Sessions whenever you want to continue it.";
            StatusText.Text = _voiceSession.IsGroqConfigured ? "Session saved. Tap the orb to begin a new one." : "Configure Groq, then restart ASHA.";
            Log("Shared-attention session saved and closed.");
        }
        catch (Exception error)
        {
            SessionStatusText.Text = ShortReason(error);
            Log($"Could not end the shared-attention session: {error.Message}");
        }
    }

    private async Task StartNewSessionFromLibraryAsync(SessionLibraryWindow window)
    {
        if (!string.IsNullOrWhiteSpace(_activeSessionId)) await EndCurrentSessionAsync();
        if (!string.IsNullOrWhiteSpace(_activeSessionId)) return;
        await StartNewSessionAsync();
        window.Close();
    }

    private async Task ContinueSessionAsync(string sessionId, SessionLibraryWindow window)
    {
        try
        {
            if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                window.Close();
                SessionStatusText.Text = $"Active now: {_activeSessionTitle}.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_activeSessionId)) await EndCurrentSessionAsync();
            if (!string.IsNullOrWhiteSpace(_activeSessionId)) return;

            var result = await RunAshaAsync("session", "resume", sessionId);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var session = document.RootElement.GetProperty("session");
            var title = session.TryGetProperty("title", out var titleValue)
                ? titleValue.GetString() ?? "Saved ASHA session"
                : "Saved ASHA session";
            SetActiveSession(sessionId, title);
            _activeSessionNeedsTitle = title.StartsWith("Shared attention ", StringComparison.OrdinalIgnoreCase);
            await LoadSessionMemoryAsync(sessionId);
            await RecordActiveSessionEventAsync("session.resumed", "The person continued this retained shared-attention session.");
            SessionStatusText.Text = $"Continuing: {title}. Full history is available and its memory is loaded.";
            StatusText.Text = "Session restored. Tap the orb to continue the conversation.";
            Log($"Continued retained session: {title}.");
            window.Close();
        }
        catch (Exception error)
        {
            SessionStatusText.Text = $"Could not continue session: {ShortReason(error)}";
            Log($"Continue-session error: {error.Message}");
        }
    }

    private async Task<IReadOnlyList<SessionLibraryItem>> ReadSessionLibraryAsync()
    {
        var projectTask = RunAshaAsync("project", "list");
        var sessionTask = RunAshaAsync("session", "list");
        await Task.WhenAll(projectTask, sessionTask);

        using var projectDocument = JsonDocument.Parse(projectTask.Result.StandardOutput);
        using var sessionDocument = JsonDocument.Parse(sessionTask.Result.StandardOutput);
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projectDocument.RootElement.GetProperty("projects").EnumerateArray())
        {
            var id = project.GetProperty("id").GetString();
            var title = project.GetProperty("title").GetString();
            if (!string.IsNullOrWhiteSpace(id)) projects[id] = title ?? id;
        }

        var items = new List<SessionLibraryItem>();
        foreach (var session in sessionDocument.RootElement.GetProperty("sessions").EnumerateArray())
        {
            var id = session.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var projectId = session.GetProperty("projectId").GetString() ?? PersonalDesktopProjectId;
            var title = session.GetProperty("title").GetString() ?? "Untitled ASHA session";
            var updated = session.TryGetProperty("updatedAt", out var updatedValue) && DateTime.TryParse(updatedValue.GetString(), out var parsed)
                ? parsed.ToLocalTime()
                : DateTime.Now;
            var closed = session.TryGetProperty("closedAt", out var closedValue) && closedValue.ValueKind == JsonValueKind.String;
            items.Add(new SessionLibraryItem(
                id,
                title,
                projects.TryGetValue(projectId, out var projectTitle) ? projectTitle : projectId,
                updated,
                string.Equals(id, _activeSessionId, StringComparison.Ordinal),
                closed));
        }
        return items;
    }

    private async void StartTeaching_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId))
        {
            TeachingStatusText.Text = "Start a shared-attention session first, so this demonstration has its conversation and visual context.";
            return;
        }

        if (_teachRecorder is { HasExited: false })
        {
            TeachingStatusText.Text = "Teaching is already recording. Press Esc anywhere to finish it.";
            return;
        }

        var executable = Path.Combine(_repositoryRoot, "teach-recorder", "bin", "Release", "net8.0-windows", "asha-teach-recorder.exe");
        if (!File.Exists(executable))
        {
            TeachingStatusText.Text = "The teaching recorder is not built yet.";
            return;
        }

        _preferences.Profile = AshaProfile.Teach;
        _preferences.Save();
        ApplyPreferencesToUi();

        var recordingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asha", "recordings");
        Directory.CreateDirectory(recordingDirectory);
        _latestRecordingPath = Path.Combine(recordingDirectory, $"teaching-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        _latestCuratedRecordingPath = null;
        _teachingReviewWindow?.Close();
        _teachingReviewWindow = null;

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _teachRecorder = Process.Start(start) ?? throw new InvalidOperationException("Could not start the teaching recorder.");
            var recorder = _teachRecorder;
            StartTeachingButton.IsEnabled = false;
            ReviewTeachingButton.IsEnabled = false;
            CompileTeachingButton.IsEnabled = false;
            TeachingStatusText.Text = "Teaching is live. Draw visual cues for important places; F8 is optional pointing, F9 is demonstration, and Esc finishes.";
            StatusText.Text = "Teach ASHA is recording privately. Press Esc when your demonstration is complete.";
            Log("Teaching recorder started; raw events remain local until you make a procedure.");
            await RecordTeachingLifecycleAsync("teaching.recording_started", "Started a private mechanical teaching recording.");

            _ = CollectTeachingRecordingAsync(recorder, _latestRecordingPath);
        }
        catch (Exception error)
        {
            TeachingStatusText.Text = ShortReason(error);
            StartTeachingButton.IsEnabled = true;
            Log($"Teaching recorder error: {error.Message}");
        }
    }

    private async Task CollectTeachingRecordingAsync(Process recorder, string recordingPath)
    {
        var eventCount = 0;
        try
        {
            while (await recorder.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                await File.AppendAllTextAsync(recordingPath, line + Environment.NewLine);
                eventCount++;
            }
            var error = await recorder.StandardError.ReadToEndAsync();
            await recorder.WaitForExitAsync();
            var completed = recorder.ExitCode == 0;
            var imported = 0;
            if (completed)
            {
                imported = await ImportTeachingRecordingAsync(recordingPath);
                await RecordTeachingLifecycleAsync("teaching.recording_completed", $"Saved {eventCount} private teaching events and added {imported} timeline references for review.");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _teachRecorder = null;
                StartTeachingButton.IsEnabled = true;
                ReviewTeachingButton.IsEnabled = completed && eventCount > 1;
                CompileTeachingButton.IsEnabled = false;
                TeachingStatusText.Text = completed
                    ? $"Saved {eventCount} private events. Review the timeline before creating a draft."
                    : $"Teaching recorder stopped unexpectedly: {ShortReason(new InvalidOperationException(error))}";
                StatusText.Text = completed
                    ? $"Teaching saved locally with {imported} session timeline references."
                    : "Teaching did not complete.";
                Log(completed ? $"Teaching recording saved ({eventCount} events; {imported} linked to the session timeline)." : $"Teaching recorder stopped: {error}");
            });
        }
        catch (Exception error)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _teachRecorder = null;
                StartTeachingButton.IsEnabled = true;
                TeachingStatusText.Text = ShortReason(error);
                Log($"Teaching capture error: {error.Message}");
            });
        }
    }

    private async void CompileTeaching_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_latestCuratedRecordingPath) || !File.Exists(_latestCuratedRecordingPath))
        {
            TeachingStatusText.Text = "Review and save a teaching timeline first.";
            return;
        }

        try
        {
            CompileTeachingButton.IsEnabled = false;
            var title = $"Reviewed desktop demonstration {DateTime.Now:yyyy-MM-dd HH:mm}";
            var compiled = await RunAshaAsync("compile", _latestCuratedRecordingPath, "--title", title);
            using var document = JsonDocument.Parse(compiled.StandardOutput);
            var steps = document.RootElement.TryGetProperty("steps", out var resultSteps) && resultSteps.ValueKind == JsonValueKind.Array
                ? resultSteps.GetArrayLength()
                : 0;
            var recipeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asha", "recipes");
            Directory.CreateDirectory(recipeDirectory);
            var recipePath = Path.Combine(recipeDirectory, $"procedure-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(recipePath, JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            await RecordTeachingLifecycleAsync("procedure.drafted", $"Created reviewed local draft {Path.GetFileName(recipePath)} with {steps} steps.");
            TeachingStatusText.Text = $"Reviewed draft created: {steps} steps. It remains a proposal until you approve its use.";
            StatusText.Text = "ASHA created a reviewed procedure draft.";
            Log($"Created a reviewed {steps}-step procedure draft.");
        }
        catch (Exception error)
        {
            TeachingStatusText.Text = ShortReason(error);
            Log($"Procedure compiler error: {error.Message}");
        }
        finally
        {
            CompileTeachingButton.IsEnabled = true;
        }
    }

    private async void ReviewTeaching_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId) && _conversationMessages.Count == 0 && (string.IsNullOrWhiteSpace(_latestRecordingPath) || !File.Exists(_latestRecordingPath)))
        {
            TeachingStatusText.Text = "Start a session, talk, point, or record a demonstration before opening review.";
            return;
        }

        if (_teachingReviewWindow is { IsVisible: true })
        {
            _teachingReviewWindow.Activate();
            return;
        }

        var recording = !string.IsNullOrWhiteSpace(_latestRecordingPath) && File.Exists(_latestRecordingPath)
            ? TeachingRecording.Read(_latestRecordingPath)
            : Array.Empty<TeachingTimelineItem>();
        var conversation = string.IsNullOrWhiteSpace(_activeSessionId)
            ? _conversationMessages.ToArray()
            : await SessionTranscriptStore.ReadAsync(_activeSessionId);
        var attention = await ReadSessionAttentionItemsAsync();
        var window = new TeachingReviewWindow(recording, conversation, attention) { Owner = this };
        _teachingReviewWindow = window;
        window.Closed += (_, _) => _teachingReviewWindow = null;
        window.CurationSaved += items => _ = SaveTeachingCurationAsync(items);
        window.Show();
    }

    private async Task SaveTeachingCurationAsync(IReadOnlyList<TeachingTimelineItem> items)
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId)) return;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asha", "curations");
            Directory.CreateDirectory(directory);
            var stem = $"teaching-{DateTime.Now:yyyyMMdd-HHmmss}";
            var curated = Path.Combine(directory, $"{stem}.jsonl");
            var manifest = Path.Combine(directory, $"{stem}.review.json");
            var actionCount = await TeachingRecording.SaveCuratedCandidateRecordingAsync(curated, items);
            var source = !string.IsNullOrWhiteSpace(_latestRecordingPath) && File.Exists(_latestRecordingPath)
                ? _latestRecordingPath
                : $"session-{_activeSessionId}-timeline";
            await TeachingRecording.SaveCurationManifestAsync(manifest, source, _activeSessionId, items);
            _latestCuratedRecordingPath = curated;
            CompileTeachingButton.IsEnabled = actionCount > 0;
            TeachingStatusText.Text = actionCount > 0
                ? $"Review saved: {actionCount} demonstrated action(s) are ready for a draft."
                : "Review saved. Keep at least one demonstration action to create a draft.";
            await RecordTeachingLifecycleAsync("teaching.curated", $"Saved a teaching review with {items.Count(item => item.Include)} retained evidence item(s) and {actionCount} demonstrated action(s).");
            Log($"Teaching review saved ({actionCount} candidate action(s)).");
        }
        catch (Exception error)
        {
            TeachingStatusText.Text = ShortReason(error);
            Log($"Could not save teaching review: {error.Message}");
        }
    }

    private async Task<int> ImportTeachingRecordingAsync(string recordingPath)
    {
        var sessionId = _activeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return 0;
        var events = TeachingRecording.ToSemanticEvents(TeachingRecording.Read(recordingPath)).ToArray();
        if (events.Length == 0) return 0;

        await _sessionWriteGate.WaitAsync();
        try
        {
            await RunAshaAsync("session", "record-many", sessionId, JsonSerializer.Serialize(events, JsonOptions));
            return events.Length;
        }
        catch (Exception error)
        {
            await Dispatcher.InvokeAsync(() => Log($"Could not link raw teaching events to the session: {error.Message}"));
            return 0;
        }
        finally
        {
            _sessionWriteGate.Release();
        }
    }

    private async Task<IReadOnlyList<TeachingTimelineItem>> ReadSessionAttentionItemsAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId)) return [];
        try
        {
            var result = await RunAshaAsync("session", "show", _activeSessionId);
            using var document = JsonDocument.Parse(result.StandardOutput);
            return TeachingRecording.AttentionItems(document.RootElement).ToArray();
        }
        catch (Exception error)
        {
            Log($"Could not load visual teaching evidence for review: {error.Message}");
            return [];
        }
    }

    private async Task RecordTeachingLifecycleAsync(string type, string note)
    {
        await RecordActiveSessionEventAsync(
            type,
            note,
            "human",
            "teach_procedure",
            new { app = "desktop", label = "ASHA mechanical teaching", control = "procedure" });
    }

    private async void ControlSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_controlLease is not null)
            {
                await StopControlLeaseAsync("The person stopped the computer-control session.");
                return;
            }

            if (!ComputerControlLease.TryStart(
                    _preferences.ComputerControl,
                    _activeSessionId,
                    out var lease,
                    out var reason) ||
                lease is null)
            {
                ControlSessionStatusText.Text = reason;
                if (_preferences.ComputerControl.AllowedCapabilities == ComputerControlCapability.None)
                {
                    SettingsPanel.Visibility = Visibility.Visible;
                    ProfilePanel.Visibility = Visibility.Collapsed;
                    ComputerControlPolicyStatusText.Text = "Choose what ASHA may use, then start the temporary control session again.";
                }
                return;
            }

            _preferences.Profile = AshaProfile.Assist;
            _preferences.Save();
            ApplyPreferencesToUi();
            if (_preferences.ShowControlPresence)
                await ShowControlPresenceFrameAsync();

            _controlLease = lease;
            UpdateControlLeaseUi();
            StatusText.Text = "ASHA computer control is enabled for this session.";
            var access = CurrentControlAccess();
            Log($"Computer-control lease started: {access.DescribeForPerson()}");
            await RecordActiveSessionEventAsync(
                "control.lease_started",
                "The person explicitly started a temporary, human-facing computer-control lease.",
                "human",
                "computer_control",
                new
                {
                    app = "desktop",
                    label = "ASHA computer control",
                    control = "lease",
                    leaseId = lease.Id,
                    capabilities = access.EffectiveCapabilities.ToString(),
                });
        }
        catch (Exception error)
        {
            ControlSessionStatusText.Text = ShortReason(error);
            Log($"Desktop-control session error: {error.Message}");
        }
    }

    private ComputerControlAccess CurrentControlAccess() =>
        new(_preferences.ComputerControl, _controlLease, _activeSessionId);

    private async Task StopControlLeaseAsync(string reason, bool recordEvent = true)
    {
        var lease = _controlLease;
        if (lease is null) return;
        _controlLease = null;

        try { await RunAshaAsync("clear", ControlPresenceMarkId); }
        catch (Exception error) { Log($"Could not clear the computer-control presence frame: {error.Message}"); }

        UpdateControlLeaseUi();
        StatusText.Text = _voiceSession.IsGroqConfigured ? "Tap the orb to start listening." : "Configure Groq, then restart ASHA.";
        Log("Computer-control lease ended.");
        if (recordEvent)
        {
            await RecordActiveSessionEventAsync(
                "control.lease_ended",
                reason,
                "human",
                "computer_control",
                new
                {
                    app = "desktop",
                    label = "ASHA computer control",
                    control = "lease",
                    leaseId = lease.Id,
                });
        }
    }

    private void UpdateControlLeaseUi()
    {
        var access = CurrentControlAccess();
        ControlSessionButton.Content = _controlLease is null ? "Enable computer control" : "Stop computer control";
        ControlSessionStatusText.Text = access.IsLeaseActive
            ? $"{access.DescribeForPerson()} {(_preferences.ShowControlPresence ? "The blue-violet frame shows that control is active." : "The desktop presence frame is hidden by your setting.")}"
            : _controlLease is null
                ? "Computer control is disabled."
                : "The active lease no longer contains an allowed capability. Stop it or enable a new lease from the current policy.";
    }

    private void VisionModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || VisionModeBox.SelectedItem is not ComboBoxItem { Tag: string value } || !Enum.TryParse<VisionPreference>(value, out var mode)) return;
        _preferences.Vision = mode;
        _preferences.Save();
        if (mode != VisionPreference.Live) _liveAwarenessContext = null;
        if (!string.IsNullOrWhiteSpace(_activeSessionId))
        {
            _screenObserver.SetMode(mode);
            _awarenessCoordinator.SetMode(mode);
            if (mode == VisionPreference.Off) AwarenessStatusText.Text = "Local awareness is off.";
        }
        else
        {
            AwarenessStatusText.Text = mode == VisionPreference.Off
                ? "Local awareness is off."
                : "Start a session when you want local desktop awareness.";
        }
        Log($"Desktop-awareness policy: {VisionDisplayName(mode)}.");
    }

    private void ShowAwarenessScene(AwarenessScene scene)
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId) || scene.Mode == VisionPreference.Off) return;

        var pace = scene.Mode == VisionPreference.Live ? "Live local awareness" : "Local session awareness";
        var foreground = scene.Foreground?.DisplayName ?? "the desktop";
        var providerContext = _preferences.LiveProviderAwareness && _liveAwarenessContext is not null
            ? $" The configured model's latest context: {_liveAwarenessContext.Summary}"
            : " No image is being shared.";
        if (scene.Hovered is not null && scene.Hovered.Handle != scene.Foreground?.Handle)
        {
            AwarenessStatusText.Text = $"{pace}: {foreground}. Pointer over {scene.Hovered.DisplayName}.{providerContext}";
            return;
        }

        AwarenessStatusText.Text = $"{pace}: {foreground}.{providerContext}";
    }

    private void RemoteVisionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _preferences.AllowRemoteVision = RemoteVisionCheckBox.IsChecked == true;
        LiveProviderAwarenessCheckBox.IsEnabled = _preferences.AllowRemoteVision;
        if (!_preferences.AllowRemoteVision) _liveAwarenessContext = null;
        _preferences.Save();
        Log(_preferences.AllowRemoteVision
            ? "One-view remote vision is allowed during an active session when the person selects a view or ASHA requests current visual context."
            : "Remote vision is off; desktop samples and selected evidence remain local.");
    }

    private void LiveProviderAwarenessChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _preferences.LiveProviderAwareness = LiveProviderAwarenessCheckBox.IsChecked == true;
        _preferences.Save();
        if (_preferences.LiveProviderAwareness)
        {
            Log("Adaptive model awareness enabled. Only throttled changed keyframes may leave the PC while Live mode and a session are active.");
            QueueLiveAwarenessRefresh(new LocalScreenChange(DateTime.UtcNow, 1));
        }
        else
        {
            _liveAwarenessContext = null;
            Log("Adaptive model awareness disabled. Local desktop sampling is unchanged.");
        }
    }

    private void QueueLiveAwarenessRefresh(LocalScreenChange change)
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId) ||
            _preferences.Vision != VisionPreference.Live ||
            !_preferences.AllowRemoteVision ||
            !_preferences.LiveProviderAwareness ||
            !_voiceSession.SupportsVision) return;

        // Voice is the foreground interaction. During a live conversation,
        // retain only the newest changed keyframe and analyse it in the safe
        // window while ASHA speaks. A background vision call must never race
        // the microphone re-arm or the person's next model turn.
        if (_liveAwarenessRefreshInFlight || _conversationActive || _voiceTurnInFlight || _voiceCapturing)
        {
            _pendingLiveScreenChange = change;
            return;
        }
        if (DateTime.UtcNow - _lastLiveAwarenessRefreshUtc < TimeSpan.FromSeconds(5)) return;

        _lastLiveAwarenessRefreshUtc = DateTime.UtcNow;
        _ = RefreshLiveAwarenessAsync(change, _lifetimeCancellation.Token);
    }

    private async Task RefreshLiveAwarenessAsync(LocalScreenChange change, CancellationToken cancellationToken)
    {
        _liveAwarenessRefreshInFlight = true;
        try
        {
            var scene = _awarenessCoordinator.Current;
            var pointer = scene?.Pointer;
            if (pointer is null && GetCursorPos(out var currentPointer))
                pointer = new AwarenessPoint(currentPointer.X, currentPointer.Y);
            if (pointer is null) return;

            AwarenessStatusText.Text = "ASHA noticed a meaningful change and is refreshing her visual context…";
            var frame = await _screenObserver.CaptureCurrentContextAsync(pointer.X, pointer.Y, cancellationToken);
            if (frame is null) return;
            await ShowTransientLiveCaptureBoundaryAsync(frame);
            var summary = await _voiceSession.DescribeDesktopViewAsync(frame, scene, cancellationToken);
            if (string.IsNullOrWhiteSpace(summary)) return;

            _liveAwarenessContext = new DesktopAwarenessContext(DateTime.UtcNow, summary, change.ChangedScore);
            AwarenessStatusText.Text = $"Model live awareness: {summary}";
            await RecordActiveSessionEventAsync(
                "vision.live_awareness_updated",
                summary,
                "system",
                "ambient_desktop_awareness",
                new
                {
                    app = scene?.Foreground?.ProcessName ?? "desktop",
                    label = scene?.Foreground?.DisplayName ?? "current desktop",
                    control = "adaptive changed keyframe",
                    x = frame.ContextX,
                    y = frame.ContextY,
                    w = frame.ContextWidth,
                    h = frame.ContextHeight,
                    changeScore = change.ChangedScore,
                });
            Log($"Adaptive live visual context updated (change score {change.ChangedScore:0.000}).");
        }
        catch (OperationCanceledException)
        {
            // ASHA is closing.
        }
        catch (Exception error)
        {
            AwarenessStatusText.Text = $"Live visual awareness could not refresh: {ShortReason(error)}";
            Log($"Adaptive live-awareness error: {error.Message}");
        }
        finally
        {
            _liveAwarenessRefreshInFlight = false;
        }
    }

    private async Task ShowTransientLiveCaptureBoundaryAsync(VisionAttachment frame)
    {
        if (!frame.ContextX.HasValue || !frame.ContextY.HasValue ||
            !frame.ContextWidth.HasValue || !frame.ContextHeight.HasValue) return;

        var id = $"asha-live-capture-{Guid.NewGuid():N}";
        var boundary = new MarkRequest(id, "frame", frame.ContextX.Value, frame.ContextY.Value,
            frame.ContextWidth.Value, frame.ContextHeight.Value, null, "#76B6FF");
        try
        {
            await RunAshaAsync("mark", JsonSerializer.Serialize(boundary, JsonOptions));
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                try { StartAshaWithoutWaiting("clear", id); } catch { }
            });
        }
        catch (Exception error)
        {
            Log($"Could not show the live-awareness capture boundary: {error.Message}");
        }
    }

    private async void CaptureVisionEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId))
        {
            StatusText.Text = "Start a shared-attention session before capturing desktop evidence.";
            return;
        }
        if (_preferences.Vision == VisionPreference.Off)
        {
            StatusText.Text = "Desktop awareness is off. Choose session awareness or live awareness first.";
            return;
        }
        StatusText.Text = "Move the pointer onto the area ASHA should see… capturing in two seconds.";
        await Task.Delay(2_000);
        if (!GetCursorPos(out var point))
        {
            StatusText.Text = "Windows could not read the current pointer position.";
            return;
        }

        var surface = ResolveTopmostSurface(point);
        var evidence = await CaptureVisionEvidenceAsync("selected look", point.X, point.Y, surface);
        if (evidence is null) return;
        if (!_preferences.AllowRemoteVision)
        {
            StatusText.Text = "The view is saved locally. Enable selected visual sharing if you want ASHA to see it.";
            return;
        }
        if (!_voiceSession.SupportsVision)
        {
            StatusText.Text = "The configured model cannot receive images. Select a vision-capable model before asking ASHA to look.";
            return;
        }

        _shareVisionOnNextTurn = true;
        StatusText.Text = "ASHA will see this selected view with your next spoken turn.";
        Log("One selected desktop view is armed for ASHA's next spoken turn.");
    }

    private async void ComputerControlPolicyChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingPreferences) return;

        var policy = _preferences.ComputerControl;
        policy.AllowApplicationAndFolderOpening = ApplicationFolderControlCheckBox.IsChecked == true;
        policy.AllowKeyboardInteraction = KeyboardControlCheckBox.IsChecked == true;
        policy.EnableVirtualCursor = VirtualCursorCheckBox.IsChecked == true;
        policy.VirtualCursorBehaviour = VirtualCursorDemonstrateRadio.IsChecked == true
            ? VirtualCursorBehaviour.DemonstrateOnly
            : VirtualCursorBehaviour.Interact;
        policy.ShowVirtualCursor = VirtualCursorVisibleCheckBox.IsChecked == true;
        policy.AllowPhysicalCursor = PhysicalCursorCheckBox.IsChecked == true;
        policy.AskBeforePhysicalFallback = PhysicalFallbackCheckBox.IsChecked == true;
        policy.Normalize();
        _preferences.Save();
        ApplyPreferencesToUi();

        if (_controlLease is not null && !CurrentControlAccess().IsLeaseActive)
        {
            await StopControlLeaseAsync(
                "The person revoked the final capability covered by the active computer-control lease.");
        }
        else
        {
            UpdateControlLeaseUi();
        }

        var newlyAllowedOutsideLease = _controlLease is not null
            ? policy.AllowedCapabilities & ~_controlLease.GrantedCapabilities
            : ComputerControlCapability.None;
        ComputerControlPolicyStatusText.Text = newlyAllowedOutsideLease != ComputerControlCapability.None
            ? "Saved. Newly allowed capabilities will become active only after you stop and restart the control session."
            : DescribeComputerControlPolicy(policy);
        Log($"Computer-control policy changed: {policy.AllowedCapabilities}.");
        await RecordActiveSessionEventAsync(
            "control.policy_changed",
            "The person changed ASHA's persistent computer-control limits directly in Settings.",
            "human",
            "computer_control_policy",
            new
            {
                app = "asha",
                label = "Computer control settings",
                control = "policy",
                allowed = policy.AllowedCapabilities.ToString(),
            });
    }

    private async void ControlPresenceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingPreferences) return;
        _preferences.ShowControlPresence = ControlPresenceCheckBox.IsChecked == true;
        _preferences.Save();
        if (_controlLease is not null)
        {
            try
            {
                if (_preferences.ShowControlPresence)
                    await ShowControlPresenceFrameAsync();
                else
                    await RunAshaAsync("clear", ControlPresenceMarkId);
                UpdateControlLeaseUi();
            }
            catch (Exception error)
            {
                ControlSessionStatusText.Text = $"The control lease is still active, but its presence frame could not be updated: {ShortReason(error)}";
                Log($"Control-presence update error: {error.Message}");
            }
        }
        Log(_preferences.ShowControlPresence
            ? "Desktop-control presence will be visible during physical actions."
            : "Desktop-control presence is hidden by preference.");
    }

    private void ApplyPreferencesToUi()
    {
        _applyingPreferences = true;
        try
        {
            ObserveProfileButton.Background = _preferences.Profile == AshaProfile.Observe ? System.Windows.Media.Brushes.LightBlue : System.Windows.Media.Brushes.Transparent;
            TeachProfileButton.Background = _preferences.Profile == AshaProfile.Teach ? System.Windows.Media.Brushes.LightBlue : System.Windows.Media.Brushes.Transparent;
            AssistProfileButton.Background = _preferences.Profile == AshaProfile.Assist ? System.Windows.Media.Brushes.LightBlue : System.Windows.Media.Brushes.Transparent;
            ProfileStatusText.Text = ProfileStatus(_preferences.Profile);
            VisionModeBox.SelectedIndex = _preferences.Vision switch
            {
                VisionPreference.Off => 0,
                VisionPreference.OnChange => 1,
                VisionPreference.Live => 2,
                _ => 1,
            };
            var control = _preferences.ComputerControl;
            control.Normalize();
            ApplicationFolderControlCheckBox.IsChecked = control.AllowApplicationAndFolderOpening;
            KeyboardControlCheckBox.IsChecked = control.AllowKeyboardInteraction;
            VirtualCursorCheckBox.IsChecked = control.EnableVirtualCursor;
            VirtualCursorInteractRadio.IsChecked = control.VirtualCursorBehaviour == VirtualCursorBehaviour.Interact;
            VirtualCursorDemonstrateRadio.IsChecked = control.VirtualCursorBehaviour == VirtualCursorBehaviour.DemonstrateOnly;
            VirtualCursorVisibleCheckBox.IsChecked = control.ShowVirtualCursor;
            PhysicalCursorCheckBox.IsChecked = control.AllowPhysicalCursor;
            PhysicalFallbackCheckBox.IsChecked = control.AskBeforePhysicalFallback;
            VirtualCursorOptionsPanel.IsEnabled = control.EnableVirtualCursor;
            VirtualCursorVisibleCheckBox.IsEnabled =
                control.EnableVirtualCursor &&
                control.VirtualCursorBehaviour == VirtualCursorBehaviour.Interact;
            PhysicalFallbackCheckBox.IsEnabled =
                control.AllowPhysicalCursor &&
                control.EnableVirtualCursor &&
                control.VirtualCursorBehaviour == VirtualCursorBehaviour.Interact;
            ControlPresenceCheckBox.IsChecked = _preferences.ShowControlPresence;
            RemoteVisionCheckBox.IsChecked = _preferences.AllowRemoteVision;
            LiveProviderAwarenessCheckBox.IsChecked = _preferences.LiveProviderAwareness;
            LiveProviderAwarenessCheckBox.IsEnabled = _preferences.AllowRemoteVision;
            ComputerControlPolicyStatusText.Text = DescribeComputerControlPolicy(control);
        }
        finally
        {
            _applyingPreferences = false;
        }
        UpdateControlLeaseUi();
    }

    private static string DescribeComputerControlPolicy(ComputerControlPolicy policy)
    {
        if (policy.AllowedCapabilities == ComputerControlCapability.None)
            return "All computer-control capabilities are off. Settings alone never start a control session.";

        var allowed = new List<string>();
        if (policy.AllowApplicationAndFolderOpening) allowed.Add("applications and ordinary folders");
        if (policy.AllowKeyboardInteraction) allowed.Add("approved keyboard input");
        if (policy.EnableVirtualCursor)
            allowed.Add(policy.VirtualCursorBehaviour == VirtualCursorBehaviour.Interact
                ? "virtual interaction cursor"
                : "virtual demonstration cursor");
        if (policy.AllowPhysicalCursor) allowed.Add("physical cursor");
        return $"Allowed globally: {string.Join(", ", allowed)}. Start a control session to activate them temporarily.";
    }

    private async Task ShowControlPresenceFrameAsync()
    {
        await RunAshaAsync("clear", ControlPresenceMarkId);
        var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        var virtualTop = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        var frame = new MarkRequest(
            ControlPresenceMarkId,
            "frame",
            virtualLeft,
            virtualTop,
            virtualWidth,
            virtualHeight,
            "ASHA is using your desktop",
            "#766CFF");
        await RunAshaAsync("mark", JsonSerializer.Serialize(frame, JsonOptions));
    }

    private static string ProfileDisplayName(AshaProfile profile) => profile switch
    {
        AshaProfile.Observe => "Observe",
        AshaProfile.Teach => "Teach ASHA",
        AshaProfile.Assist => "Assist",
        _ => profile.ToString(),
    };

    private static string ProfileStatus(AshaProfile profile) => profile switch
    {
        AshaProfile.Observe => "Observe is active: ASHA can speak, see only under the selected policy, and show virtual guidance. It cannot operate your input.",
        AshaProfile.Teach => "Teach ASHA is active: your completed demonstrations are kept as private learning material until you curate or share them.",
        AshaProfile.Assist => "Assist is active: ASHA can resolve and propose learned procedures. Every control action still needs an allowed capability and an explicit session lease.",
        _ => string.Empty,
    };

    private static string VisionDisplayName(VisionPreference mode) => mode switch
    {
        VisionPreference.Off => "off",
        VisionPreference.OnChange => "on change",
        VisionPreference.Live => "live samples",
        _ => mode.ToString(),
    };

    private void Quit_Click(object sender, RoutedEventArgs e) => RequestQuit();

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var show = new Forms.ToolStripMenuItem("Show ASHA");
        show.Click += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        var hide = new Forms.ToolStripMenuItem("Hide ASHA");
        hide.Click += (_, _) => Dispatcher.BeginInvoke(HideToTray);
        var quit = new Forms.ToolStripMenuItem("Quit ASHA");
        quit.Click += (_, _) => Dispatcher.BeginInvoke(RequestQuit);
        menu.Items.Add(show);
        menu.Items.Add(hide);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quit);

        var tray = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Information,
            Text = "ASHA — shared attention",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(ShowFromTray);
        };
        return tray;
    }

    private void HideToTray()
    {
        if (_reallyQuitting) return;
        ControlsPopup.IsOpen = false;
        Hide();
        if (_shownTrayHint) return;
        _shownTrayHint = true;
        _trayIcon.ShowBalloonTip(2600, "ASHA is still ready", "ASHA is hidden in the system tray. Click its icon to bring the orb back.", Forms.ToolTipIcon.Info);
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void ShowFromTray()
    {
        if (_reallyQuitting) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void RequestQuit()
    {
        if (_reallyQuitting) return;
        var dialog = new QuitDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _reallyQuitting = true;
        ControlsPopup.IsOpen = false;
        Close();
    }

    private void ConfigureGroq_Click(object sender, RoutedEventArgs e)
    {
        var setup = Path.Combine(_repositoryRoot, "configure-groq.bat");
        if (!File.Exists(setup))
        {
            StatusText.Text = "The Groq setup script is missing.";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = setup,
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = true,
        });
        StatusText.Text = "Save one or more Groq keys in the setup window, then restart ASHA.";
    }

    private string DescribeProvider()
    {
        var model = Environment.GetEnvironmentVariable("ASHA_GROQ_MODEL") ?? "qwen/qwen3.6-27b";
        var keyState = _voiceSession.GroqKeyCount switch
        {
            0 => "no keys configured",
            1 => "one key configured",
            var count => $"{count} rotating keys configured",
        };
        return $"Groq · {model} · {keyState}";
    }

    private async void TapToListenTimer_Tick(object? sender, EventArgs e)
    {
        _tapToListenTimer.Stop();
        if (_voiceCapturing || _voiceTurnInFlight) return;
        if (string.IsNullOrWhiteSpace(_activeSessionId)) await StartNewSessionAsync();
        if (string.IsNullOrWhiteSpace(_activeSessionId)) return;
        _conversationActive = true;
        if (StartVoiceCapture()) AshaEarcons.ConversationStarted();
    }

    private bool StartVoiceCapture(bool keepConversationReadyOnFailure = false)
    {
        try
        {
            if (_voiceCapturing || _microphone.IsRecording) return true;
            _heardSpeech = false;
            _speechCandidateFrames = 0;
            _lastSpeechAtUtc = DateTime.UtcNow;
            _microphone.Start();
            _voiceCapturing = true;
            _conversationBoundaryTimer.Start();
            OrbSurface.SetPresenceState(OrbPresenceState.Listening);
            OrbSurface.SetAudioEnergy(0);
            StatusText.Text = "Listening… speak naturally. Tap ASHA's centre to end the conversation.";
            Log(_conversationActive ? "Free conversation listening." : "Voice turn started.");
            return true;
        }
        catch (Exception error)
        {
            if (!keepConversationReadyOnFailure) _conversationActive = false;
            _conversationBoundaryTimer.Stop();
            _dragPending = false;
            OrbSurface.ReleaseMouseCapture();
            StatusText.Text = $"Microphone unavailable: {ShortReason(error)}";
            Log($"Microphone error: {error.Message}");
            return false;
        }
    }

    private async Task ResumeFreeConversationAsync()
    {
        // Some Windows audio drivers take a moment to release WaveIn after a
        // completed recording. A single immediate StartRecording call made
        // the conversation appear to work for exactly one turn on those
        // machines. Keep the session alive and re-arm it deliberately.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            if (!_conversationActive || _reallyQuitting || _voiceTurnInFlight || !_voiceSession.IsGroqConfigured) return;

            _conversationActive = true;
            if (StartVoiceCapture(keepConversationReadyOnFailure: true))
            {
                StatusText.Text = "Listening again… speak naturally. Tap ASHA's centre only when you want to end the conversation.";
                Log($"Free conversation re-armed after reply (attempt {attempt}).");
                return;
            }

            Log($"Microphone did not re-arm after reply (attempt {attempt}); retrying.");
        }

        _conversationActive = false;
        OrbSurface.SetPresenceState(OrbPresenceState.Idle);
        StatusText.Text = "ASHA could not reopen the microphone. Tap the orb once to try again.";
        Log("Free conversation stopped because the microphone could not be re-armed after three attempts.");
    }

    private void ObserveIncomingSpeech(double energy)
    {
        // The energy is intentionally handled locally and continuously. This
        // is the light-weight first turn detector; it avoids sending a request
        // for every short audio buffer to the speech service.
        // V8S-style desktop microphones often report a modest RMS value even
        // for clear speech, so this deliberately sits just above room noise.
        if (!_conversationActive) return;
        var speechThreshold = Math.Clamp((_ambientMicrophoneEnergy * 1.8) + 0.0025, 0.004, 0.018);
        if (energy < speechThreshold)
        {
            if (!_heardSpeech)
            {
                _speechCandidateFrames = 0;
                _ambientMicrophoneEnergy = (_ambientMicrophoneEnergy * 0.96) + (energy * 0.04);
            }
            return;
        }

        if (!_heardSpeech)
        {
            // Two consecutive microphone buffers avoid treating a click or a
            // speaker tail as a new human turn. Once real speech begins, drop
            // the accumulated inter-turn silence but retain a short pre-roll
            // so Whisper receives the first syllable intact.
            _speechCandidateFrames++;
            if (_speechCandidateFrames < 2) return;
            _microphone.KeepRecentAudio(TimeSpan.FromMilliseconds(900));
            _heardSpeech = true;
        }
        _lastSpeechAtUtc = DateTime.UtcNow;
    }

    private void ConversationBoundaryTimer_Tick(object? sender, EventArgs e)
    {
        if (!_conversationActive || !_voiceCapturing || _voiceTurnInFlight || !_heardSpeech) return;
        if (DateTime.UtcNow - _lastSpeechAtUtc < TimeSpan.FromMilliseconds(1_250)) return;

        _conversationBoundaryTimer.Stop();
        Log("Speech pause detected; sending turn.");
        _ = CompleteVoiceTurnAsync();
    }

    private async Task EndConversationAsync()
    {
        var wasActive = _conversationActive;
        _conversationActive = false;
        _heardSpeech = false;
        _speechCandidateFrames = 0;
        _tapToListenTimer.Stop();
        _conversationBoundaryTimer.Stop();
        _voiceTurnCancellation?.Cancel();

        if (_voiceCapturing)
        {
            _voiceCapturing = false;
            try { await _microphone.StopAsync(); }
            catch (Exception error) { Log($"Microphone stop error: {error.Message}"); }
        }

        OrbSurface.SetAudioEnergy(0);
        OrbSurface.SetPresenceState(OrbPresenceState.Idle);
        StatusText.Text = "Conversation paused. Tap ASHA to talk again.";
        Log("Free conversation ended.");
        if (wasActive) AshaEarcons.ConversationEnded();
    }

    private async Task<bool> SubmitTypedTurnAsync(string? rawText)
    {
        var text = NormalizeTypedInput(rawText);
        if (text.Length == 0) return false;
        if (_voiceTurnInFlight)
        {
            StatusText.Text = "ASHA is finishing the current turn. Your typed message is still in the composer.";
            return false;
        }

        var resumeListening = _conversationActive;
        var assistantReplyStored = false;
        var turnStage = "storing_typed_turn";
        _voiceTurnInFlight = true;
        SetComposerBusy(true);
        _voiceTurnCancellation = new CancellationTokenSource();

        try
        {
            // A live microphone and a typed turn must never race into the same
            // conversation state. Pause and discard the unfinished audio, then
            // resume listening after the typed reply when talk mode was active.
            if (_voiceCapturing)
            {
                _voiceCapturing = false;
                _heardSpeech = false;
                _speechCandidateFrames = 0;
                _conversationBoundaryTimer.Stop();
                try { _ = await _microphone.StopAsync(); }
                catch (Exception error) { Log($"Microphone pause for typed turn: {error.Message}"); }
            }

            OrbSurface.SetPresenceState(OrbPresenceState.Thinking);
            OrbSurface.SetAudioEnergy(0.12);
            StatusText.Text = "Thinking about your typed message…";
            await AddConversationAsync("You", text);
            await MaybeNameActiveSessionAsync(text);

            turnStage = "requesting_model_or_tool_result";
            var reply = await _voiceSession.RespondToTranscriptAsync(
                text,
                ResolveVisionForTranscriptAsync,
                ExecuteVisualToolAsync,
                CurrentControlAccess(),
                !string.IsNullOrWhiteSpace(_activeSessionId) &&
                    _preferences.Vision != VisionPreference.Off &&
                    _preferences.AllowRemoteVision &&
                    _voiceSession.SupportsVision,
                _liveAwarenessContext,
                _voiceTurnCancellation.Token);

            turnStage = "storing_asha_reply";
            await AddConversationAsync("ASHA", reply);
            assistantReplyStored = true;
            if (!string.IsNullOrWhiteSpace(_activeSessionId))
                _ = RefreshSessionMemoryAsync(_activeSessionId, forceCompression: false);

            if (resumeListening)
            {
                OrbSurface.SetPresenceState(OrbPresenceState.Speaking);
                StatusText.Text = "ASHA is speaking…";
                turnStage = "playing_speech";
                await _voiceSession.SpeakAsync(reply, _voiceTurnCancellation.Token);
            }
            else
            {
                StatusText.Text = "ASHA replied in the conversation.";
            }
            return true;
        }
        catch (AllGroqKeysRateLimitedException error)
        {
            var retryText = error.RetryAtUtc is { } retryAt
                ? $" Please try again after {retryAt.ToLocalTime():HH:mm}."
                : " Please try again in a little while.";
            var reply = $"All of my available connections are temporarily busy.{retryText}";
            StatusText.Text = reply;
            await AddConversationAsync("ASHA", reply);
            assistantReplyStored = true;
            await RecordTypedTurnFailureAsync(turnStage, error, text);
            return true;
        }
        catch (OperationCanceledException error)
        {
            if (_reallyQuitting) return true;
            await RecordTypedTurnFailureAsync(turnStage, error, text);
            if (!assistantReplyStored)
            {
                const string reply = "Your typed message is saved, but that answer was interrupted before it finished.";
                await AddConversationAsync("ASHA", reply);
                StatusText.Text = reply;
            }
            return true;
        }
        catch (Exception error)
        {
            await RecordTypedTurnFailureAsync(turnStage, error, text);
            Log($"Typed turn failed during {turnStage}: {SanitizeTurnDiagnostic(error.Message)}");
            if (!assistantReplyStored)
            {
                const string reply = "I have your typed message, but I couldn't finish the answer. I didn't perform any unverified desktop action.";
                await AddConversationAsync("ASHA", reply);
                StatusText.Text = reply;
            }
            return true;
        }
        finally
        {
            OrbSurface.SetAudioEnergy(0);
            OrbSurface.SetPresenceState(OrbPresenceState.Idle);
            _voiceTurnCancellation?.Dispose();
            _voiceTurnCancellation = null;
            _voiceTurnInFlight = false;
            SetComposerBusy(false);
            if (resumeListening && _conversationActive && !_reallyQuitting && _voiceSession.IsGroqConfigured)
                _ = ResumeFreeConversationAsync();
        }
    }

    internal static string NormalizeTypedInput(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();

    private Task RecordTypedTurnFailureAsync(string stage, Exception error, string text) =>
        RecordActiveSessionEventAsync(
            "conversation.turn_failed",
            "ASHA retained a failed typed turn instead of silently dropping it.",
            "system",
            "diagnose_typed_turn",
            target: new { app = "ASHA", label = stage, control = "typed_turn" },
            content: string.Join(
                Environment.NewLine,
                $"stage: {stage}",
                $"error type: {error.GetType().Name}",
                $"diagnostic: {SanitizeTurnDiagnostic(error.Message)}",
                $"typed message retained: {text.Length > 0}",
                "desktop action performed: not claimed"));

    private async Task CompleteVoiceTurnAsync()
    {
        if (!_voiceCapturing || _voiceTurnInFlight) return;
        var preserveFinalStatus = false;
        var turnStage = "capturing_audio";
        string? transcript = null;
        var assistantReplyStored = false;
        _voiceCapturing = false;
        _voiceTurnInFlight = true;
        _conversationBoundaryTimer.Stop();
        _dragPending = false;
        _voiceTurnCancellation = new CancellationTokenSource();

        try
        {
            var wav = await _microphone.StopAsync();
            if (wav.Length < 2_000)
            {
                StatusText.Text = "I only heard a short sound. I am still listening—please speak again.";
                return;
            }

            OrbSurface.SetPresenceState(OrbPresenceState.Thinking);
            OrbSurface.SetAudioEnergy(0.16);
            StatusText.Text = "Thinking…";
            turnStage = "transcribing_speech";
            transcript = await _voiceSession.TranscribeTurnAsync(wav, _voiceTurnCancellation.Token);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                StatusText.Text = "I did not catch that. I am still listening—please try again.";
                return;
            }

            // Persist and show what the person said before any visual or model
            // follow-up. If a provider call later fails, the spoken turn is
            // still visible, reviewable, and part of the full session log.
            turnStage = "storing_human_turn";
            await AddConversationAsync("You", transcript);
            await MaybeNameActiveSessionAsync(transcript);

            turnStage = "requesting_model_or_tool_result";
            var reply = await _voiceSession.RespondToTranscriptAsync(
                transcript,
                ResolveVisionForTranscriptAsync,
                ExecuteVisualToolAsync,
                CurrentControlAccess(),
                !string.IsNullOrWhiteSpace(_activeSessionId) &&
                    _preferences.Vision != VisionPreference.Off &&
                    _preferences.AllowRemoteVision &&
                    _voiceSession.SupportsVision,
                _liveAwarenessContext,
                _voiceTurnCancellation.Token);
            turnStage = "storing_asha_reply";
            await AddConversationAsync("ASHA", reply);
            assistantReplyStored = true;
            if (!string.IsNullOrWhiteSpace(_activeSessionId))
                _ = RefreshSessionMemoryAsync(_activeSessionId, forceCompression: false);

            OrbSurface.SetPresenceState(OrbPresenceState.Speaking);
            StatusText.Text = "ASHA is speaking…";
            turnStage = "playing_speech";

            // Continuous visual awareness gets one bounded opportunity while
            // ASHA is already speaking. It is cancelled as soon as speech
            // ends, so reopening the microphone is never held up by vision.
            using var awarenessWindow = CancellationTokenSource.CreateLinkedTokenSource(_voiceTurnCancellation.Token);
            awarenessWindow.CancelAfter(TimeSpan.FromSeconds(8));
            var awarenessRefresh = RefreshPendingLiveAwarenessDuringReplyAsync(awarenessWindow.Token);
            try
            {
                await _voiceSession.SpeakAsync(reply, _voiceTurnCancellation.Token);
            }
            finally
            {
                awarenessWindow.Cancel();
                await awarenessRefresh;
            }
        }
        catch (AllGroqKeysRateLimitedException error)
        {
            preserveFinalStatus = true;
            _conversationActive = false;
            await RecordVoiceTurnFailureAsync(turnStage, error, transcript);
            var retryText = error.RetryAtUtc is { } retryAt
                ? $" Please try again after {retryAt.ToLocalTime():HH:mm}."
                : " Please try again in a little while.";
            var reply = $"All of my available connections are temporarily busy.{retryText}";
            StatusText.Text = reply;
            AwarenessStatusText.Text = "Automatic visual updates are paused while all configured model connections cool down.";
            Log($"All {_voiceSession.GroqKeyCount} configured Groq keys are rate-limited; ASHA returned to a calm idle state.");
            await AddConversationAsync("ASHA", reply);
            assistantReplyStored = true;
            try
            {
                OrbSurface.SetPresenceState(OrbPresenceState.Speaking);
                await _voiceSession.SpeakAsync(reply, _voiceTurnCancellation.Token);
            }
            catch (Exception speechError)
            {
                Log($"Could not speak the rate-limit notice: {speechError.Message}");
            }
        }
        catch (GroqKeysUnavailableException error)
        {
            preserveFinalStatus = true;
            _conversationActive = false;
            await RecordVoiceTurnFailureAsync(turnStage, error, transcript);
            const string reply = "I cannot reach my model connections right now. Your words are saved, and you can try again shortly.";
            StatusText.Text = reply;
            Log($"Groq key-ring network error: {error.Message}");
            await AddConversationAsync("ASHA", reply);
            assistantReplyStored = true;
            try
            {
                OrbSurface.SetPresenceState(OrbPresenceState.Speaking);
                await _voiceSession.SpeakAsync(reply, _voiceTurnCancellation.Token);
            }
            catch (Exception speechError)
            {
                Log($"Could not speak the connection notice: {speechError.Message}");
            }
        }
        catch (OperationCanceledException error)
        {
            // Closing ASHA or tapping its centre deliberately cancels a turn.
            // Any other cancellation is a failed turn and must never disappear.
            if (_conversationActive && !_reallyQuitting)
            {
                preserveFinalStatus = true;
                await HandleVoiceTurnFailureAsync(turnStage, error, transcript, assistantReplyStored);
            }
        }
        catch (Exception error)
        {
            preserveFinalStatus = true;
            await HandleVoiceTurnFailureAsync(turnStage, error, transcript, assistantReplyStored);
        }
        finally
        {
            OrbSurface.SetAudioEnergy(0);
            OrbSurface.SetPresenceState(OrbPresenceState.Idle);
            _voiceTurnCancellation?.Dispose();
            _voiceTurnCancellation = null;
            _voiceTurnInFlight = false;

            if (_conversationActive && !_reallyQuitting && _voiceSession.IsGroqConfigured)
            {
                // Return to listening after ASHA finishes speaking. This is
                // what makes the interaction a session instead of a sequence
                // of manually armed one-shot recordings.
                _ = ResumeFreeConversationAsync();
            }
            else if (!preserveFinalStatus &&
                     !StatusText.Text.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                     !StatusText.Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                StatusText.Text = _voiceSession.IsGroqConfigured ? "Tap the orb to start listening." : "Configure Groq, then restart ASHA.";
        }
    }

    private async Task HandleVoiceTurnFailureAsync(
        string stage,
        Exception error,
        string? transcript,
        bool assistantReplyStored)
    {
        await RecordVoiceTurnFailureAsync(stage, error, transcript);
        Log($"Voice turn failed during {stage}: {SanitizeTurnDiagnostic(error.Message)}");

        if (assistantReplyStored)
        {
            StatusText.Text = "My answer is in the conversation, but I could not play it aloud.";
            return;
        }

        const string reply = "I heard you, but I couldn't finish that answer. I didn't perform any desktop action. Please try once more.";
        StatusText.Text = reply;
        await AddConversationAsync("ASHA", reply);
        try
        {
            using var speechTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            OrbSurface.SetPresenceState(OrbPresenceState.Speaking);
            await _voiceSession.SpeakAsync(reply, speechTimeout.Token);
        }
        catch (Exception speechError)
        {
            Log($"Could not speak the failed-turn notice: {SanitizeTurnDiagnostic(speechError.Message)}");
        }
    }

    private async Task RecordVoiceTurnFailureAsync(string stage, Exception error, string? transcript)
    {
        var diagnostic = SanitizeTurnDiagnostic(error.Message);
        var diagnosticContent = string.Join(
            Environment.NewLine,
            $"stage: {stage}",
            $"error type: {error.GetType().Name}",
            $"diagnostic: {diagnostic}",
            $"transcript retained: {!string.IsNullOrWhiteSpace(transcript)}",
            "desktop action performed: false");
        await RecordActiveSessionEventAsync(
            "voice.turn_failed",
            "ASHA retained a failed conversational turn instead of silently dropping it.",
            "system",
            "diagnose_voice_turn",
            target: new
            {
                app = "ASHA",
                label = stage,
                control = "voice_turn",
            },
            content: diagnosticContent);
    }

    internal static string SanitizeTurnDiagnostic(string? message)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "No diagnostic detail was available." : message.Trim();
        value = Regex.Replace(value, @"\bgsk_[A-Za-z0-9_-]+\b", "gsk_[redacted]", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\bBearer\s+[A-Za-z0-9._~-]+", "Bearer [redacted]", RegexOptions.CultureInvariant);
        return value.Length <= 500 ? value : value[..500] + "…";
    }

    private async Task RefreshPendingLiveAwarenessDuringReplyAsync(CancellationToken cancellationToken)
    {
        if (_pendingLiveScreenChange is not { } change ||
            _preferences.Vision != VisionPreference.Live ||
            !_preferences.AllowRemoteVision ||
            !_preferences.LiveProviderAwareness ||
            !_voiceSession.SupportsVision ||
            _liveAwarenessRefreshInFlight) return;

        if (DateTime.UtcNow - _lastLiveAwarenessRefreshUtc < TimeSpan.FromSeconds(5)) return;
        _pendingLiveScreenChange = null;
        _lastLiveAwarenessRefreshUtc = DateTime.UtcNow;
        await RefreshLiveAwarenessAsync(change, cancellationToken);
    }

    private async void PlaceCueAtPointer_Click(object sender, RoutedEventArgs e) => await MarkAtCurrentMouseAsync();

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDrawCueButton();

    private void DrawCue_Click(object sender, RoutedEventArgs e)
    {
        if (_cueDrawingArmed)
        {
            CancelCueDrawing("Cue drawing cancelled.");
            return;
        }

        var kind = SelectedCueKind();
        if (kind is not ("box" or "arrow"))
        {
            StatusText.Text = "Select box or arrow before drawing.";
            return;
        }
        BeginCueDrawing(kind);
    }

    private void BeginCueDrawing(string kind)
    {
        if (kind is not ("box" or "arrow")) return;
        SelectCueKind(kind);

        _cueDrawMouseProc = CueDrawingMouseHook;
        _cueDrawHook = SetWindowsHookEx(WhMouseLl, _cueDrawMouseProc, GetModuleHandle(null), 0);
        if (_cueDrawHook == IntPtr.Zero)
        {
            StatusText.Text = $"ASHA could not prepare {kind} drawing.";
            Log($"Could not install the temporary {kind}-drawing mouse hook.");
            return;
        }

        _cueDrawKeyboardProc = CueDrawingKeyboardHook;
        _cueDrawKeyboardHook = SetWindowsHookExKeyboard(WhKeyboardLl, _cueDrawKeyboardProc, GetModuleHandle(null), 0);

        _cueDrawingKind = kind;
        _cueDrawingArmed = true;
        _cueDrawingStarted = false;
        DrawCueButton.Content = "Cancel drawing";
        StatusText.Text = $"Draw {kind} is ready. Press, drag, and release anywhere on the desktop. Press Escape to cancel.";
        Log($"{kind} drawing armed; the next drag is reserved for an ASHA visual cue.");
        _ = RecordActiveSessionEventAsync(
            "cue.drawing_armed",
            $"The person armed one press-drag-release {kind} cue.",
            "human",
            "teach_surface",
            new { app = "desktop", label = $"Draw {kind}", control = "press-drag-release", kind });
    }

    private IntPtr CueDrawingMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_cueDrawingArmed) return CallNextHookEx(_cueDrawHook, code, wParam, lParam);
        var mouse = Marshal.PtrToStructure<LowLevelMouseData>(lParam);
        var message = unchecked((int)wParam.ToInt64());

        if (message == WmLButtonDown && !_cueDrawingStarted)
        {
            _cueDrawingStarted = true;
            _cueDrawingStart = mouse.Point;
            var kind = _cueDrawingKind ?? "box";
            Dispatcher.BeginInvoke(() =>
            {
                _cueDrawingPreview ??= new CueDrawingPreviewWindow(kind);
                _cueDrawingPreview.UpdateGesture(_cueDrawingStart, _cueDrawingStart);
                StatusText.Text = kind == "arrow"
                    ? "Drawing arrow… release where its tip should point."
                    : "Drawing box… release when the important area is framed.";
            });
            return new IntPtr(1);
        }

        if (message == WmMouseMove && _cueDrawingStarted)
        {
            var start = _cueDrawingStart;
            Dispatcher.BeginInvoke(() => _cueDrawingPreview?.UpdateGesture(start, mouse.Point));
            return new IntPtr(1);
        }

        if (message == WmLButtonUp && _cueDrawingStarted)
        {
            var start = _cueDrawingStart;
            var kind = _cueDrawingKind ?? "box";
            StopCueDrawingHook();
            Dispatcher.BeginInvoke(async () => await FinishDrawnCueAsync(kind, start, mouse.Point));
            return new IntPtr(1);
        }

        return CallNextHookEx(_cueDrawHook, code, wParam, lParam);
    }

    private IntPtr CueDrawingKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _cueDrawingArmed && unchecked((int)wParam.ToInt64()) == WmKeyDown)
        {
            var key = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
            if (key.VirtualKeyCode == VkEscape)
            {
                Dispatcher.BeginInvoke(() => CancelCueDrawing("Cue drawing cancelled."));
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_cueDrawKeyboardHook, code, wParam, lParam);
    }

    private void CancelCueDrawing(string status)
    {
        var wasArmed = _cueDrawingArmed;
        var kind = _cueDrawingKind;
        StopCueDrawingHook();
        _cueDrawingPreview?.Close();
        _cueDrawingPreview = null;
        StatusText.Text = status;
        if (wasArmed)
            _ = RecordActiveSessionEventAsync(
                "cue.drawing_cancelled",
                $"The person cancelled the armed {kind ?? "visual"} cue before completing it.",
                "human",
                "teach_surface",
                new { app = "desktop", label = $"Draw {kind ?? "cue"}", control = "cancel", kind });
    }

    private void StopCueDrawingHook()
    {
        _cueDrawingArmed = false;
        _cueDrawingStarted = false;
        _cueDrawingKind = null;
        if (_cueDrawHook != IntPtr.Zero) _ = UnhookWindowsHookEx(_cueDrawHook);
        _cueDrawHook = IntPtr.Zero;
        _cueDrawMouseProc = null;
        if (_cueDrawKeyboardHook != IntPtr.Zero) _ = UnhookWindowsHookEx(_cueDrawKeyboardHook);
        _cueDrawKeyboardHook = IntPtr.Zero;
        _cueDrawKeyboardProc = null;
        Dispatcher.BeginInvoke((Action)UpdateDrawCueButton);
    }

    private async Task FinishDrawnCueAsync(string kind, NativePoint start, NativePoint end)
    {
        _cueDrawingPreview?.Close();
        _cueDrawingPreview = null;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        if (kind == "arrow")
        {
            if (Math.Sqrt((double)dx * dx + (double)dy * dy) < 12)
            {
                StatusText.Text = "Arrow ignored because its vector was too short. Drag from its origin to its tip.";
                return;
            }
            await CreateVisualCueAsync("arrow", start, dx, dy);
            return;
        }

        var width = Math.Abs(dx);
        var height = Math.Abs(dy);
        if (width < 12 || height < 12)
        {
            StatusText.Text = "Box ignored because it was too small. Drag a visible area.";
            return;
        }

        var center = new NativePoint { X = Math.Min(start.X, end.X) + width / 2, Y = Math.Min(start.Y, end.Y) + height / 2 };
        await CreateVisualCueAsync("box", center, width, height);
    }

    private void UpdateDrawCueButton()
    {
        if (DrawCueButton is null || _cueDrawingArmed) return;
        var kind = SelectedCueKind();
        DrawCueButton.Content = kind switch
        {
            "box" => "Draw box",
            "arrow" => "Draw arrow",
            _ => "Draw selected",
        };
        DrawCueButton.IsEnabled = kind is "box" or "arrow";
    }

    private async Task MarkAtCurrentMouseAsync()
    {
        if (!GetCursorPos(out var point))
        {
            StatusText.Text = "Windows could not read the current pointer position.";
            return;
        }

        var kind = SelectedCueKind();
        var width = kind == "arrow" ? ReadVectorDimension(WidthBox, 220) : ReadDimension(WidthBox, 220);
        var height = kind == "arrow" ? ReadVectorDimension(HeightBox, 100) : ReadDimension(HeightBox, 100);
        await CreateVisualCueAsync(kind, point, kind is "box" or "arrow" ? width : null, kind is "box" or "arrow" ? height : null);
    }

    private string SelectedCueKind() =>
        (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "dot";

    private void SelectCueKind(string kind)
    {
        KindBox.SelectedItem = KindBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), kind, StringComparison.OrdinalIgnoreCase));
    }

    private async Task CreateVisualCueAsync(string kind, NativePoint point, int? width = null, int? height = null)
    {
        try
        {
            var id = $"live-{Guid.NewGuid():N}";
            var color = ReadColor(ColorBox.Text);
            var mark = new MarkRequest(
                id, kind, point.X, point.Y, width, height,
                string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(), color);
            var result = await RunAshaAsync("mark", JsonSerializer.Serialize(mark, JsonOptions));
            using var response = JsonDocument.Parse(result.StandardOutput);
            var returnedId = response.RootElement.GetProperty("id").GetString() ?? id;
            var live = new LiveMark(returnedId, kind, point.X, point.Y, width, height, mark.Label, color);
            _marks.Add(live);
            MarkList.SelectedItem = live;
            var surface = ResolveTopmostSurface(point);
            await RecordCueCreatedAsync(live, surface);
            if (surface is not null)
            {
                await CaptureVisionEvidenceAsync("visual cue", live.X, live.Y, surface);
                Log($"Taught target: {surface.DisplayName} ({surface.ProcessName}).");
                StatusText.Text = $"Visual cue and target saved. {_marks.Count} active cue(s).";
            }
            else
            {
                Log($"Placed {kind} '{returnedId}' at {point.X}, {point.Y}; no user-facing window was found there.");
                StatusText.Text = $"Visual cue active. {_marks.Count} active cue(s).";
            }
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            Log($"Error: {error.Message}");
        }
    }

    private void MarkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkList.SelectedItem is not LiveMark mark) return;
        LoadCueIntoEditor(mark, preserveLabel: LabelBox.IsKeyboardFocusWithin && _labelEditingCueId is not null);
    }

    private void LoadCueIntoEditor(LiveMark mark, bool preserveLabel = false)
    {
        KindBox.SelectedItem = KindBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), mark.Kind, StringComparison.OrdinalIgnoreCase));
        if (!preserveLabel) LabelBox.Text = mark.Label ?? string.Empty;
        WidthBox.Text = (mark.W ?? DefaultWidth(mark.Kind)).ToString();
        HeightBox.Text = (mark.H ?? DefaultHeight(mark.Kind)).ToString();
        XBox.Text = mark.X.ToString();
        YBox.Text = mark.Y.ToString();
        ColorBox.Text = mark.Color;
    }

    private void LabelBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _labelEditingCueId = (MarkList.SelectedItem as LiveMark)?.Id;
        _labelEditingText = LabelBox.Text;
    }

    private void LabelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LabelBox.IsKeyboardFocusWithin && _labelEditingCueId is not null)
            _labelEditingText = LabelBox.Text;
    }

    private async void LabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var cueId = _labelEditingCueId;
        var text = _labelEditingText ?? LabelBox.Text;
        await CommitCueLabelAsync(cueId, text);
    }

    private async void LabelBox_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var cueId = _labelEditingCueId;
        var text = _labelEditingText ?? LabelBox.Text;
        _labelEditingCueId = null;
        _labelEditingText = null;
        await CommitCueLabelAsync(cueId, text);
        if (MarkList.SelectedItem is LiveMark selected) LoadCueIntoEditor(selected);
    }

    private async Task CommitCueLabelAsync(string? cueId, string rawLabel)
    {
        if (_labelCommitInProgress || string.IsNullOrWhiteSpace(cueId)) return;
        var mark = _marks.FirstOrDefault(candidate => string.Equals(candidate.Id, cueId, StringComparison.Ordinal));
        if (mark is null) return;
        var label = string.IsNullOrWhiteSpace(rawLabel) ? null : rawLabel.Trim();
        if (string.Equals(mark.Label, label, StringComparison.Ordinal)) return;

        _labelCommitInProgress = true;
        try
        {
            await RunAshaAsync("update", mark.Id, JsonSerializer.Serialize(new { label }, JsonOptions));
            var index = _marks.IndexOf(mark);
            if (index < 0) return;
            var updated = mark with { Label = label };
            _marks[index] = updated;
            if (MarkList.SelectedItem is LiveMark current && current.Id == mark.Id)
                MarkList.SelectedItem = updated;
            await RecordCueLifecycleAsync("cue.updated", updated, "Human changed a visual cue label.");
            StatusText.Text = "Cue label saved.";
            Log($"Updated label for '{mark.Id}'.");
        }
        catch (Exception error)
        {
            StatusText.Text = ShortReason(error);
            Log($"Could not save cue label: {error.Message}");
        }
        finally
        {
            _labelCommitInProgress = false;
        }
    }

    private async void ApplyCueChanges_Click(object sender, RoutedEventArgs e)
    {
        if (MarkList.SelectedItem is not LiveMark mark)
        {
            StatusText.Text = "Select an active visual cue first.";
            return;
        }

        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? mark.Kind;
        if (!int.TryParse(XBox.Text, out var x) || !int.TryParse(YBox.Text, out var y))
        {
            StatusText.Text = "X and Y must be whole desktop-pixel values.";
            return;
        }

        var hasSize = kind is "box" or "arrow" or "circle";
        var width = hasSize
            ? (kind == "arrow" ? ReadVectorDimension(WidthBox, DefaultWidth(kind)) : ReadDimension(WidthBox, DefaultWidth(kind)))
            : (int?)null;
        var height = hasSize
            ? (kind == "arrow" ? ReadVectorDimension(HeightBox, DefaultHeight(kind)) : ReadDimension(HeightBox, DefaultHeight(kind)))
            : (int?)null;
        var color = ReadColor(ColorBox.Text);
        if (!string.Equals(color, ColorBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Colour must be a six-digit hex value, for example #4C8DFF.";
            return;
        }

        try
        {
            var label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim();
            var update = new CueUpdateRequest(kind, x, y, width, height, label, color);
            await RunAshaAsync("update", mark.Id, JsonSerializer.Serialize(update, JsonOptions));
            var index = _marks.IndexOf(mark);
            var updated = new LiveMark(mark.Id, kind, x, y, width, height, label, color);
            _marks[index] = updated;
            MarkList.SelectedItem = updated;
            await RecordCueLifecycleAsync("cue.updated", updated, "Human edited a visual cue.");
            StatusText.Text = "Cue changes applied.";
            Log($"Updated '{mark.Id}'.");
        }
        catch (Exception error)
        {
            StatusText.Text = ShortReason(error);
            Log($"Could not update cue: {error.Message}");
        }
    }

    private async void ClearSelected_Click(object sender, RoutedEventArgs e)
    {
        if (MarkList.SelectedItem is not LiveMark mark)
        {
            StatusText.Text = "Select an active visual cue first.";
            return;
        }
        await ClearMarkAsync(mark);
    }

    private async Task ClearMarkAsync(LiveMark mark)
    {
        try
        {
            await RunAshaAsync("clear", mark.Id);
            _marks.Remove(mark);
            await RecordCueLifecycleAsync("cue.deleted", mark, "Human removed a visual cue.");
            Log($"Cleared '{mark.Id}'.");
            StatusText.Text = $"{_marks.Count} active cue(s).";
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private async Task MoveSelectedToCurrentMouseAsync()
    {
        if (MarkList.SelectedItem is not LiveMark mark)
        {
            StatusText.Text = "Select an active cue, then press Ctrl+Alt+Shift+M.";
            return;
        }
        if (!GetCursorPos(out var point))
        {
            StatusText.Text = "Windows could not read the current pointer position.";
            return;
        }

        try
        {
            await RunAshaAsync("move", mark.Id, JsonSerializer.Serialize(new MoveRequest(point.X, point.Y), JsonOptions));
            var index = _marks.IndexOf(mark);
            var moved = mark with { X = point.X, Y = point.Y };
            _marks[index] = moved;
            MarkList.SelectedItem = moved;
            await RecordCueLifecycleAsync("cue.moved", moved, "Human moved a visual cue.");
            Log($"Moved '{mark.Id}' to {point.X}, {point.Y}.");
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            Log($"Error: {error.Message}");
        }
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        Exception? commandError = null;
        var clearedCues = _marks.ToArray();
        try
        {
            if (_cueEditing)
            {
                await RunAshaAsync("edit", "off");
                _cueEditing = false;
                _cueEditEventTimer.Stop();
                EditCuesButton.Content = "Edit cues";
            }
            await RunAshaAsync("clear");
        }
        catch (Exception error)
        {
            commandError = error;
        }
        finally
        {
            // The node mark engine clears every overlay it knows about. A
            // crashed/restarted process can leave a native click-through
            // overlay behind, though, so the UI's promise is stronger: clear
            // every ASHA overlay launched from this exact project binary.
            var orphaned = ClearOrphanedOverlayWindows();
            foreach (var mark in clearedCues)
                await RecordCueLifecycleAsync("cue.deleted", mark, "Human cleared this visual cue with Clear all visual cues.");
            _marks.Clear();
            if (_controlLease is not null)
                await StopControlLeaseAsync(
                    "The person cleared all visual cues, including the visible computer-control presence.",
                    recordEvent: true);
            Log($"Cleared all ASHA visual cues{(orphaned > 0 ? $" and {orphaned} stale overlay(s)" : string.Empty)}.");
            StatusText.Text = commandError is null
                ? "All visual cues are cleared."
                : $"All visible cues were cleared; the mark store reported: {ShortReason(commandError)}";
        }
    }

    private async void EditCues_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cueEditing = !_cueEditing;
            if (_cueEditing)
            {
                var directory = CueEditEventDirectory();
                Directory.CreateDirectory(directory);
                foreach (var file in Directory.EnumerateFiles(directory, "*.json")) File.Delete(file);
                await RunAshaAsync("edit", "on");
                _cueEditEventTimer.Start();
                EditCuesButton.Content = "Finish editing";
                StatusText.Text = "Edit cues is on. Click a cue to select it, drag it to move it, or right-click it to delete it.";
                Log("Cue editing enabled; target applications are temporarily protected beneath cues.");
            }
            else
            {
                _cueEditEventTimer.Stop();
                await RunAshaAsync("edit", "off");
                EditCuesButton.Content = "Edit cues";
                StatusText.Text = "Cue editing is off. Visual cues are click-through again.";
                Log("Cue editing disabled; visual cues are click-through again.");
            }
        }
        catch (Exception error)
        {
            _cueEditing = false;
            _cueEditEventTimer.Stop();
            EditCuesButton.Content = "Edit cues";
            StatusText.Text = ShortReason(error);
            Log($"Could not change cue editing: {error.Message}");
        }
    }

    private async void CueEditEventTimer_Tick(object? sender, EventArgs e)
    {
        if (!_cueEditing || _processingCueEditEvents) return;
        _processingCueEditEvents = true;
        try
        {
            var directory = CueEditEventDirectory();
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path))
            {
                CueEditEvent? edit;
                try
                {
                    edit = JsonSerializer.Deserialize<CueEditEvent>(await File.ReadAllTextAsync(path), JsonOptions);
                    File.Delete(path);
                }
                catch
                {
                    // A file is written atomically, but tolerate a stale file
                    // from a process stopped halfway through an edit.
                    continue;
                }
                if (edit is null || string.IsNullOrWhiteSpace(edit.Id)) continue;

                var mark = _marks.FirstOrDefault(candidate => candidate.Id == edit.Id);
                if (edit.Type == "select")
                {
                    if (mark is not null) MarkList.SelectedItem = mark;
                    continue;
                }
                if (edit.Type == "delete")
                {
                    if (mark is not null) await ClearMarkAsync(mark);
                    else await RunAshaAsync("clear", edit.Id);
                    continue;
                }
                if (edit.Type == "move" && mark is not null && edit.X is int x && edit.Y is int y)
                {
                    await RunAshaAsync("move", mark.Id, JsonSerializer.Serialize(new MoveRequest(x, y), JsonOptions));
                    var index = _marks.IndexOf(mark);
                    var moved = mark with { X = x, Y = y };
                    _marks[index] = moved;
                    MarkList.SelectedItem = moved;
                    await RecordCueLifecycleAsync("cue.moved", moved, "Human moved a visual cue by dragging it.");
                    Log($"Moved '{mark.Id}' by dragging it.");
                }
            }
        }
        catch (Exception error)
        {
            Log($"Cue editing sync error: {error.Message}");
        }
        finally
        {
            _processingCueEditEvents = false;
        }
    }

    private static string CueEditEventDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asha", "cue-edit-events");

    private int ClearOrphanedOverlayWindows()
    {
        var overlay = Path.GetFullPath(Path.Combine(_repositoryRoot, "overlay", "bin", "Release", "net8.0-windows", "asha-overlay.exe"));
        var cleared = 0;
        foreach (var process in Process.GetProcessesByName("asha-overlay"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (!string.Equals(executable, overlay, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(entireProcessTree: true);
                cleared++;
            }
            catch
            {
                // A process can exit between enumeration and inspection; that
                // already fulfils the goal of removing the visual cue.
            }
            finally
            {
                process.Dispose();
            }
        }
        return cleared;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyQuitting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _tapToListenTimer.Stop();
        _conversationBoundaryTimer.Stop();
        _cueEditEventTimer.Stop();
        StopCueDrawingHook();
        _cueDrawingPreview?.Close();
        _conversationActive = false;
        _voiceTurnCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _microphone.Dispose();
        _voiceSession.Dispose();
        _screenObserver.Dispose();
        _awarenessCoordinator.Dispose();
        _lifetimeCancellation.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        if (_source is not null)
        {
            _source.RemoveHook(WindowMessageHook);
            if (_markHotkeyRegistered) UnregisterHotKey(_source.Handle, MarkHotkeyId);
            UnregisterHotKey(_source.Handle, MoveHotkeyId);
            UnregisterHotKey(_source.Handle, ControlsHotkeyId);
        }
        try { StartAshaWithoutWaiting("clear"); } catch { }
    }

    private static string ShortReason(Exception error) =>
        error.Message.Length > 120 ? error.Message[..120] + "…" : error.Message;

    private async Task<CommandResult> RunAshaAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = NodeExecutable(),
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(_repositoryRoot, "bin", "asha.mjs"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the ASHA CLI.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = new CommandResult((await standardOutput).Trim(), (await standardError).Trim(), process.ExitCode);
        if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? "ASHA command failed." : result.StandardError);
        return result;
    }

    private Task RecordCueCreatedAsync(LiveMark cue, SurfaceTarget? surface) =>
        RecordCueLifecycleAsync(
            "cue.created",
            cue,
            $"Human created a {cue.Kind} visual cue.",
            surface);

    private Task RecordCueLifecycleAsync(string type, LiveMark cue, string note, SurfaceTarget? surface = null)
    {
        object target = surface is null
            ? new { app = "desktop", label = cue.Label ?? "visual cue", control = cue.Kind, x = cue.X, y = cue.Y, w = cue.W, h = cue.H }
            : new
            {
                app = surface.ProcessName,
                label = cue.Label ?? surface.DisplayName,
                control = surface.WindowClass,
                x = cue.X,
                y = cue.Y,
                w = cue.W,
                h = cue.H,
            };
        return RecordActiveSessionEventAsync(
            type,
            note,
            "human",
            "teach_surface",
            target,
            cue: CuePayload(cue));
    }

    private static object CuePayload(LiveMark cue) => new
    {
        id = cue.Id,
        kind = cue.Kind,
        x = cue.X,
        y = cue.Y,
        w = cue.W,
        h = cue.H,
        label = cue.Label,
        color = cue.Color,
    };

    private static object EvidencePayload(VisualEvidenceBundle bundle) => new
    {
        privacy = "local",
        beforeFile = bundle.BeforeFile,
        afterFile = bundle.AfterFile,
        contextFile = bundle.ContextFile,
        changedScore = bundle.ChangedScore,
        contextX = bundle.ContextX,
        contextY = bundle.ContextY,
        contextWidth = bundle.ContextWidth,
        contextHeight = bundle.ContextHeight,
        contextPixelWidth = bundle.ContextPixelWidth,
        contextPixelHeight = bundle.ContextPixelHeight,
    };

    private async Task RestoreActiveSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId)) return;

        try
        {
            var result = await RunAshaAsync("session", "show", _activeSessionId);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var session = document.RootElement.GetProperty("session");
            if (session.TryGetProperty("closedAt", out var closedAt) && closedAt.ValueKind == JsonValueKind.String)
            {
                ClearActiveSession();
                return;
            }

            var title = session.TryGetProperty("title", out var titleValue)
                ? titleValue.GetString() ?? "Shared-attention session"
                : "Shared-attention session";
            SetActiveSession(_activeSessionId, title, save: false);
            _activeSessionNeedsTitle = title.StartsWith("Shared attention ", StringComparison.OrdinalIgnoreCase);
            await LoadSessionMemoryAsync(_activeSessionId);
            Log("Resumed the existing local shared-attention session.");
        }
        catch (Exception error)
        {
            ClearActiveSession();
            Log($"Could not resume the previous session: {error.Message}");
        }
    }

    private void SetActiveSession(string sessionId, string title, bool save = true)
    {
        _activeSessionId = sessionId;
        _activeSessionTitle = title;
        _preferences.ActiveSessionId = sessionId;
        if (save) _preferences.Save();
        _screenObserver.Start(_preferences.Vision);
        _awarenessCoordinator.Start(_preferences.Vision);
        SessionButton.Content = "End session";
        SessionStatusText.Text = $"Recording locally: {title}. Conversation and teaching from this point are kept together.";
        CurrentSessionText.Text = $"Session: {title}";
        AwarenessStatusText.Text = _preferences.Vision == VisionPreference.Off
            ? "Local awareness is off."
            : "Local awareness is starting…";
    }

    private void ClearActiveSession()
    {
        _controlLease = null;
        try { StartAshaWithoutWaiting("clear", ControlPresenceMarkId); } catch { }
        _screenObserver.Stop();
        _awarenessCoordinator.Stop();
        _latestVisionEvidence = null;
        _liveAwarenessContext = null;
        _pendingLiveScreenChange = null;
        _lastLiveAwarenessRefreshUtc = DateTime.MinValue;
        _activeSessionId = null;
        _activeSessionTitle = null;
        _activeSessionNeedsTitle = false;
        _preferences.ActiveSessionId = null;
        _preferences.Save();
        SessionButton.Content = "Start session";
        SessionStatusText.Text = "No durable session is active.";
        CurrentSessionText.Text = "Session: none — the next conversation starts one";
        AwarenessStatusText.Text = "Local awareness is idle until you start a session.";
        _conversationMessages.Clear();
        _voiceSession.ResetConversationMemory();
        UpdateControlLeaseUi();
    }

    private async Task LoadSessionMemoryAsync(string sessionId)
    {
        var fullTranscript = await SessionTranscriptStore.ReadAsync(sessionId);
        _conversationMessages.Clear();
        foreach (var message in fullTranscript) _conversationMessages.Add(message);
        await RefreshSessionMemoryAsync(sessionId, forceCompression: true);
        Log($"Loaded {fullTranscript.Count} full-log conversation messages for this session.");
    }

    private async Task RefreshSessionMemoryAsync(string sessionId, bool forceCompression)
    {
        await _memoryRefreshGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            var fullTranscript = await SessionTranscriptStore.ReadAsync(sessionId);
            var partition = SessionMemoryStore.Partition(fullTranscript);
            var snapshot = await SessionMemoryStore.ReadAsync(sessionId);
            var covered = snapshot.CoveredMessageCount;
            var summary = snapshot.Summary;
            if (covered < 0 || covered > partition.OlderMessageCount)
            {
                covered = 0;
                summary = string.Empty;
            }

            var uncoveredCount = partition.OlderMessageCount - covered;
            if (uncoveredCount > 0 && (forceCompression || uncoveredCount >= 8))
            {
                var segment = fullTranscript.Skip(covered).Take(uncoveredCount).ToArray();
                summary = await _voiceSession.CompressConversationAsync(summary, segment, _lifetimeCancellation.Token);
                covered = partition.OlderMessageCount;
                snapshot = new SessionMemorySnapshot(covered, summary, DateTime.UtcNow);
                await SessionMemoryStore.WriteAsync(sessionId, snapshot);
                Log($"Updated derived session-memory summary through full-log message {covered}; the complete transcript was not changed.");
            }

            if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                // Until a small new segment reaches the compression threshold,
                // keep it verbatim alongside the normal recent window so there
                // is never a gap between the summary and recent conversation.
                var contextStart = Math.Min(covered, fullTranscript.Count);
                _voiceSession.LoadConversationMemory(fullTranscript.Skip(contextStart), summary);
            }
        }
        catch (OperationCanceledException)
        {
            // ASHA is closing.
        }
        catch (Exception error)
        {
            Log($"Session-memory refresh error: {error.Message}");
        }
        finally
        {
            _memoryRefreshGate.Release();
        }
    }

    private async Task MaybeNameActiveSessionAsync(string firstMeaningfulWords)
    {
        if (!_activeSessionNeedsTitle || string.IsNullOrWhiteSpace(_activeSessionId)) return;
        var normalized = Regex.Replace(firstMeaningfulWords, @"\s+", " ").Trim(' ', '.', ',', '?', '!', ':', ';', '-', '—');
        if (string.IsNullOrWhiteSpace(normalized) ||
            Regex.IsMatch(normalized, @"^(asha[,.]?\s*)?(can you hear me|hello|hi|test(ing)?|one[, ]+two[, ]+three)\b", RegexOptions.IgnoreCase))
            return;

        normalized = Regex.Replace(normalized, @"^(asha[,.]?\s*)?(please\s+)?(can|could|would|will)\s+you\s+", string.Empty, RegexOptions.IgnoreCase);
        if (normalized.Length > 68)
        {
            normalized = normalized[..68];
            var finalSpace = normalized.LastIndexOf(' ');
            if (finalSpace >= 36) normalized = normalized[..finalSpace];
        }
        if (normalized.Length < 3) return;
        var title = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        var sessionId = _activeSessionId;
        try
        {
            await RunAshaAsync("session", "rename", sessionId, "--title", title);
            if (!string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)) return;
            _activeSessionTitle = title;
            _activeSessionNeedsTitle = false;
            SessionStatusText.Text = $"Saved locally: {title}. Full logs and session memory stay together.";
            CurrentSessionText.Text = $"Session: {title}";
            Log($"Session received an automatic title: {title}.");
        }
        catch (Exception error)
        {
            Log($"Could not name the active session: {error.Message}");
        }
    }

    private async Task PersistConversationAsync(string sessionId, ConversationMessage message)
    {
        await _sessionWriteGate.WaitAsync();
        try
        {
            await SessionTranscriptStore.AppendAsync(sessionId, message);
            var eventValue = new
            {
                actor = string.Equals(message.Speaker, "ASHA", StringComparison.OrdinalIgnoreCase) ? "model" : "human",
                type = "conversation.message",
                intent = "shared_attention_conversation",
                note = $"{message.Speaker} said this during the local shared-attention session.",
                content = LedgerContent(message.Text),
                target = new { app = "ASHA", label = message.Speaker, control = "conversation" },
            };
            await RunAshaAsync("session", "record", sessionId, JsonSerializer.Serialize(eventValue, JsonOptions));
        }
        catch (Exception error)
        {
            Log($"Could not retain a conversation message: {error.Message}");
        }
        finally
        {
            _sessionWriteGate.Release();
        }
    }

    private async Task<VisualEvidenceBundle?> CaptureVisionEvidenceAsync(
        string reason,
        int anchorX,
        int anchorY,
        SurfaceTarget? surface,
        DesktopCaptureRegion? requestedRegion = null)
    {
        var sessionId = _activeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || _preferences.Vision == VisionPreference.Off) return null;

        try
        {
            StatusText.Text = "Saving local visual evidence…";
            var bundle = await _screenObserver.PreserveEvidenceAsync(sessionId, reason, anchorX, anchorY, requestedRegion);
            if (bundle is null) return null;
            object target = surface is null
                ? new { app = "desktop", label = reason, control = "screen", x = anchorX, y = anchorY }
                : new { app = surface.ProcessName, label = surface.DisplayName, control = surface.WindowClass, x = anchorX, y = anchorY, w = surface.Width, h = surface.Height };
            await RecordActiveSessionEventAsync(
                "vision.evidence_captured",
                $"Saved a local before-and-after view for {reason}. Images remain on this computer.",
                "system",
                "ground_desktop",
                target,
                EvidencePayload(bundle));
            _latestVisionEvidence = bundle;
            StatusText.Text = "Local visual evidence saved for this session.";
            Log($"Local vision evidence saved for {reason} (change score {bundle.ChangedScore:0.000}).");
            return bundle;
        }
        catch (Exception error)
        {
            StatusText.Text = $"Could not capture visual evidence: {ShortReason(error)}";
            Log($"Visual evidence capture error: {error.Message}");
            return null;
        }
    }

    private async Task<VisionAttachment?> ResolveVisionForTranscriptAsync(string transcript, VisionRequest request, CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ResolveVisionForTranscriptAsync(transcript, request, cancellationToken)).Task.Unwrap();

        var personSelected = request.Kind == VisionRequestKind.PersonSelected;
        if (personSelected && !_shareVisionOnNextTurn) return null;
        if (personSelected) _shareVisionOnNextTurn = false;

        if (string.IsNullOrWhiteSpace(_activeSessionId) ||
            _preferences.Vision == VisionPreference.Off ||
            !_preferences.AllowRemoteVision ||
            !_voiceSession.SupportsVision)
            return null;

        var evidence = _latestVisionEvidence;
        var effectiveScope = request.Scope;
        var scene = _awarenessCoordinator.Current;
        SurfaceTarget? selectedSurface = null;
        if (request.Kind == VisionRequestKind.ModelRequested)
        {
            var pointer = scene?.Pointer;
            if (pointer is null && GetCursorPos(out var currentPointer))
                pointer = new AwarenessPoint(currentPointer.X, currentPointer.Y);
            if (pointer is null && request.Region is null) return null;

            effectiveScope = request.Region is null
                ? ResolveVisionScope(transcript, request.Scope, scene)
                : VisionRequestScope.Region;
            var requestedRegion = request.Region ?? ResolveViewRegion(effectiveScope, scene, pointer!);
            if (requestedRegion is not null && request.PreferTextDetail && !requestedRegion.PreferTextDetail)
                requestedRegion = requestedRegion with { PreferTextDetail = true };
            var anchorX = requestedRegion is null ? pointer!.X : requestedRegion.X + (requestedRegion.Width / 2);
            var anchorY = requestedRegion is null ? pointer!.Y : requestedRegion.Y + (requestedRegion.Height / 2);
            var nativePoint = new NativePoint { X = anchorX, Y = anchorY };
            selectedSurface = effectiveScope == VisionRequestScope.ForegroundWindow && scene?.Foreground is { } foreground
                ? new SurfaceTarget(foreground.ProcessName, foreground.Title, foreground.WindowClass, foreground.X, foreground.Y, foreground.Width, foreground.Height)
                : ResolveTopmostSurface(nativePoint);
            evidence = await CaptureVisionEvidenceAsync($"model requested {VisionScopeLabel(effectiveScope)}", anchorX, anchorY, selectedSurface, requestedRegion);
            if (evidence is not null) await ShowTransientCaptureBoundaryAsync(evidence);
        }

        if (evidence is null) return null;

        var attachment = LoadVisionAttachment(evidence, BuildVisionRuntimeContext(scene, selectedSurface, effectiveScope));
        if (attachment is null)
        {
            StatusText.Text = "ASHA could not read the selected view.";
            return null;
        }

        await RecordActiveSessionEventAsync(
            "vision.shared_with_provider",
            personSelected
                ? "The person selected one current desktop view for ASHA's next spoken turn."
                : "ASHA requested one current desktop view because the person's spoken turn required visual context.",
            "system",
            personSelected ? "selected_voice_turn_awareness" : "model_requested_awareness",
            new
            {
                app = "desktop",
                label = personSelected ? "person-selected view" : $"model-requested {VisionScopeLabel(effectiveScope)}",
                control = personSelected ? "one-view visual share" : VisionScopeLabel(effectiveScope),
                x = evidence.ContextX,
                y = evidence.ContextY,
                w = evidence.ContextWidth,
                h = evidence.ContextHeight,
            },
            EvidencePayload(evidence));
        StatusText.Text = personSelected ? "ASHA is looking at the selected view…" : "ASHA requested and received one current view…";
        return attachment;
    }

    private static string? BuildVisionRuntimeContext(AwarenessScene? scene, SurfaceTarget? selectedSurface, VisionRequestScope scope)
    {
        var parts = new List<string>(2);
        if (scene?.Foreground is { } foreground)
            parts.Add($"Windows identifies the current foreground application as {foreground.ProcessName}, with window title {foreground.DisplayName}.");
        if (selectedSurface is not null && scope != VisionRequestScope.EntireDesktop)
            parts.Add($"Windows identifies the top-level surface at the selected region as {selectedSurface.ProcessName}, with window title {selectedSurface.DisplayName}.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static DesktopCaptureRegion? ResolveViewRegion(VisionRequestScope scope, AwarenessScene? scene, AwarenessPoint pointer)
    {
        if (scope == VisionRequestScope.PointerArea) return null;
        if (scope == VisionRequestScope.ForegroundWindow && scene?.Foreground is { Width: > 0, Height: > 0 } foreground)
            return new DesktopCaptureRegion(foreground.X, foreground.Y, foreground.Width, foreground.Height, PreferTextDetail: true);

        var virtualScreen = Forms.SystemInformation.VirtualScreen;
        if (scope == VisionRequestScope.EntireDesktop)
            return new DesktopCaptureRegion(virtualScreen.Left, virtualScreen.Top, virtualScreen.Width, virtualScreen.Height);

        var monitor = Forms.Screen.FromPoint(new Drawing.Point(pointer.X, pointer.Y)).Bounds;
        var leftWidth = Math.Max(1, monitor.Width / 2);
        var upperHeight = Math.Max(1, monitor.Height / 2);
        return scope switch
        {
            VisionRequestScope.LeftScreen => new DesktopCaptureRegion(monitor.Left, monitor.Top, leftWidth, monitor.Height),
            VisionRequestScope.RightScreen => new DesktopCaptureRegion(monitor.Left + leftWidth, monitor.Top, monitor.Width - leftWidth, monitor.Height),
            VisionRequestScope.UpperScreen => new DesktopCaptureRegion(monitor.Left, monitor.Top, monitor.Width, upperHeight),
            VisionRequestScope.LowerScreen => new DesktopCaptureRegion(monitor.Left, monitor.Top + upperHeight, monitor.Width, monitor.Height - upperHeight),
            VisionRequestScope.UpperLeftScreen => new DesktopCaptureRegion(monitor.Left, monitor.Top, leftWidth, upperHeight),
            VisionRequestScope.UpperRightScreen => new DesktopCaptureRegion(monitor.Left + leftWidth, monitor.Top, monitor.Width - leftWidth, upperHeight),
            VisionRequestScope.LowerLeftScreen => new DesktopCaptureRegion(monitor.Left, monitor.Top + upperHeight, leftWidth, monitor.Height - upperHeight),
            VisionRequestScope.LowerRightScreen => new DesktopCaptureRegion(monitor.Left + leftWidth, monitor.Top + upperHeight, monitor.Width - leftWidth, monitor.Height - upperHeight),
            _ => null,
        };
    }

    internal static VisionRequestScope ResolveVisionScope(string transcript, VisionRequestScope requested, AwarenessScene? scene)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return requested;
        var text = transcript.Trim();
        var explicitlyPointerBound = Regex.IsMatch(
            text,
            @"\b(?:under|near|around|beside|at)\s+(?:my|the)\s+(?:mouse|cursor|pointer)\b|\bwhere\s+i(?:'m|\s+am)\s+(?:looking|pointing)\b|\b(?:right\s+)?here\b|\bthis\s+(?:spot|area)\b|\b(?:unter|neben|bei|um)\s+(?:meine[rnm]?|der|die|dem)\s*(?:maus|mauszeiger|cursor|zeiger)\b|\b(?:genau\s+)?hier\b|\bdiese\s+stelle\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (explicitlyPointerBound) return VisionRequestScope.PointerArea;

        var explicitlyUpper = Regex.IsMatch(text, @"\b(upper|top|oben|ober\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var explicitlyLower = Regex.IsMatch(text, @"\b(lower|bottom|unten|unter\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var explicitlyLeft = Regex.IsMatch(text, @"\b(left|links|linke\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var explicitlyRight = Regex.IsMatch(text, @"\b(right|rechts|rechte\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (explicitlyUpper && explicitlyLeft) return VisionRequestScope.UpperLeftScreen;
        if (explicitlyUpper && explicitlyRight) return VisionRequestScope.UpperRightScreen;
        if (explicitlyLower && explicitlyLeft) return VisionRequestScope.LowerLeftScreen;
        if (explicitlyLower && explicitlyRight) return VisionRequestScope.LowerRightScreen;
        if (explicitlyUpper) return VisionRequestScope.UpperScreen;
        if (explicitlyLower) return VisionRequestScope.LowerScreen;
        if (explicitlyLeft) return VisionRequestScope.LeftScreen;
        if (explicitlyRight) return VisionRequestScope.RightScreen;

        var explicitlyBroad = Regex.IsMatch(
            text,
            @"\b(entire|whole|full|all)\s+(?:desktop|screen|monitor|display)\b|\bdesktop\s+overview\b|\b(gesamte[nrms]?|ganze[nrms]?)\s+(?:desktop|bildschirm|monitor|anzeige)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (explicitlyBroad) return VisionRequestScope.EntireDesktop;

        var targetsApplicationOrWindow = Regex.IsMatch(
            text,
            @"\b(app|application|program|window|browser|explorer|desktop\s+icon)\b|\b(anwendung|programm|fenster|browser|explorer|desktop\s*symbol)\b|\bwhere\s+(?:is|are).{0,60}\b(open|running)\b|\bwo\s+.{0,60}\b(offen|ge.ffnet|l.uft)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (targetsApplicationOrWindow) return VisionRequestScope.EntireDesktop;

        var targetsInterfaceElement = Regex.IsMatch(
            text,
            @"\b(button|tab|menu|menu\s+item|field|label|link|icon|control|setting|option|panel|sidebar|section|docs|documentation)\b|\b(knopf|schaltfl.che|tab|men.|men.punkt|feld|beschriftung|link|symbol|einstellung|option|bereich|seitenleiste|dokumentation)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (targetsInterfaceElement && scene?.Foreground is { Width: > 0, Height: > 0 })
            return VisionRequestScope.ForegroundWindow;

        return requested;
    }

    private static string VisionScopeLabel(VisionRequestScope scope) => scope switch
    {
        VisionRequestScope.ForegroundWindow => "foreground-window view",
        VisionRequestScope.EntireDesktop => "entire-desktop view",
        VisionRequestScope.LeftScreen => "left-screen view",
        VisionRequestScope.RightScreen => "right-screen view",
        VisionRequestScope.UpperScreen => "upper-screen view",
        VisionRequestScope.LowerScreen => "lower-screen view",
        VisionRequestScope.UpperLeftScreen => "upper-left-screen view",
        VisionRequestScope.UpperRightScreen => "upper-right-screen view",
        VisionRequestScope.LowerLeftScreen => "lower-left-screen view",
        VisionRequestScope.LowerRightScreen => "lower-right-screen view",
        VisionRequestScope.Region => "closer region view",
        _ => "pointer-area view",
    };

    private async Task ShowTransientCaptureBoundaryAsync(VisualEvidenceBundle evidence)
    {
        if (!evidence.ContextX.HasValue || !evidence.ContextY.HasValue ||
            !evidence.ContextWidth.HasValue || !evidence.ContextHeight.HasValue) return;

        var id = $"asha-capture-{Guid.NewGuid():N}";
        var boundary = new MarkRequest(
            id,
            "frame",
            evidence.ContextX.Value,
            evidence.ContextY.Value,
            evidence.ContextWidth.Value,
            evidence.ContextHeight.Value,
            null,
            "#76B6FF");
        try
        {
            await RunAshaAsync("mark", JsonSerializer.Serialize(boundary, JsonOptions));
            Log("ASHA briefly showed the exact desktop region included in her requested view.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1_200);
                try { StartAshaWithoutWaiting("clear", id); } catch { }
            });
        }
        catch (Exception error)
        {
            Log($"Could not show the temporary capture boundary: {error.Message}");
        }
    }

    private static VisionAttachment? LoadVisionAttachment(VisualEvidenceBundle evidence, string? desktopContext = null)
    {
        var relative = evidence.ContextFile ?? evidence.AfterFile;
        var runtimeRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asha"));
        var path = Path.GetFullPath(Path.Combine(runtimeRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(runtimeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;

        var info = new FileInfo(path);
        if (info.Length > 8 * 1024 * 1024 && evidence.ContextFile is not null)
        {
            path = Path.GetFullPath(Path.Combine(runtimeRoot, evidence.AfterFile.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path)) return null;
            info = new FileInfo(path);
        }
        if (info.Length == 0 || info.Length > 20 * 1024 * 1024) return null;
        return new VisionAttachment(
            Path.GetFileName(path),
            File.ReadAllBytes(path),
            evidence.ContextX,
            evidence.ContextY,
            evidence.ContextWidth,
            evidence.ContextHeight,
            evidence.ContextPixelWidth,
            evidence.ContextPixelHeight,
            desktopContext);
    }

    private async Task<string> ExecuteVisualToolAsync(AshaVisualToolCall call, VisionAttachment? vision, CancellationToken cancellationToken)
    {
        return await Dispatcher.InvokeAsync(() => ExecuteVisualToolOnUiAsync(call, vision, cancellationToken)).Task.Unwrap();
    }

    private async Task<string> ExecuteVisualToolOnUiAsync(AshaVisualToolCall call, VisionAttachment? vision, CancellationToken cancellationToken)
    {
        if (string.Equals(call.Name, "asha_clear_guidance", StringComparison.Ordinal))
            return await ExecuteClearGuidanceOnUiAsync(call);
        if (string.Equals(call.Name, "asha_open_application", StringComparison.Ordinal))
            return await ExecuteOpenApplicationOnUiAsync(call, cancellationToken);
        if (string.Equals(call.Name, "asha_open_folder", StringComparison.Ordinal))
            return await ExecuteOpenFolderOnUiAsync(call, cancellationToken);
        if (string.Equals(call.Name, "asha_desktop_action", StringComparison.Ordinal))
            return vision is null
                ? JsonSerializer.Serialize(new { ok = false, error = "A current coordinate-mapped desktop view is required before physical pointer input." })
                : await ExecuteDesktopActionOnUiAsync(call, vision, cancellationToken);
        if (!string.Equals(call.Name, "asha_mark", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { ok = false, error = "That ASHA tool is not available." });
        if (string.IsNullOrWhiteSpace(_activeSessionId) || vision is null || !vision.HasDesktopMapping)
            return JsonSerializer.Serialize(new { ok = false, error = "An active session and coordinate-mapped visual evidence are required." });
        if (!TryReadToolString(call.Arguments, "kind", out var kind) || !new[] { "dot", "circle", "box", "arrow", "label" }.Contains(kind, StringComparer.Ordinal))
            return JsonSerializer.Serialize(new { ok = false, error = "Choose dot, circle, box, arrow, or label." });
        var hasBundledBox = TryReadBundledImageBox(call.Arguments, "x", "y", out var bundledLeft, out var bundledTop, out var bundledRight, out var bundledBottom);
        int imageX;
        int imageY;
        if (hasBundledBox && kind is "box" or "arrow")
        {
            imageX = bundledLeft;
            imageY = bundledTop;
        }
        else if (!TryReadImagePoint(call.Arguments, "x", "y", out imageX, out imageY))
            return JsonSerializer.Serialize(new { ok = false, error = "Visual guidance requires a point inside the supplied image." });

        var label = TryReadToolString(call.Arguments, "label", out var suppliedLabel) && suppliedLabel.Length <= 80 ? suppliedLabel : null;
        var visibleText = TryReadToolString(call.Arguments, "visible_text", out var suppliedVisibleText) && suppliedVisibleText.Length <= 120
            ? suppliedVisibleText
            : label;
        var requiresTextGrounding = TryReadToolString(call.Arguments, "target_type", out var targetType) &&
                                    string.Equals(targetType, "text", StringComparison.Ordinal);
        int? imageWidth = null;
        int? imageHeight = null;
        if (kind == "box")
        {
            var hasExplicitSize = TryReadToolDimension(call.Arguments, "w", 2, vision.ImageWidth, out imageWidth) &&
                                  TryReadToolDimension(call.Arguments, "h", 2, vision.ImageHeight, out imageHeight);
            if (!hasExplicitSize && hasBundledBox)
            {
                imageWidth = Math.Abs(bundledRight - bundledLeft);
                imageHeight = Math.Abs(bundledBottom - bundledTop);
                imageX = Math.Min(bundledLeft, bundledRight);
                imageY = Math.Min(bundledTop, bundledBottom);
            }
            if (imageWidth is null or < 2 || imageHeight is null or < 2 ||
                imageX + imageWidth > vision.ImageWidth || imageY + imageHeight > vision.ImageHeight)
                return JsonSerializer.Serialize(new { ok = false, error = "A box must fit inside the supplied image." });
        }
        else if (kind == "arrow")
        {
            var hasExplicitVector = TryReadToolSignedDimension(call.Arguments, "w", -vision.ImageWidth, vision.ImageWidth, out imageWidth) &&
                                    TryReadToolSignedDimension(call.Arguments, "h", -vision.ImageHeight, vision.ImageHeight, out imageHeight);
            if (!hasExplicitVector && hasBundledBox)
            {
                imageWidth = bundledRight - bundledLeft;
                imageHeight = bundledBottom - bundledTop;
            }
            if (imageWidth is null || imageHeight is null ||
                (imageWidth == 0 && imageHeight == 0) ||
                !vision.TryMapImagePoint(imageX + imageWidth!.Value, imageY + imageHeight!.Value, out _, out _))
                return JsonSerializer.Serialize(new { ok = false, error = "An arrow and its tip must fit inside the supplied image." });
        }

        var grounding = "model_coordinates";
        if (kind == "box" && !string.IsNullOrWhiteSpace(visibleText))
        {
            OcrTextMatch? match = null;
            try
            {
                match = await LocalOcrGrounder.FindNearestAsync(
                    vision.Bytes,
                    visibleText,
                    imageX + (imageWidth!.Value / 2),
                    imageY + (imageHeight!.Value / 2),
                    cancellationToken);
                if (match is { } text)
                {
                    const int horizontalPadding = 7;
                    const int verticalPadding = 4;
                    imageX = Math.Max(0, text.X - horizontalPadding);
                    imageY = Math.Max(0, text.Y - verticalPadding);
                    var right = Math.Min(vision.ImageWidth, text.X + text.Width + horizontalPadding);
                    var bottom = Math.Min(vision.ImageHeight, text.Y + text.Height + verticalPadding);
                    imageWidth = Math.Max(2, right - imageX);
                    imageHeight = Math.Max(2, bottom - imageY);
                    grounding = "local_windows_ocr";
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log($"Local OCR grounding was unavailable: {error.Message}");
            }
            if (requiresTextGrounding && match is null)
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"ASHA could not verify the visible text {visibleText} in the selected view, so no mark was shown.",
                });
        }

        if (!vision.TryMapImagePoint(imageX, imageY, out var x, out var y))
            return JsonSerializer.Serialize(new { ok = false, error = "Visual guidance requires a point inside the supplied image." });
        int? width = null;
        int? height = null;
        if (kind == "box")
        {
            width = Math.Max(12, vision.MapImageWidth(imageWidth!.Value));
            height = Math.Max(12, vision.MapImageHeight(imageHeight!.Value));
        }
        else if (kind == "arrow")
        {
            width = vision.MapImageWidth(imageWidth!.Value);
            height = vision.MapImageHeight(imageHeight!.Value);
        }

        var expectedApp = TryReadToolString(call.Arguments, "expected_app", out var suppliedExpectedApp) && suppliedExpectedApp.Length <= 80
            ? suppliedExpectedApp
            : ExpectedAppFromWindowLabel(label);
        if (!string.IsNullOrWhiteSpace(expectedApp))
        {
            var verificationX = kind == "box" && width.HasValue ? x + (width.Value / 2) : x;
            var verificationY = kind == "box" && height.HasValue ? y + (height.Value / 2) : y;
            var exposedSurface = ResolveTopmostSurface(new NativePoint { X = verificationX, Y = verificationY });
            if (!SurfaceMatchesExpectedApplication(expectedApp, exposedSurface))
            {
                var actual = exposedSurface?.DisplayName;
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = string.IsNullOrWhiteSpace(actual)
                        ? $"ASHA could not verify that {expectedApp} is visibly exposed at that location."
                        : $"That location is on {actual}, not visibly on {expectedApp}. ASHA did not show the mark.",
                });
            }
        }
        var id = $"asha-guidance-{Guid.NewGuid():N}";
        var mark = new MarkRequest(id, kind, x, y, width, height, label, VisualGuidanceColor(kind));
        var result = await RunAshaAsync("mark", JsonSerializer.Serialize(mark, JsonOptions));
        using var document = JsonDocument.Parse(result.StandardOutput);
        var markId = document.RootElement.GetProperty("id").GetString() ?? id;
        var guidanceCue = new LiveMark(markId, kind, x, y, width, height, label, VisualGuidanceColor(kind));
        _marks.Add(guidanceCue);
        await RecordActiveSessionEventAsync(
            "guidance.visual_mark_shown",
            $"ASHA showed a {kind} to guide the person to a visible target.",
            "model",
            "teach_human",
            new { app = "desktop", label, control = kind, x, y, w = width, h = height, grounding },
            cue: CuePayload(guidanceCue));
        Log($"ASHA visual guidance: {kind} at {x}, {y}; grounding: {grounding}.");
        return JsonSerializer.Serialize(new { ok = true, id = markId, kind, x, y, label, grounding, action = "visual overlay shown; no mouse or keyboard input occurred" });
    }

    private async Task<string> ExecuteClearGuidanceOnUiAsync(AshaVisualToolCall call)
    {
        var scope = TryReadToolString(call.Arguments, "scope", out var suppliedScope) && suppliedScope is "latest" or "all"
            ? suppliedScope
            : "latest";
        var ids = ReadPersistedGuidanceMarkIds().ToList();
        foreach (var mark in _marks.Where(mark => mark.Id.StartsWith("asha-guidance-", StringComparison.Ordinal)))
        {
            if (!ids.Contains(mark.Id, StringComparer.Ordinal)) ids.Add(mark.Id);
        }

        var selected = scope == "all" ? ids : ids.TakeLast(1).ToList();
        foreach (var id in selected)
            await RunAshaAsync("clear", id);
        foreach (var mark in _marks.Where(mark => selected.Contains(mark.Id, StringComparer.Ordinal)).ToArray())
            _marks.Remove(mark);

        if (selected.Count > 0)
        {
            await RecordActiveSessionEventAsync(
                "guidance.visual_marks_cleared",
                scope == "all" ? "ASHA removed all of her visual guidance marks." : "ASHA removed her latest visual guidance mark.",
                "model",
                "teach_human",
                new { app = "desktop", label = "ASHA visual guidance", control = $"clear_{scope}", count = selected.Count });
        }
        StatusText.Text = selected.Count == 0 ? "ASHA has no highlights to remove." : "ASHA removed her visual guidance.";
        Log(selected.Count == 0 ? "No ASHA guidance marks were present." : $"ASHA cleared {selected.Count} model-created guidance mark(s).");
        return JsonSerializer.Serialize(new { ok = true, scope, removed = selected.Count, action = "only ASHA-created visual guidance was removed" });
    }

    private static IReadOnlyList<string> ReadPersistedGuidanceMarkIds()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asha", "marks.json");
            if (!File.Exists(path)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Object) return [];
            return marks.EnumerateObject()
                .Select(mark => mark.Name)
                .Where(id => id.StartsWith("asha-guidance-", StringComparison.Ordinal))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task<string> ExecuteOpenApplicationOnUiAsync(AshaVisualToolCall call, CancellationToken cancellationToken)
    {
        var access = CurrentControlAccess();
        if (!access.IsLeaseActive)
            return JsonSerializer.Serialize(new { ok = false, error = "Computer control is disabled. Start an allowed control session first." });
        if (!access.CanOpenApplicationsAndFolders)
            return JsonSerializer.Serialize(new { ok = false, error = "Opening applications and folders is not permitted by the current policy and control lease." });
        if (string.IsNullOrWhiteSpace(_activeSessionId))
            return JsonSerializer.Serialize(new { ok = false, error = "Start a shared-attention session before opening an application." });
        if (!TryReadToolString(call.Arguments, "application", out var application))
            return JsonSerializer.Serialize(new { ok = false, error = "Choose an installed application by its display name." });

        var result = await ApplicationLauncher.OpenAsync(application, cancellationToken);
        await RecordActiveSessionEventAsync(
            "control.application_opened",
            $"ASHA opened or activated the installed application {result.ResolvedName} after the person enabled computer control.",
            "model",
            "computer_control",
            new { app = result.ProcessName, label = result.WindowTitle, control = "open_application" });
        StatusText.Text = $"Opened {result.ResolvedName}.";
        Log($"ASHA opened {result.ResolvedName}; visible window verified as {result.WindowTitle}.");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            requested = result.RequestedName,
            application = result.ResolvedName,
            process = result.ProcessName,
            window = result.WindowTitle,
            activated_existing = result.ActivatedExisting,
            verification = "a visible application window was found and brought forward",
        });
    }

    private async Task<string> ExecuteOpenFolderOnUiAsync(AshaVisualToolCall call, CancellationToken cancellationToken)
    {
        var access = CurrentControlAccess();
        if (!access.IsLeaseActive)
            return JsonSerializer.Serialize(new { ok = false, error = "Computer control is disabled. Start an allowed control session first." });
        if (!access.CanOpenApplicationsAndFolders)
            return JsonSerializer.Serialize(new { ok = false, error = "Opening applications and folders is not permitted by the current policy and control lease." });
        if (string.IsNullOrWhiteSpace(_activeSessionId))
            return JsonSerializer.Serialize(new { ok = false, error = "Start a shared-attention session before opening a folder." });
        if (!TryReadToolString(call.Arguments, "folder", out var folder))
            return JsonSerializer.Serialize(new { ok = false, error = "Choose an existing ordinary folder by name or local path." });

        var result = await FolderLauncher.OpenAsync(folder, cancellationToken);
        await RecordActiveSessionEventAsync(
            "control.folder_opened",
            "ASHA opened one existing non-system folder in Explorer after the person enabled computer control.",
            "model",
            "computer_control",
            new { app = "explorer", label = result.WindowTitle, control = "open_folder" });
        StatusText.Text = $"Opened {Path.GetFileName(result.Path.TrimEnd(Path.DirectorySeparatorChar))}.";
        Log($"ASHA opened a folder in Explorer and verified the visible window {result.WindowTitle}.");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            folder = result.RequestedFolder,
            window = result.WindowTitle,
            verification = "a visible Explorer window was found",
        });
    }

    private async Task<string> ExecuteDesktopActionOnUiAsync(AshaVisualToolCall call, VisionAttachment vision, CancellationToken cancellationToken)
    {
        var access = CurrentControlAccess();
        if (!access.IsLeaseActive)
            return JsonSerializer.Serialize(new { ok = false, error = "Computer control is disabled. Start an allowed control session first." });
        if (string.IsNullOrWhiteSpace(_activeSessionId) || !vision.HasDesktopMapping)
            return JsonSerializer.Serialize(new { ok = false, error = "A coordinate-mapped image from an active session is required before physical input." });
        if (!TryReadToolString(call.Arguments, "action", out var action))
            return JsonSerializer.Serialize(new { ok = false, error = "A desktop action is required." });
        if (!access.AllowsCurrentPhysicalExecutorAction(action))
        {
            if (action is "type_text" or "key")
                return JsonSerializer.Serialize(new { ok = false, error = "Keyboard interaction is not permitted by the current policy and control lease." });
            if (access.CanInteractWithVirtualCursor)
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "Virtual interaction is permitted, but its background driver is not connected in this build. ASHA did not fall back to your physical cursor.",
                });
            return JsonSerializer.Serialize(new { ok = false, error = "Physical cursor interaction is not permitted by the current policy and control lease." });
        }

        DesktopAction input;
        string visibleLabel;
        switch (action)
        {
            case "move":
            case "click":
            case "double_click":
            case "right_click":
                if (!TryReadVisiblePoint(call.Arguments, "x", "y", vision, out var x, out var y))
                    return JsonSerializer.Serialize(new { ok = false, error = "The requested target must be inside the supplied desktop image." });
                input = new DesktopAction(action, x, y);
                visibleLabel = action switch
                {
                    "move" => "ASHA moves the pointer",
                    "click" => "ASHA clicks",
                    "double_click" => "ASHA double-clicks",
                    _ => "ASHA right-clicks",
                };
                break;
            case "drag":
                if (!TryReadVisiblePoint(call.Arguments, "x", "y", vision, out var startX, out var startY) ||
                    !TryReadVisiblePoint(call.Arguments, "end_x", "end_y", vision, out var endX, out var endY))
                    return JsonSerializer.Serialize(new { ok = false, error = "Both ends of an ASHA drag must be inside the supplied desktop image." });
                input = new DesktopAction(action, startX, startY, endX, endY);
                visibleLabel = "ASHA drags";
                break;
            case "scroll":
                if (!TryReadToolCoordinate(call.Arguments, "delta", out var delta) || delta is < -1200 or > 1200 || delta == 0)
                    return JsonSerializer.Serialize(new { ok = false, error = "Scroll requires a non-zero wheel delta between minus 1200 and 1200." });
                int? scrollX = null, scrollY = null;
                if (call.Arguments.TryGetProperty("x", out _) || call.Arguments.TryGetProperty("y", out _))
                {
                    if (!TryReadVisiblePoint(call.Arguments, "x", "y", vision, out var parsedX, out var parsedY))
                        return JsonSerializer.Serialize(new { ok = false, error = "An optional scroll target must be inside the supplied desktop image." });
                    scrollX = parsedX;
                    scrollY = parsedY;
                }
                input = new DesktopAction(action, scrollX, scrollY, Delta: delta);
                visibleLabel = "ASHA scrolls";
                break;
            case "type_text":
                if (!TryReadToolString(call.Arguments, "text", out var text) || text.Length > 280)
                    return JsonSerializer.Serialize(new { ok = false, error = "Text input must be non-empty and at most 280 characters." });
                if (LooksSensitive(text))
                    return JsonSerializer.Serialize(new { ok = false, error = "ASHA never types credentials, secrets, or recovery codes." });
                input = new DesktopAction(action, Text: text);
                visibleLabel = "ASHA types";
                break;
            case "key":
                if (!TryReadToolString(call.Arguments, "key", out var key) ||
                    !new[] { "enter", "escape", "tab", "space", "backspace", "up", "down", "left", "right" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                    return JsonSerializer.Serialize(new { ok = false, error = "Only the approved navigation keys are available." });
                input = new DesktopAction(action, Key: key);
                visibleLabel = $"ASHA presses {key}";
                break;
            default:
                return JsonSerializer.Serialize(new { ok = false, error = "That physical desktop action is not available." });
        }

        SurfaceTarget? exposedSurface = null;
        if (input.X.HasValue && input.Y.HasValue)
        {
            exposedSurface = ResolveTopmostSurface(new NativePoint { X = input.X.Value, Y = input.Y.Value });
            if (exposedSurface is null)
                return JsonSerializer.Serialize(new { ok = false, error = "Windows could not verify a visible top-layer surface at that target." });
            if (TryReadToolString(call.Arguments, "expected_app", out var expectedApp) &&
                !SurfaceMatchesExpectedApplication(expectedApp, exposedSurface))
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"The target is no longer exposed in {expectedApp}; {exposedSurface.DisplayName} is currently on top there.",
                });
        }
        else if (action is "type_text" or "key" && _awarenessCoordinator.Current?.Foreground is { } focused)
        {
            if (IsProtectedInputSurface(focused.ProcessName, focused.WindowClass))
                return JsonSerializer.Serialize(new { ok = false, error = "ASHA does not type or send keys into terminal, shell, registry, or administrative control surfaces." });
            exposedSurface = new SurfaceTarget(focused.ProcessName, focused.Title, focused.WindowClass, focused.X, focused.Y, focused.Width, focused.Height);
        }

        await ShowTransientControlCueAsync(input, visibleLabel);
        await Task.Delay(180, cancellationToken);
        await DesktopControlExecutor.ExecuteAsync(input, cancellationToken);
        await Task.Delay(180, cancellationToken);

        SurfaceTarget? resultingSurface = null;
        if (GetCursorPos(out var pointer)) resultingSurface = ResolveTopmostSurface(pointer);
        await RecordActiveSessionEventAsync(
            "control.input_sent",
            $"ASHA sent one visible physical {action.Replace('_', ' ')} input after the person enabled computer control.",
            "model",
            "computer_control",
            new
            {
                app = resultingSurface?.ProcessName ?? "desktop",
                label = visibleLabel,
                control = action,
                exposedApp = exposedSurface?.ProcessName,
                exposedWindow = exposedSurface?.DisplayName,
                x = input.X,
                y = input.Y,
                endX = input.EndX,
                endY = input.EndY,
            });
        Log($"{visibleLabel}: physical input sent.");
        StatusText.Text = $"{visibleLabel}. ASHA is checking the visible result.";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            action,
            top_layer = exposedSurface is null ? null : new { app = exposedSurface.ProcessName, window = exposedSurface.DisplayName },
            input = "physical desktop input was sent visibly",
            verification = "a fresh post-action view is required before claiming the intended result",
        });
    }

    private async Task ShowTransientControlCueAsync(DesktopAction action, string label)
    {
        var x = action.X ?? GetSystemMetrics(SmXVirtualScreen) + 42;
        var y = action.Y ?? GetSystemMetrics(SmYVirtualScreen) + 42;
        var id = $"asha-control-{Guid.NewGuid():N}";
        var cue = new MarkRequest(id, action.Kind == "drag" ? "arrow" : "dot", x, y,
            action.Kind == "drag" ? action.EndX!.Value - x : null,
            action.Kind == "drag" ? action.EndY!.Value - y : null,
            label, "#766CFF");
        await RunAshaAsync("mark", JsonSerializer.Serialize(cue, JsonOptions));
        _ = Task.Run(async () =>
        {
            await Task.Delay(1_400);
            try { StartAshaWithoutWaiting("clear", id); } catch { }
        });
    }

    private static bool TryReadVisiblePoint(JsonElement arguments, string xName, string yName, VisionAttachment vision, out int x, out int y)
    {
        x = 0;
        y = 0;
        return TryReadImagePoint(arguments, xName, yName, out var imageX, out var imageY) &&
               vision.TryMapImagePoint(imageX, imageY, out x, out y);
    }

    private static bool TryReadImagePoint(JsonElement arguments, string xName, string yName, out int imageX, out int imageY)
    {
        imageX = 0;
        imageY = 0;
        if (arguments.TryGetProperty(xName, out var bundled) && bundled.ValueKind == JsonValueKind.Array)
        {
            if (!TryReadNumberArray(bundled, out var coordinates) || coordinates.Length is not (2 or 4)) return false;
            var hasY = arguments.TryGetProperty(yName, out var rawY);
            if (hasY && rawY.ValueKind == JsonValueKind.Array &&
                TryReadNumberArray(rawY, out var duplicated) &&
                duplicated.SequenceEqual(coordinates))
            {
                hasY = false;
            }
            if (!hasY)
            {
                if (coordinates.Length == 2)
                {
                    imageX = (int)Math.Round(coordinates[0]);
                    imageY = (int)Math.Round(coordinates[1]);
                }
                else
                {
                    imageX = (int)Math.Round((coordinates[0] + coordinates[2]) / 2d);
                    imageY = (int)Math.Round((coordinates[1] + coordinates[3]) / 2d);
                }
                return true;
            }
        }

        return TryReadToolCoordinate(arguments, xName, out imageX) &&
               TryReadToolCoordinate(arguments, yName, out imageY);
    }

    private static bool TryReadBundledImageBox(JsonElement arguments, string xName, string yName, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        if (!arguments.TryGetProperty(xName, out var bundled) ||
            bundled.ValueKind != JsonValueKind.Array ||
            !TryReadNumberArray(bundled, out var coordinates) ||
            coordinates.Length != 4) return false;
        if (arguments.TryGetProperty(yName, out var rawY))
        {
            if (rawY.ValueKind != JsonValueKind.Array ||
                !TryReadNumberArray(rawY, out var duplicated) ||
                !duplicated.SequenceEqual(coordinates)) return false;
        }
        left = (int)Math.Round(coordinates[0]);
        top = (int)Math.Round(coordinates[1]);
        right = (int)Math.Round(coordinates[2]);
        bottom = (int)Math.Round(coordinates[3]);
        return true;
    }

    private static bool TryReadNumberArray(JsonElement raw, out double[] values)
    {
        values = [];
        if (raw.ValueKind != JsonValueKind.Array) return false;
        var elements = raw.EnumerateArray().ToArray();
        values = new double[elements.Length];
        for (var index = 0; index < elements.Length; index++)
        {
            if (elements[index].ValueKind != JsonValueKind.Number ||
                !elements[index].TryGetDouble(out values[index]) ||
                !double.IsFinite(values[index])) return false;
        }
        return true;
    }

    private static string? ExpectedAppFromWindowLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var match = Regex.Match(label, @"^\s*(.+?)\s+(?:window|app|application|fenster|anwendung)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool SurfaceMatchesExpectedApplication(string expected, SurfaceTarget? surface)
    {
        if (surface is null) return false;
        var expectedName = NormalizeSurfaceName(expected);
        if (expectedName.Length == 0) return true;
        var candidates = new[] { surface.ProcessName, surface.DisplayName, surface.WindowClass }
            .Select(NormalizeSurfaceName)
            .Where(value => value.Length >= 3)
            .ToArray();
        if (candidates.Any(candidate => candidate.Contains(expectedName, StringComparison.Ordinal) || expectedName.Contains(candidate, StringComparison.Ordinal)))
            return true;
        if (expectedName.Contains("fileexplorer", StringComparison.Ordinal) && candidates.Any(candidate => candidate.Contains("explorer", StringComparison.Ordinal))) return true;
        if (expectedName.Contains("outlook", StringComparison.Ordinal) && candidates.Any(candidate => candidate.Contains("outlook", StringComparison.Ordinal) || candidate == "olk")) return true;
        if (expectedName.Contains("edge", StringComparison.Ordinal) && candidates.Any(candidate => candidate.Contains("msedge", StringComparison.Ordinal))) return true;
        if (expectedName.Contains("visualstudiocode", StringComparison.Ordinal) && candidates.Any(candidate => candidate == "code" || candidate.Contains("visualstudiocode", StringComparison.Ordinal))) return true;
        if (expectedName == "desktop" && candidates.Any(candidate => candidate.Contains("progman", StringComparison.Ordinal) || candidate.Contains("workerw", StringComparison.Ordinal))) return true;
        return false;
    }

    private static string NormalizeSurfaceName(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"\b(window|app|application|program|fenster|anwendung|programm)\b|[^a-z0-9]+", string.Empty, RegexOptions.CultureInvariant);

    private static bool LooksSensitive(string text) => Regex.IsMatch(
        text,
        @"(password|passwort|token|secret|api[_ -]?key|recoverys*code|one[- ]?times*(?:code|password)|otp)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsProtectedInputSurface(string processName, string windowClass) =>
        Regex.IsMatch(
            $"{processName} {windowClass}",
            @"\b(cmd|powershell|pwsh|windowsterminal|wsl|bash|regedit|mmc|taskmgr|consolewindowclass|cascadia_hosting_window_class)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool TryReadToolString(JsonElement arguments, string name, out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetProperty(name, out var raw) || raw.ValueKind != JsonValueKind.String) return false;
        value = raw.GetString()?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadToolCoordinate(JsonElement arguments, string name, out int value)
    {
        value = 0;
        if (!arguments.TryGetProperty(name, out var raw)) return false;
        if (raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out var number) && double.IsFinite(number))
        {
            value = (int)Math.Round(number);
            return true;
        }
        if (raw.ValueKind != JsonValueKind.Array) return false;
        var interval = raw.EnumerateArray().ToArray();
        if (interval.Length is < 1 or > 2) return false;
        var values = new double[interval.Length];
        for (var index = 0; index < interval.Length; index++)
        {
            if (interval[index].ValueKind != JsonValueKind.Number ||
                !interval[index].TryGetDouble(out values[index]) ||
                !double.IsFinite(values[index])) return false;
        }
        value = (int)Math.Round(values.Average());
        return true;
    }

    internal static bool TryReadToolCoordinateForTesting(string json, string name, out int value)
    {
        using var document = JsonDocument.Parse(json);
        return TryReadToolCoordinate(document.RootElement, name, out value);
    }

    internal static bool TryReadImagePointForTesting(string json, string xName, string yName, out int x, out int y)
    {
        using var document = JsonDocument.Parse(json);
        return TryReadImagePoint(document.RootElement, xName, yName, out x, out y);
    }

    private static bool TryReadToolDimension(JsonElement arguments, string name, int minimum, int maximum, out int? value)
    {
        value = null;
        if (!TryReadToolCoordinate(arguments, name, out var number) || number < minimum || number > maximum) return false;
        value = number;
        return true;
    }

    private static bool TryReadToolSignedDimension(JsonElement arguments, string name, int minimum, int maximum, out int? value)
    {
        value = null;
        if (!TryReadToolCoordinate(arguments, name, out var number) || number < minimum || number > maximum) return false;
        value = number;
        return true;
    }

    private static string VisualGuidanceColor(string kind) => kind switch
    {
        "arrow" => "#766CFF",
        "box" => "#4C8DFF",
        "circle" => "#4C8DFF",
        _ => "#FFB020",
    };

    private async Task RecordActiveSessionEventAsync(
        string type,
        string note,
        string actor = "system",
        string? intent = null,
        object? target = null,
        object? evidence = null,
        object? cue = null,
        string? content = null)
    {
        var sessionId = _activeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        await _sessionWriteGate.WaitAsync();
        try
        {
            var eventValue = new { actor, type, intent, note, content, target, cue, evidence };
            await RunAshaAsync("session", "record", sessionId, JsonSerializer.Serialize(eventValue, JsonOptions));
        }
        catch (Exception error)
        {
            Log($"Could not add '{type}' to the active session: {error.Message}");
        }
        finally
        {
            _sessionWriteGate.Release();
        }
    }

    private static string LedgerContent(string text)
    {
        const int maxCharacters = 6_000;
        var normalized = text.Trim();
        return normalized.Length <= maxCharacters ? normalized : normalized[..(maxCharacters - 1)] + "…";
    }

    private static SurfaceTarget? ResolveTopmostSurface(NativePoint point)
    {
        var hit = WindowFromPoint(point);
        if (hit == IntPtr.Zero) return null;

        var root = GetAncestor(hit, GaRoot);
        if (root != IntPtr.Zero) hit = root;
        if (!IsWindowVisible(hit) || !GetWindowRect(hit, out var bounds)) return null;

        // A taskbar icon belongs visually to the app the human chose, not to
        // Explorer, which owns the taskbar window. Resolve the UIA button first.
        if (string.Equals(ReadWindowClass(hit), "Shell_TrayWnd", StringComparison.Ordinal))
        {
            var taskbarTarget = ResolveTaskbarButton(point);
            if (taskbarTarget is not null) return taskbarTarget;
        }

        GetWindowThreadProcessId(hit, out var processId);
        if (processId == 0) return null;

        string processName;
        try { processName = Process.GetProcessById((int)processId).ProcessName; }
        catch { processName = "unknown-process"; }

        return new SurfaceTarget(
            processName,
            ReadWindowText(hit),
            ReadWindowClass(hit),
            bounds.Left,
            bounds.Top,
            Math.Max(0, bounds.Right - bounds.Left),
            Math.Max(0, bounds.Bottom - bounds.Top));
    }

    private static SurfaceTarget? ResolveTaskbarButton(NativePoint point)
    {
        try
        {
            AutomationElement? element = AutomationElement.FromPoint(new Point(point.X, point.Y));
            for (var depth = 0; element is not null && depth < 8; depth++)
            {
                var current = element.Current;
                var name = current.Name?.Trim();
                if (current.ControlType == ControlType.Button && !string.IsNullOrWhiteSpace(name))
                {
                    var rectangle = current.BoundingRectangle;
                    return new SurfaceTarget(
                        name,
                        $"Taskbar target: {name}",
                        "taskbar-button",
                        (int)Math.Round(rectangle.Left),
                        (int)Math.Round(rectangle.Top),
                        Math.Max(0, (int)Math.Round(rectangle.Width)),
                        Math.Max(0, (int)Math.Round(rectangle.Height)));
                }
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch
        {
            // UI Automation is a best-effort semantic enhancement. The caller
            // still records the ordinary topmost window when it is unavailable.
        }

        return null;
    }

    private static string ReadWindowText(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private void StartAshaWithoutWaiting(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = NodeExecutable(),
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(_repositoryRoot, "bin", "asha.mjs"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process.Start(start);
    }

    private static int ReadDimension(TextBox textBox, int fallback) => int.TryParse(textBox.Text, out var value) && value > 0 ? value : fallback;
    private static int ReadVectorDimension(TextBox textBox, int fallback) => int.TryParse(textBox.Text, out var value) && value != 0 ? value : fallback;
    private static int DefaultWidth(string kind) => kind == "arrow" ? 220 : kind == "box" ? 220 : 96;
    private static int DefaultHeight(string kind) => kind == "arrow" ? 100 : kind == "box" ? 100 : 96;
    private static string ReadColor(string? value) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), "^#[0-9a-fA-F]{6}$")
        ? value.Trim().ToUpperInvariant()
        : "#4C8DFF";

    private static string NodeExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("ASHA_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        const string standardPath = @"C:\Program Files\nodejs\node.exe";
        return File.Exists(standardPath) ? standardPath : "node";
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the ASHA repository next to this application.");
    }

    private void Log(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private async Task AddConversationAsync(string speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var message = new ConversationMessage(DateTime.Now, speaker, text);
        _conversationMessages.Add(message);
        AttachedChatList.ScrollIntoView(message);
        _conversationWindow?.ScrollToLatest();
        Log($"{speaker}: {text}");
        var sessionId = _activeSessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
            await PersistConversationAsync(sessionId, message);
    }

    private sealed record MarkRequest(string Id, string Kind, int X, int Y, int? W, int? H, string? Label, string Color);
    private sealed record MoveRequest(int X, int Y);
    private sealed record SurfaceTarget(string ProcessName, string WindowTitle, string WindowClass, int X, int Y, int Width, int Height)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(WindowTitle) ? "unnamed window" : WindowTitle;
    }
    private sealed record LiveMark(string Id, string Kind, int X, int Y, int? W, int? H, string? Label, string Color)
    {
        public string Display => $"{Kind} · {Label ?? "(no label)"} · {X}, {Y}";
    }
    private sealed record CueUpdateRequest(string Kind, int X, int Y, int? W, int? H, string? Label, string Color);
    private sealed record CommandResult(string StandardOutput, string StandardError, int ExitCode);
    private sealed record CueEditEvent(string Type, string Id, int? X, int? Y);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int maximumCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookExKeyboard(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private sealed class CueDrawingPreviewWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly IntPtr HwndTopmost = new(-1);
        private readonly string _kind;
        private readonly Canvas? _arrowCanvas;
        private readonly WpfLine? _arrowLine;
        private readonly WpfPolyline? _arrowHead;

        public CueDrawingPreviewWindow(string kind)
        {
            _kind = kind;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = 1;
            Height = 1;
            if (kind == "arrow")
            {
                var stroke = new SolidColorBrush(Color.FromRgb(118, 108, 255));
                _arrowCanvas = new Canvas { Background = Brushes.Transparent };
                _arrowLine = new WpfLine
                {
                    Stroke = stroke,
                    StrokeThickness = 6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _arrowHead = new WpfPolyline
                {
                    Stroke = stroke,
                    StrokeThickness = 6,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _arrowCanvas.Children.Add(_arrowLine);
                _arrowCanvas.Children.Add(_arrowHead);
                Content = _arrowCanvas;
            }
            else
            {
                Content = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(76, 141, 255)),
                    BorderThickness = new Thickness(3),
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromArgb(22, 76, 141, 255)),
                };
            }
            SourceInitialized += (_, _) => MakeClickThrough();
        }

        public void UpdateGesture(NativePoint start, NativePoint current)
        {
            if (!IsVisible) Show();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var padding = _kind == "arrow" ? 24 : 0;
            var left = Math.Min(start.X, current.X) - padding;
            var top = Math.Min(start.Y, current.Y) - padding;
            var width = Math.Max(2, Math.Abs(current.X - start.X) + padding * 2);
            var height = Math.Max(2, Math.Abs(current.Y - start.Y) + padding * 2);
            _ = SetWindowPos(handle, HwndTopmost, left, top, width, height, SwpNoActivate | SwpShowWindow);

            if (_kind != "arrow" || _arrowCanvas is null || _arrowLine is null || _arrowHead is null) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var scaleX = Math.Max(0.1, dpi.DpiScaleX);
            var scaleY = Math.Max(0.1, dpi.DpiScaleY);
            var startX = (start.X - left) / scaleX;
            var startY = (start.Y - top) / scaleY;
            var endX = (current.X - left) / scaleX;
            var endY = (current.Y - top) / scaleY;
            _arrowCanvas.Width = width / scaleX;
            _arrowCanvas.Height = height / scaleY;
            _arrowLine.X1 = startX;
            _arrowLine.Y1 = startY;
            _arrowLine.X2 = endX;
            _arrowLine.Y2 = endY;
            var angle = Math.Atan2(endY - startY, endX - startX);
            const double wingLength = 19;
            _arrowHead.Points =
            [
                new Point(endX + wingLength * Math.Cos(angle + Math.PI * .82), endY + wingLength * Math.Sin(angle + Math.PI * .82)),
                new Point(endX, endY),
                new Point(endX + wingLength * Math.Cos(angle - Math.PI * .82), endY + wingLength * Math.Sin(angle - Math.PI * .82)),
            ];
        }

        private void MakeClickThrough()
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
