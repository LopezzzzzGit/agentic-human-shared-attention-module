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
    private readonly AshaPreferences _preferences;
    private readonly DispatcherTimer _tapToListenTimer;
    private readonly DispatcherTimer _conversationBoundaryTimer;
    private readonly DispatcherTimer _cueEditEventTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly string _repositoryRoot;
    private ConversationWindow? _conversationWindow;
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
    private IntPtr _boxDrawHook;
    private LowLevelMouseProc? _boxDrawMouseProc;
    private IntPtr _boxDrawKeyboardHook;
    private LowLevelKeyboardProc? _boxDrawKeyboardProc;
    private bool _boxDrawingArmed;
    private bool _boxDrawingStarted;
    private NativePoint _boxDrawingStart;
    private BoxDrawingPreviewWindow? _boxDrawingPreview;
    private bool _conversationActive;
    private bool _voiceCapturing;
    private bool _voiceTurnInFlight;
    private bool _heardSpeech;
    private int _speechCandidateFrames;
    private double _ambientMicrophoneEnergy = 0.001;
    private DateTime _lastSpeechAtUtc;
    private bool _reallyQuitting;
    private bool _shownTrayHint;
    private bool _controlSessionActive;
    private Process? _teachRecorder;
    private string? _latestRecordingPath;
    private string? _latestCuratedRecordingPath;
    private TeachingReviewWindow? _teachingReviewWindow;
    private string? _activeSessionId;
    private VisualEvidenceBundle? _latestVisionEvidence;
    private bool _shareVisionOnNextTurn;
    private CancellationTokenSource? _voiceTurnCancellation;
    private readonly SemaphoreSlim _sessionWriteGate = new(1, 1);

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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        if (_source is null) return;
        _source.AddHook(WindowMessageHook);
        _markHotkeyRegistered = RegisterHotKey(_source.Handle, MarkHotkeyId, ModControl | ModAlt, VkM);
        RegisterHotKey(_source.Handle, MoveHotkeyId, ModControl | ModAlt | ModShift, VkM);
        RegisterHotKey(_source.Handle, ControlsHotkeyId, ModControl | ModAlt, VkA);
        if (!_markHotkeyRegistered) StatusText.Text = "Ctrl+Alt+M is already in use. Free it to place a visual cue.";
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
        return window;
    }

    private void AttachedChatResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        AttachedChatPanel.Height = Math.Clamp(AttachedChatPanel.Height + e.VerticalChange, 170, 540);
    }

    private void Profile_Click(object sender, RoutedEventArgs e) => TogglePanel(ProfilePanel, SettingsPanel);

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
            if (_teachRecorder is { HasExited: false })
            {
                SessionStatusText.Text = "Finish the teaching recording with Esc before ending this session.";
                return;
            }

            var endingId = _activeSessionId;
            try
            {
                await RecordActiveSessionEventAsync("session.ended", "The person ended this shared-attention session.");
                await _sessionWriteGate.WaitAsync();
                try { await RunAshaAsync("session", "close", endingId); }
                finally { _sessionWriteGate.Release(); }

                ClearActiveSession();
                SessionStatusText.Text = "Session closed. Casual conversation remains available but is no longer retained.";
                StatusText.Text = _voiceSession.IsGroqConfigured ? "Session ended. Tap the orb to talk casually." : "Configure Groq, then restart ASHA.";
                Log("Shared-attention session ended.");
            }
            catch (Exception error)
            {
                SessionStatusText.Text = ShortReason(error);
                Log($"Could not end the shared-attention session: {error.Message}");
            }
            return;
        }

        try
        {
            try { await RunAshaAsync("project", "create", "Personal desktop", "--id", PersonalDesktopProjectId); }
            catch { /* The local default project normally already exists. */ }

            var sessionId = $"desktop-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..31];
            var title = $"Shared attention {DateTime.Now:yyyy-MM-dd HH:mm}";
            await RunAshaAsync("session", "start", "--project", PersonalDesktopProjectId, "--title", title, "--id", sessionId);
            SetActiveSession(sessionId, title);
            await RecordActiveSessionEventAsync("session.started", "The person explicitly started a local shared-attention session.");
            StatusText.Text = "Session is recording locally. Talk, point, or start a teaching demonstration.";
            Log("Shared-attention session started; casual conversation before this point was not captured.");
        }
        catch (Exception error)
        {
            SessionStatusText.Text = ShortReason(error);
            Log($"Could not start the shared-attention session: {error.Message}");
        }
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
            if (_controlSessionActive)
            {
                await RunAshaAsync("clear", ControlPresenceMarkId);
                _controlSessionActive = false;
                ControlSessionButton.Content = "Enable computer control";
                ControlSessionStatusText.Text = "Computer control is disabled.";
                StatusText.Text = _voiceSession.IsGroqConfigured ? "Tap the orb to start listening." : "Configure Groq, then restart ASHA.";
                Log("Computer control session ended.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeSessionId))
            {
                ControlSessionStatusText.Text = "Start a shared-attention session first, so every action is retained locally.";
                return;
            }

            _preferences.Profile = AshaProfile.Assist;
            _preferences.Save();
            ApplyPreferencesToUi();
            if (_preferences.ShowControlPresence)
            {
                await RunAshaAsync("clear", ControlPresenceMarkId);
                var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
                var virtualTop = GetSystemMetrics(SmYVirtualScreen);
                var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
                var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
                var frame = new MarkRequest(
                    ControlPresenceMarkId, "frame", virtualLeft, virtualTop, virtualWidth, virtualHeight,
                    "ASHA is using your desktop", "#766CFF");
                await RunAshaAsync("mark", JsonSerializer.Serialize(frame, JsonOptions));
            }

            _controlSessionActive = true;
            ControlSessionButton.Content = "Stop computer control";
            ControlSessionStatusText.Text = _preferences.ShowControlPresence
                ? "Enabled: ASHA can use visible physical input for a direct request you make. The blue-violet frame means this session is active."
                : "Enabled: ASHA can use visible physical input for a direct request you make. The desktop frame is hidden by your setting.";
            StatusText.Text = "ASHA computer control is enabled for this session.";
            Log("Computer control enabled; every ASHA action remains visible and is written to the session.");
            await RecordTeachingLifecycleAsync("control.session_started", "The person explicitly enabled a visible, human-facing computer-control session.");
        }
        catch (Exception error)
        {
            ControlSessionStatusText.Text = ShortReason(error);
            Log($"Desktop-control session error: {error.Message}");
        }
    }

    private void VisionModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || VisionModeBox.SelectedItem is not ComboBoxItem { Tag: string value } || !Enum.TryParse<VisionPreference>(value, out var mode)) return;
        _preferences.Vision = mode;
        _preferences.Save();
        if (!string.IsNullOrWhiteSpace(_activeSessionId)) _screenObserver.SetMode(mode);
        Log($"Desktop-awareness policy: {VisionDisplayName(mode)}.");
    }

    private void RemoteVisionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _preferences.AllowRemoteVision = RemoteVisionCheckBox.IsChecked == true;
        _preferences.Save();
        Log(_preferences.AllowRemoteVision
            ? "Remote vision is allowed only for an explicit visual request in an active session."
            : "Remote vision is off; selected desktop evidence remains local.");
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
            StatusText.Text = "The current AI cannot receive images. Select Qwen 3.6 before asking ASHA to look.";
            return;
        }

        _shareVisionOnNextTurn = true;
        StatusText.Text = "ASHA will see this selected view with your next spoken turn.";
        Log("One selected desktop view is armed for ASHA's next spoken turn.");
    }

    private void ControlPresenceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _preferences.ShowControlPresence = ControlPresenceCheckBox.IsChecked == true;
        _preferences.Save();
        Log(_preferences.ShowControlPresence
            ? "Desktop-control presence will be visible during physical actions."
            : "Desktop-control presence is hidden by preference.");
    }

    private void ApplyPreferencesToUi()
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
        ControlPresenceCheckBox.IsChecked = _preferences.ShowControlPresence;
        RemoteVisionCheckBox.IsChecked = _preferences.AllowRemoteVision;
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
        AshaProfile.Assist => "Assist is active: ASHA can resolve and propose learned procedures. Physical control still needs an explicit, visible control lease.",
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
        StatusText.Text = "Save your Groq key in the setup window, then restart ASHA.";
    }

    private string DescribeProvider()
    {
        var model = Environment.GetEnvironmentVariable("ASHA_GROQ_MODEL") ?? "llama-3.3-70b-versatile";
        var keyState = _voiceSession.IsGroqConfigured ? "key configured" : "no key configured";
        return $"Groq · {model} · {keyState}";
    }

    private void TapToListenTimer_Tick(object? sender, EventArgs e)
    {
        _tapToListenTimer.Stop();
        if (_voiceCapturing || _voiceTurnInFlight) return;
        _conversationActive = true;
        if (StartVoiceCapture()) AshaEarcons.ConversationStarted();
    }

    private bool StartVoiceCapture()
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
            _conversationActive = false;
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
            if (StartVoiceCapture())
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

    private async Task CompleteVoiceTurnAsync()
    {
        if (!_voiceCapturing || _voiceTurnInFlight) return;
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
            var result = await _voiceSession.RespondAsync(
                wav,
                ResolveVisionForTranscriptAsync,
                ExecuteVisualToolAsync,
                _controlSessionActive,
                _voiceTurnCancellation.Token);
            if (!string.IsNullOrWhiteSpace(result.Transcript)) AddConversation("You", result.Transcript);
            AddConversation("ASHA", result.Reply);

            OrbSurface.SetPresenceState(OrbPresenceState.Speaking);
            StatusText.Text = "ASHA is speaking…";
            await _voiceSession.SpeakAsync(result.Reply, _voiceTurnCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The app is closing or the current turn was deliberately cancelled.
        }
        catch (Exception error)
        {
            StatusText.Text = ShortReason(error);
            Log($"Voice turn error: {error.Message}");
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
            else if (!StatusText.Text.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                     !StatusText.Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                StatusText.Text = _voiceSession.IsGroqConfigured ? "Tap the orb to start listening." : "Configure Groq, then restart ASHA.";
        }
    }

    private async void PlaceCueAtPointer_Click(object sender, RoutedEventArgs e) => await MarkAtCurrentMouseAsync();

    private void DrawBox_Click(object sender, RoutedEventArgs e)
    {
        if (_boxDrawingArmed)
        {
            CancelBoxDrawing("Box drawing cancelled.");
            return;
        }

        _boxDrawMouseProc = BoxDrawingMouseHook;
        _boxDrawHook = SetWindowsHookEx(WhMouseLl, _boxDrawMouseProc, GetModuleHandle(null), 0);
        if (_boxDrawHook == IntPtr.Zero)
        {
            StatusText.Text = "ASHA could not prepare box drawing.";
            Log("Could not install the temporary box-drawing mouse hook.");
            return;
        }

        _boxDrawKeyboardProc = BoxDrawingKeyboardHook;
        _boxDrawKeyboardHook = SetWindowsHookExKeyboard(WhKeyboardLl, _boxDrawKeyboardProc, GetModuleHandle(null), 0);

        _boxDrawingArmed = true;
        _boxDrawingStarted = false;
        DrawBoxButton.Content = "Cancel drawing";
        StatusText.Text = "Draw box is ready. Press, drag, and release anywhere on the desktop. Press Escape to cancel.";
        Log("Box drawing armed; the next drag is reserved for an ASHA visual cue.");
    }

    private IntPtr BoxDrawingMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_boxDrawingArmed) return CallNextHookEx(_boxDrawHook, code, wParam, lParam);
        var mouse = Marshal.PtrToStructure<LowLevelMouseData>(lParam);
        var message = unchecked((int)wParam.ToInt64());

        if (message == WmLButtonDown && !_boxDrawingStarted)
        {
            _boxDrawingStarted = true;
            _boxDrawingStart = mouse.Point;
            Dispatcher.BeginInvoke(() =>
            {
                _boxDrawingPreview ??= new BoxDrawingPreviewWindow();
                _boxDrawingPreview.UpdateBounds(_boxDrawingStart, _boxDrawingStart);
                StatusText.Text = "Drawing box… release when the important area is framed.";
            });
            return new IntPtr(1);
        }

        if (message == WmMouseMove && _boxDrawingStarted)
        {
            var start = _boxDrawingStart;
            Dispatcher.BeginInvoke(() => _boxDrawingPreview?.UpdateBounds(start, mouse.Point));
            return new IntPtr(1);
        }

        if (message == WmLButtonUp && _boxDrawingStarted)
        {
            var start = _boxDrawingStart;
            StopBoxDrawingHook();
            Dispatcher.BeginInvoke(async () => await FinishDrawnBoxAsync(start, mouse.Point));
            return new IntPtr(1);
        }

        return CallNextHookEx(_boxDrawHook, code, wParam, lParam);
    }

    private IntPtr BoxDrawingKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _boxDrawingArmed && unchecked((int)wParam.ToInt64()) == WmKeyDown)
        {
            var key = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
            if (key.VirtualKeyCode == VkEscape)
            {
                Dispatcher.BeginInvoke(() => CancelBoxDrawing("Box drawing cancelled."));
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_boxDrawKeyboardHook, code, wParam, lParam);
    }

    private void CancelBoxDrawing(string status)
    {
        StopBoxDrawingHook();
        _boxDrawingPreview?.Close();
        _boxDrawingPreview = null;
        StatusText.Text = status;
    }

    private void StopBoxDrawingHook()
    {
        _boxDrawingArmed = false;
        _boxDrawingStarted = false;
        if (_boxDrawHook != IntPtr.Zero) _ = UnhookWindowsHookEx(_boxDrawHook);
        _boxDrawHook = IntPtr.Zero;
        _boxDrawMouseProc = null;
        if (_boxDrawKeyboardHook != IntPtr.Zero) _ = UnhookWindowsHookEx(_boxDrawKeyboardHook);
        _boxDrawKeyboardHook = IntPtr.Zero;
        _boxDrawKeyboardProc = null;
        Dispatcher.BeginInvoke(() => DrawBoxButton.Content = "Draw box");
    }

    private async Task FinishDrawnBoxAsync(NativePoint start, NativePoint end)
    {
        _boxDrawingPreview?.Close();
        _boxDrawingPreview = null;
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        if (width < 12 || height < 12)
        {
            StatusText.Text = "Box ignored because it was too small. Drag a visible area.";
            return;
        }

        var center = new NativePoint { X = Math.Min(start.X, end.X) + width / 2, Y = Math.Min(start.Y, end.Y) + height / 2 };
        await CreateVisualCueAsync("box", center, width, height);
    }

    private async Task MarkAtCurrentMouseAsync()
    {
        if (!GetCursorPos(out var point))
        {
            StatusText.Text = "Windows could not read the current pointer position.";
            return;
        }

        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "dot";
        var width = kind == "arrow" ? ReadVectorDimension(WidthBox, 220) : ReadDimension(WidthBox, 220);
        var height = kind == "arrow" ? ReadVectorDimension(HeightBox, 100) : ReadDimension(HeightBox, 100);
        await CreateVisualCueAsync(kind, point, kind is "box" or "arrow" ? width : null, kind is "box" or "arrow" ? height : null);
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
        KindBox.SelectedItem = KindBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), mark.Kind, StringComparison.OrdinalIgnoreCase));
        LabelBox.Text = mark.Label ?? string.Empty;
        WidthBox.Text = (mark.W ?? DefaultWidth(mark.Kind)).ToString();
        HeightBox.Text = (mark.H ?? DefaultHeight(mark.Kind)).ToString();
        XBox.Text = mark.X.ToString();
        YBox.Text = mark.Y.ToString();
        ColorBox.Text = mark.Color;
    }

    private async void LabelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CommitSelectedCueLabelAsync(LabelBox.Text);
    }

    private async void LabelBox_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await CommitSelectedCueLabelAsync(LabelBox.Text);
    }

    private async Task CommitSelectedCueLabelAsync(string rawLabel)
    {
        if (_labelCommitInProgress || MarkList.SelectedItem is not LiveMark mark) return;
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
            _controlSessionActive = false;
            ControlSessionButton.Content = "Enable computer control";
            ControlSessionStatusText.Text = "Computer control is disabled.";
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
        StopBoxDrawingHook();
        _boxDrawingPreview?.Close();
        _conversationActive = false;
        _voiceTurnCancellation?.Cancel();
        _microphone.Dispose();
        _voiceSession.Dispose();
        _screenObserver.Dispose();
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
        _preferences.ActiveSessionId = sessionId;
        if (save) _preferences.Save();
        _screenObserver.Start(_preferences.Vision);
        SessionButton.Content = "End session";
        SessionStatusText.Text = $"Recording locally: {title}. Conversation and teaching from this point are kept together.";
    }

    private void ClearActiveSession()
    {
        _screenObserver.Stop();
        _latestVisionEvidence = null;
        _activeSessionId = null;
        _preferences.ActiveSessionId = null;
        _preferences.Save();
        SessionButton.Content = "Start session";
        SessionStatusText.Text = "No durable session is active.";
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

    private async Task<VisualEvidenceBundle?> CaptureVisionEvidenceAsync(string reason, int anchorX, int anchorY, SurfaceTarget? surface)
    {
        var sessionId = _activeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || _preferences.Vision == VisionPreference.Off) return null;

        try
        {
            StatusText.Text = "Saving local visual evidence…";
            var bundle = await _screenObserver.PreserveEvidenceAsync(sessionId, reason, anchorX, anchorY);
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

    private async Task<VisionAttachment?> ResolveVisionForTranscriptAsync(string transcript, CancellationToken cancellationToken)
    {
        if (!_shareVisionOnNextTurn) return null;
        _shareVisionOnNextTurn = false;

        var evidence = _latestVisionEvidence;
        if (evidence is null || string.IsNullOrWhiteSpace(_activeSessionId) || !_preferences.AllowRemoteVision || !_voiceSession.SupportsVision)
            return null;

        var attachment = LoadVisionAttachment(evidence);
        if (attachment is null)
        {
            StatusText.Text = "ASHA could not read the selected view.";
            return null;
        }

        await RecordActiveSessionEventAsync(
            "vision.shared_with_provider",
            "The person selected one current desktop view for ASHA's next spoken turn.",
            "system",
            "selected_voice_turn_awareness",
            new { app = "desktop", label = "person-selected view", control = "one-shot visual share", x = evidence.ContextX, y = evidence.ContextY, w = evidence.ContextWidth, h = evidence.ContextHeight },
            EvidencePayload(evidence));
        StatusText.Text = "ASHA is looking at the selected view…";
        return attachment;
    }

    private static VisionAttachment? LoadVisionAttachment(VisualEvidenceBundle evidence)
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
            evidence.ContextHeight);
    }

    private async Task<string> ExecuteVisualToolAsync(AshaVisualToolCall call, VisionAttachment vision, CancellationToken cancellationToken)
    {
        return await Dispatcher.InvokeAsync(() => ExecuteVisualToolOnUiAsync(call, vision, cancellationToken)).Task.Unwrap();
    }

    private async Task<string> ExecuteVisualToolOnUiAsync(AshaVisualToolCall call, VisionAttachment vision, CancellationToken cancellationToken)
    {
        if (string.Equals(call.Name, "asha_desktop_action", StringComparison.Ordinal))
            return await ExecuteDesktopActionOnUiAsync(call, vision, cancellationToken);
        if (!string.Equals(call.Name, "asha_mark", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { ok = false, error = "Only the safe asha_mark visual-guidance tool is available." });
        if (string.IsNullOrWhiteSpace(_activeSessionId) || !vision.HasDesktopMapping)
            return JsonSerializer.Serialize(new { ok = false, error = "An active session and coordinate-mapped visual evidence are required." });
        if (!TryReadToolString(call.Arguments, "kind", out var kind) || !new[] { "dot", "circle", "box", "arrow", "label" }.Contains(kind, StringComparer.Ordinal))
            return JsonSerializer.Serialize(new { ok = false, error = "Choose dot, circle, box, arrow, or label." });
        if (!TryReadToolCoordinate(call.Arguments, "x", out var x) || !TryReadToolCoordinate(call.Arguments, "y", out var y))
            return JsonSerializer.Serialize(new { ok = false, error = "Visual guidance requires finite desktop x and y coordinates." });
        var contextX = vision.ContextX!.Value;
        var contextY = vision.ContextY!.Value;
        var contextWidth = vision.ContextWidth!.Value;
        var contextHeight = vision.ContextHeight!.Value;
        if (x < contextX || x > contextX + contextWidth || y < contextY || y > contextY + contextHeight)
            return JsonSerializer.Serialize(new { ok = false, error = "The mark must remain inside the supplied visual-evidence crop." });

        int? width = null;
        int? height = null;
        if (kind == "box")
        {
            if (!TryReadToolDimension(call.Arguments, "w", 12, 1_200, out width) || !TryReadToolDimension(call.Arguments, "h", 12, 1_200, out height))
                return JsonSerializer.Serialize(new { ok = false, error = "A box needs a width and height between 12 and 1200 pixels." });
        }
        else if (kind == "arrow")
        {
            if (!TryReadToolSignedDimension(call.Arguments, "w", -1_200, 1_200, out width) || !TryReadToolSignedDimension(call.Arguments, "h", -1_200, 1_200, out height) || (width == 0 && height == 0))
                return JsonSerializer.Serialize(new { ok = false, error = "An arrow needs a non-zero horizontal or vertical vector." });
        }

        var label = TryReadToolString(call.Arguments, "label", out var suppliedLabel) && suppliedLabel.Length <= 80 ? suppliedLabel : null;
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
            new { app = "desktop", label, control = kind, x, y, w = width, h = height },
            cue: CuePayload(guidanceCue));
        Log($"ASHA visual guidance: {kind} at {x}, {y}.");
        return JsonSerializer.Serialize(new { ok = true, id = markId, kind, x, y, label, action = "visual overlay shown; no mouse or keyboard input occurred" });
    }

    private async Task<string> ExecuteDesktopActionOnUiAsync(AshaVisualToolCall call, VisionAttachment vision, CancellationToken cancellationToken)
    {
        if (!_controlSessionActive)
            return JsonSerializer.Serialize(new { ok = false, error = "Computer control is disabled. Ask the person to enable the explicit control session." });
        if (string.IsNullOrWhiteSpace(_activeSessionId) || !vision.HasDesktopMapping)
            return JsonSerializer.Serialize(new { ok = false, error = "A coordinate-mapped image from an active session is required before physical input." });
        if (!TryReadToolString(call.Arguments, "action", out var action))
            return JsonSerializer.Serialize(new { ok = false, error = "A desktop action is required." });

        DesktopAction input;
        string visibleLabel;
        switch (action)
        {
            case "click":
            case "double_click":
            case "right_click":
                if (!TryReadVisiblePoint(call.Arguments, "x", "y", vision, out var x, out var y))
                    return JsonSerializer.Serialize(new { ok = false, error = "The requested target must be inside the supplied desktop image." });
                input = new DesktopAction(action, x, y);
                visibleLabel = action == "click" ? "ASHA clicks" : action == "double_click" ? "ASHA double-clicks" : "ASHA right-clicks";
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

        await ShowTransientControlCueAsync(input, visibleLabel);
        await Task.Delay(180, cancellationToken);
        await DesktopControlExecutor.ExecuteAsync(input, cancellationToken);
        await Task.Delay(180, cancellationToken);

        SurfaceTarget? resultingSurface = null;
        if (GetCursorPos(out var pointer)) resultingSurface = ResolveTopmostSurface(pointer);
        if (resultingSurface is not null)
            _ = CaptureVisionEvidenceAsync($"control action {action}", pointer.X, pointer.Y, resultingSurface);
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
                x = input.X,
                y = input.Y,
                endX = input.EndX,
                endY = input.EndY,
            });
        Log($"{visibleLabel}: physical input sent.");
        StatusText.Text = $"{visibleLabel}. ASHA captured follow-up visual evidence for review.";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            action,
            input = "physical desktop input was sent visibly; ASHA has not claimed the requested result without further verification",
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
        if (!TryReadToolCoordinate(arguments, xName, out x) || !TryReadToolCoordinate(arguments, yName, out y)) return false;
        return x >= vision.ContextX!.Value && x <= vision.ContextX.Value + vision.ContextWidth!.Value &&
               y >= vision.ContextY!.Value && y <= vision.ContextY.Value + vision.ContextHeight!.Value;
    }

    private static bool LooksSensitive(string text) => Regex.IsMatch(
        text,
        @"(password|passwort|token|secret|api[_ -]?key|recoverys*code|one[- ]?times*(?:code|password)|otp)",
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
        if (!arguments.TryGetProperty(name, out var raw) || raw.ValueKind != JsonValueKind.Number || !raw.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
        value = (int)Math.Round(number);
        return true;
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

    private void AddConversation(string speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var message = new ConversationMessage(DateTime.Now, speaker, text);
        _conversationMessages.Add(message);
        Log($"{speaker}: {text}");
        if (!string.IsNullOrWhiteSpace(_activeSessionId))
            _ = PersistConversationAsync(_activeSessionId, message);
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

    private sealed class BoxDrawingPreviewWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly IntPtr HwndTopmost = new(-1);

        public BoxDrawingPreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = 1;
            Height = 1;
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 141, 255)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(22, 76, 141, 255)),
            };
            SourceInitialized += (_, _) => MakeClickThrough();
        }

        public void UpdateBounds(NativePoint start, NativePoint current)
        {
            if (!IsVisible) Show();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var left = Math.Min(start.X, current.X);
            var top = Math.Min(start.Y, current.Y);
            var width = Math.Max(2, Math.Abs(current.X - start.X));
            var height = Math.Max(2, Math.Abs(current.Y - start.Y));
            _ = SetWindowPos(handle, HwndTopmost, left, top, width, height, SwpNoActivate | SwpShowWindow);
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
