using System.Runtime.InteropServices;

namespace AshaLive;

/// <summary>
/// The narrow physical-input adapter used by an explicitly enabled ASHA
/// control session. It intentionally has no policy of its own: the runtime
/// must validate the requested target and permission before calling it.
/// </summary>
internal static class DesktopControlExecutor
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseWheel = 0x0800;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;

    public static async Task ExecuteAsync(DesktopAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (action.Kind)
        {
            case "click":
                Move(action.X!.Value, action.Y!.Value);
                SendMouse(MouseLeftDown | MouseLeftUp);
                break;
            case "double_click":
                Move(action.X!.Value, action.Y!.Value);
                SendMouse(MouseLeftDown | MouseLeftUp);
                await Task.Delay(90, cancellationToken);
                SendMouse(MouseLeftDown | MouseLeftUp);
                break;
            case "right_click":
                Move(action.X!.Value, action.Y!.Value);
                SendMouse(MouseRightDown | MouseRightUp);
                break;
            case "drag":
                Move(action.X!.Value, action.Y!.Value);
                SendMouse(MouseLeftDown);
                await Task.Delay(120, cancellationToken);
                Move(action.EndX!.Value, action.EndY!.Value);
                await Task.Delay(120, cancellationToken);
                SendMouse(MouseLeftUp);
                break;
            case "scroll":
                if (action.X.HasValue && action.Y.HasValue) Move(action.X.Value, action.Y.Value);
                SendMouse(MouseWheel, unchecked((uint)action.Delta!.Value));
                break;
            case "type_text":
                SendUnicode(action.Text!);
                break;
            case "key":
                SendVirtualKey(action.Key!);
                break;
            default:
                throw new InvalidOperationException($"Unsupported desktop action '{action.Kind}'.");
        }
    }

    private static void Move(int x, int y)
    {
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Windows could not move the physical pointer.");
    }

    private static void SendMouse(uint flags, uint data = 0)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } },
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("Windows rejected the physical mouse input.");
    }

    private static void SendUnicode(string text)
    {
        foreach (var character in text)
        {
            var down = new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = KeyUnicode } },
            };
            var up = new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = KeyUnicode | KeyUp } },
            };
            if (SendInput(2, [down, up], Marshal.SizeOf<Input>()) != 2)
                throw new InvalidOperationException("Windows rejected text input.");
        }
    }

    private static void SendVirtualKey(string key)
    {
        var virtualKey = key.ToLowerInvariant() switch
        {
            "enter" => 0x0D,
            "escape" => 0x1B,
            "tab" => 0x09,
            "space" => 0x20,
            "backspace" => 0x08,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ => throw new InvalidOperationException("Only Enter, Escape, Tab, Space, Backspace, and arrow keys are available in the first control release."),
        };
        var down = new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)virtualKey } } };
        var up = new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)virtualKey, Flags = KeyUp } } };
        if (SendInput(2, [down, up], Marshal.SizeOf<Input>()) != 2)
            throw new InvalidOperationException("Windows rejected the keyboard input.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}

internal sealed record DesktopAction(
    string Kind,
    int? X = null,
    int? Y = null,
    int? EndX = null,
    int? EndY = null,
    int? Delta = null,
    string? Text = null,
    string? Key = null);
