using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;

internal static class Program
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;
    private const int VkEscape = 0x1B;
    private const int VkF8 = 0x77;
    private const int VkF9 = 0x78;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkBack = 0x08;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly LowLevelKeyboardProc KeyboardCallback = KeyboardHook;
    private static readonly LowLevelMouseProc MouseCallback = MouseHook;
    private static readonly StringBuilder TextBuffer = new();

    private static IntPtr _keyboardHook;
    private static IntPtr _mouseHook;
    private static bool _running = true;
    private static string _mode = "demo";
    private static DateTime? _lastEventAt;
    private static Context? _textContext;
    private static bool _redactionActive;
    private static bool _leftButtonDown;
    private static bool _dragStarted;
    private static Point _dragOrigin;
    private static Timer? _pendingClick;
    private static Point _pendingClickPoint;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _keyboardHook = SetHook(WhKeyboardLl, KeyboardCallback);
        _mouseHook = SetHook(WhMouseLl, MouseCallback);

        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Console.Error.WriteLine("Could not install the Windows input hooks.");
            Cleanup();
            return 1;
        }

        Emit("mode", new Dictionary<string, object?> { ["mode"] = _mode });
        try
        {
            while (_running && GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            return 0;
        }
        finally
        {
            FlushText();
            Cleanup();
        }
    }

    private static IntPtr SetHook(int hookType, Delegate callback)
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        return SetWindowsHookEx(hookType, callback, moduleHandle, 0);
    }

    private static IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_running) return CallNextHookEx(_mouseHook, code, wParam, lParam);
        var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        var point = new Point(mouse.pt.x, mouse.pt.y);
        var message = wParam.ToInt32();

        try
        {
            switch (message)
            {
                case WmLButtonDown:
                    FlushText();
                    _leftButtonDown = true;
                    _dragStarted = false;
                    _dragOrigin = point;
                    break;
                case WmMouseMove when _leftButtonDown && !_dragStarted && Distance(_dragOrigin, point) >= 5:
                    _dragStarted = true;
                    EmitMouse("dragstart", _dragOrigin);
                    break;
                case WmLButtonUp:
                    _leftButtonDown = false;
                    if (_dragStarted)
                    {
                        EmitMouse("dragend", point);
                    }
                    else
                    {
                        QueueClick(point);
                    }
                    break;
                case WmLButtonDblClk:
                    CancelQueuedClick();
                    EmitMouse("dblclick", point);
                    break;
                case WmRButtonDown:
                    FlushText();
                    EmitMouse("rightclick", point);
                    break;
                case WmMouseWheel:
                    FlushText();
                    EmitMouse("wheel", point, new Dictionary<string, object?>
                    {
                        ["delta"] = unchecked((short)((mouse.mouseData >> 16) & 0xffff)),
                    });
                    break;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"mouse-hook error: {error.Message}");
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_running || (wParam.ToInt32() != WmKeyDown && wParam.ToInt32() != WmSysKeyDown))
        {
            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var key = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        try
        {
            if (key.vkCode == VkEscape)
            {
                FlushText();
                _running = false;
                PostQuitMessage(0);
                return new IntPtr(1);
            }
            if (key.vkCode == VkF8)
            {
                SwitchMode("point");
                return new IntPtr(1);
            }
            if (key.vkCode == VkF9)
            {
                SwitchMode("demo");
                return new IntPtr(1);
            }
            if (IsHotkey(key.vkCode))
            {
                FlushText();
                Emit("hotkey", ContextAtFocusedElement().ToFields(new Dictionary<string, object?>
                {
                    ["keys"] = HotkeyName(key.vkCode),
                }));
                return CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }
            CaptureText(key.vkCode, key.scanCode);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"keyboard-hook error: {error.Message}");
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static void QueueClick(Point point)
    {
        CancelQueuedClick();
        _pendingClickPoint = point;
        _pendingClick = new Timer(_ =>
        {
            lock (Gate)
            {
                _pendingClick?.Dispose();
                _pendingClick = null;
                EmitMouse("click", _pendingClickPoint);
            }
        }, null, GetDoubleClickTime(), Timeout.Infinite);
    }

    private static void CancelQueuedClick()
    {
        _pendingClick?.Dispose();
        _pendingClick = null;
    }

    private static void EmitMouse(string type, Point point, Dictionary<string, object?>? extra = null)
    {
        var fields = ContextAtPoint(point).ToFields(extra);
        fields["x"] = (int)Math.Round(point.X);
        fields["y"] = (int)Math.Round(point.Y);
        Emit(type, fields);
    }

    private static void CaptureText(uint virtualKey, uint scanCode)
    {
        var context = ContextAtFocusedElement();
        if (_textContext is not null && !_textContext.SameTargetAs(context)) FlushText();

        _textContext = context;
        if (context.IsPassword)
        {
            if (!_redactionActive)
            {
                _redactionActive = true;
                Emit("redacted", context.ToFields());
            }
            return;
        }
        _redactionActive = false;

        if (virtualKey == VkBack)
        {
            if (TextBuffer.Length > 0) TextBuffer.Length -= 1;
            return;
        }
        var text = TranslateKey(virtualKey, scanCode);
        if (!string.IsNullOrEmpty(text)) TextBuffer.Append(text);
    }

    private static void FlushText()
    {
        lock (Gate)
        {
            if (TextBuffer.Length == 0 || _textContext is null) return;
            Emit("text", _textContext.ToFields(new Dictionary<string, object?> { ["value"] = TextBuffer.ToString() }));
            TextBuffer.Clear();
        }
    }

    private static string TranslateKey(uint virtualKey, uint scanCode)
    {
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState)) return string.Empty;
        var output = new StringBuilder(8);
        var result = ToUnicodeEx(virtualKey, scanCode, keyboardState, output, output.Capacity, 0, GetKeyboardLayout(0));
        return result > 0 ? output.ToString(0, result) : string.Empty;
    }

    private static bool IsHotkey(uint virtualKey) =>
        (GetAsyncKeyState(VkControl) & 0x8000) != 0 ||
        (GetAsyncKeyState(VkShift) & 0x8000) != 0 ||
        (GetAsyncKeyState(VkMenu) & 0x8000) != 0;

    private static string HotkeyName(uint virtualKey)
    {
        var modifiers = new List<string>();
        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0) modifiers.Add("ctrl");
        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0) modifiers.Add("shift");
        if ((GetAsyncKeyState(VkMenu) & 0x8000) != 0) modifiers.Add("alt");
        modifiers.Add(((char)virtualKey).ToString().ToLowerInvariant());
        return string.Join('+', modifiers);
    }

    private static void SwitchMode(string mode)
    {
        FlushText();
        _mode = mode;
        Emit("mode", new Dictionary<string, object?> { ["mode"] = mode });
    }

    private static void Emit(string type, Dictionary<string, object?> fields)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (_lastEventAt is { } prior)
            {
                var pause = now - prior;
                if (pause.TotalSeconds > 3)
                {
                    WriteEvent(new Dictionary<string, object?>
                    {
                        ["t"] = now.ToString("O"),
                        ["type"] = "wait",
                        ["seconds"] = Math.Round(pause.TotalSeconds, 3),
                        ["mode"] = _mode,
                    });
                }
            }

            fields["t"] = now.ToString("O");
            fields["type"] = type;
            fields["mode"] = _mode;
            WriteEvent(fields);
            _lastEventAt = now;
        }
    }

    private static void WriteEvent(Dictionary<string, object?> value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        Console.Out.Flush();
    }

    private static Context ContextAtPoint(Point point)
    {
        try { return Context.FromElement(AutomationElement.FromPoint(point), WindowTitleAt(point)); }
        catch { return Context.Unknown(WindowTitleAt(point)); }
    }

    private static Context ContextAtFocusedElement()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            return Context.FromElement(element, WindowTitleAtForegroundWindow());
        }
        catch { return Context.Unknown(WindowTitleAtForegroundWindow()); }
    }

    private static string? WindowTitleAt(Point point)
    {
        var handle = GetAncestor(WindowFromPoint(new NativePoint((int)point.X, (int)point.Y)), 2);
        return WindowText(handle);
    }

    private static string? WindowTitleAtForegroundWindow() => WindowText(GetAncestor(GetForegroundWindow(), 2));

    private static string? WindowText(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var length = GetWindowTextLength(handle);
        if (length <= 0) return null;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static double Distance(Point first, Point second) => Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static void Cleanup()
    {
        CancelQueuedClick();
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    private sealed record Context(string? App, int? Pid, string? Window, string? ControlType, string? Name, string? AutomationId, int[]? Rect, bool IsPassword)
    {
        public static Context FromElement(AutomationElement element, string? window)
        {
            var current = element.Current;
            string? app = null;
            try { app = Process.GetProcessById(current.ProcessId).ProcessName + ".exe"; } catch { }
            var rectangle = current.BoundingRectangle;
            return new Context(
                app,
                current.ProcessId,
                window,
                current.ControlType?.ProgrammaticName,
                current.Name,
                current.AutomationId,
                rectangle.IsEmpty ? null : [(int)rectangle.Left, (int)rectangle.Top, (int)rectangle.Right, (int)rectangle.Bottom],
                current.IsPassword);
        }

        public static Context Unknown(string? window) => new(null, null, window, null, null, null, null, false);

        public bool SameTargetAs(Context other)
        {
            var sameRect = (Rect is null && other.Rect is null) || (Rect?.SequenceEqual(other.Rect ?? []) ?? false);
            return Pid == other.Pid && AutomationId == other.AutomationId && Name == other.Name && ControlType == other.ControlType && sameRect;
        }

        public Dictionary<string, object?> ToFields(Dictionary<string, object?>? extra = null)
        {
            var fields = extra is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(extra);
            fields["app"] = App;
            fields["pid"] = Pid;
            fields["window"] = Window;
            fields["controlType"] = ControlType;
            fields["name"] = Name;
            fields["automationId"] = AutomationId;
            fields["rect"] = Rect;
            return fields;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int x; public int y; public NativePoint(int x, int y) { this.x = x; this.y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct { public NativePoint pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public NativePoint pt; }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, Delegate callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] state, StringBuilder buffer, int bufferCapacity, uint flags, IntPtr keyboardLayout);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
