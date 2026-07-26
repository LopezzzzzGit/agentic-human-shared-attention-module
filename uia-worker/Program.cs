using System.Text.Json;

namespace AshaLive;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var capture = new DesktopStateCaptureCore();
            object? result = args switch
            {
                ["foreground"] => capture.CaptureForeground(),
                ["foreground", var rawDeniedProcessId]
                    when int.TryParse(rawDeniedProcessId, out var deniedProcessId) =>
                    capture.CaptureForeground(deniedProcessId),
                ["point", var rawX, var rawY]
                    when int.TryParse(rawX, out var x) && int.TryParse(rawY, out var y) =>
                    capture.CaptureAtPoint(x, y),
                ["point", var rawX, var rawY, var rawDeniedProcessId]
                    when int.TryParse(rawX, out var x) &&
                         int.TryParse(rawY, out var y) &&
                         int.TryParse(rawDeniedProcessId, out var deniedProcessId) =>
                    capture.CaptureAtPoint(x, y, deniedProcessId),
                ["act", var rawX, var rawY, var action, var expectedName, var expectedRole, var semanticRole, var rawDeniedProcessId]
                    when int.TryParse(rawX, out var x) &&
                         int.TryParse(rawY, out var y) &&
                         int.TryParse(rawDeniedProcessId, out var deniedProcessId) =>
                    capture.TryExecuteAccessibleAction(
                        x,
                        y,
                        action,
                        expectedName,
                        expectedRole,
                        semanticRole,
                        deniedProcessId),
                _ => throw new ArgumentException("Use foreground or point <x> <y>."),
            };
            Console.Out.Write(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.Write(error.GetType().Name);
            return 1;
        }
    }
}
