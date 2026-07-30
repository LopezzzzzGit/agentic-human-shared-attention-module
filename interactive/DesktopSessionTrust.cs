namespace AshaLive;

internal enum DesktopSessionTransition
{
    Startup,
    Lock,
    Unlock,
    Logon,
    Logoff,
    ConsoleConnect,
    ConsoleDisconnect,
    RemoteConnect,
    RemoteDisconnect,
}

internal sealed record DesktopSessionTrust(
    bool IsTrustedLocalConsole,
    string Reason,
    DesktopSessionTransition Transition)
{
    public bool CanObserveDesktop => IsTrustedLocalConsole;
    public bool CanStartComputerControl => IsTrustedLocalConsole;
}

/// <summary>
/// Pure policy for Windows desktop-session transitions. Returning to a local
/// console may restore observation, but callers never restore a control lease.
/// </summary>
internal static class DesktopSessionTrustPolicy
{
    public static DesktopSessionTrust Evaluate(
        DesktopSessionTransition transition,
        bool isTerminalServicesSession)
    {
        if (transition is DesktopSessionTransition.Lock)
            return Untrusted(transition, "Windows is locked.");
        if (transition is DesktopSessionTransition.Logoff)
            return Untrusted(transition, "The Windows session is logging off.");
        if (transition is DesktopSessionTransition.ConsoleDisconnect)
            return Untrusted(transition, "The local Windows console disconnected.");
        if (transition is DesktopSessionTransition.RemoteConnect)
            return Untrusted(transition, "A Windows remote desktop session connected.");
        if (transition is DesktopSessionTransition.RemoteDisconnect)
            return Untrusted(
                transition,
                "The remote desktop session disconnected; local presence has not yet been re-established.");

        return isTerminalServicesSession
            ? Untrusted(
                transition,
                "ASHA is running in a Windows remote desktop session.")
            : new DesktopSessionTrust(
                true,
                "The local Windows console is unlocked and interactive.",
                transition);
    }

    private static DesktopSessionTrust Untrusted(
        DesktopSessionTransition transition,
        string reason) =>
        new(false, reason, transition);
}
