namespace AshaLive;

[Flags]
public enum ComputerControlCapability
{
    None = 0,
    OpenApplicationsAndFolders = 1 << 0,
    KeyboardInteraction = 1 << 1,
    VirtualCursor = 1 << 2,
    VirtualCursorInteraction = 1 << 3,
    PhysicalCursor = 1 << 4,
}

public enum VirtualCursorBehaviour
{
    Interact,
    DemonstrateOnly,
}

/// <summary>
/// Persistent upper bounds for ASHA computer control. These settings never
/// activate control by themselves; a short-lived session lease is required as
/// well.
/// </summary>
public sealed class ComputerControlPolicy
{
    public bool AllowApplicationAndFolderOpening { get; set; }
    public bool AllowKeyboardInteraction { get; set; }
    public bool EnableVirtualCursor { get; set; }
    public VirtualCursorBehaviour VirtualCursorBehaviour { get; set; } = VirtualCursorBehaviour.Interact;
    public bool ShowVirtualCursor { get; set; } = true;
    public bool AllowPhysicalCursor { get; set; }
    public bool AskBeforePhysicalFallback { get; set; } = true;

    public ComputerControlCapability AllowedCapabilities
    {
        get
        {
            var capabilities = ComputerControlCapability.None;
            if (AllowApplicationAndFolderOpening)
                capabilities |= ComputerControlCapability.OpenApplicationsAndFolders;
            if (AllowKeyboardInteraction)
                capabilities |= ComputerControlCapability.KeyboardInteraction;
            if (EnableVirtualCursor)
            {
                capabilities |= ComputerControlCapability.VirtualCursor;
                if (VirtualCursorBehaviour == VirtualCursorBehaviour.Interact)
                    capabilities |= ComputerControlCapability.VirtualCursorInteraction;
            }
            if (AllowPhysicalCursor)
                capabilities |= ComputerControlCapability.PhysicalCursor;
            return capabilities;
        }
    }

    public void Normalize()
    {
        // An invisible demonstrator cursor cannot demonstrate anything. Keep
        // the previous visible default rather than producing a meaningless
        // enabled-but-inert state.
        if (EnableVirtualCursor &&
            VirtualCursorBehaviour == VirtualCursorBehaviour.DemonstrateOnly)
            ShowVirtualCursor = true;
    }
}

/// <summary>
/// A process-local activation for one retained ASHA session. The lease takes a
/// snapshot of the capabilities that were allowed when it started; enabling a
/// new global permission never silently expands an existing lease.
/// </summary>
public sealed record ComputerControlLease(
    string Id,
    string SessionId,
    DateTimeOffset StartedAtUtc,
    ComputerControlCapability GrantedCapabilities)
{
    public static bool TryStart(
        ComputerControlPolicy policy,
        string? sessionId,
        out ComputerControlLease? lease,
        out string reason)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            reason = "Start a shared-attention session before starting computer control.";
            return false;
        }

        var granted = policy.AllowedCapabilities;
        if (granted == ComputerControlCapability.None)
        {
            reason = "Choose at least one computer-control capability in Settings first.";
            return false;
        }

        lease = new ComputerControlLease(
            $"control-{Guid.NewGuid():N}",
            sessionId,
            DateTimeOffset.UtcNow,
            granted);
        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// The effective state at the moment of an action. Turning a persistent
/// permission off revokes it immediately because the live result is the
/// intersection of policy and lease. Turning one on requires a new lease.
/// </summary>
public readonly record struct ComputerControlAccess(
    ComputerControlPolicy Policy,
    ComputerControlLease? Lease,
    string? ActiveSessionId,
    ComputerControlRuntimeStatus? RuntimeStatus = null,
    string? RuntimeBlockReason = null)
{
    public bool HasLeaseForCurrentSession =>
        Lease is not null &&
        !string.IsNullOrWhiteSpace(ActiveSessionId) &&
        string.Equals(Lease.SessionId, ActiveSessionId, StringComparison.Ordinal);

    public ComputerControlCapability EffectiveCapabilities =>
        string.IsNullOrWhiteSpace(RuntimeBlockReason) && HasLeaseForCurrentSession
            ? Lease!.GrantedCapabilities & Policy.AllowedCapabilities
            : ComputerControlCapability.None;

    public bool IsLeaseActive => EffectiveCapabilities != ComputerControlCapability.None;
    public bool CanOpenApplicationsAndFolders =>
        Allows(ComputerControlCapability.OpenApplicationsAndFolders);
    public bool CanUseKeyboard =>
        Allows(ComputerControlCapability.KeyboardInteraction);
    public bool CanShowVirtualCursor =>
        Allows(ComputerControlCapability.VirtualCursor) && Policy.ShowVirtualCursor;
    public bool CanInteractWithVirtualCursor =>
        Allows(ComputerControlCapability.VirtualCursorInteraction);
    public bool CanUsePhysicalCursor =>
        Allows(ComputerControlCapability.PhysicalCursor);
    public bool MustAskBeforePhysicalFallback =>
        CanUsePhysicalCursor && Policy.AskBeforePhysicalFallback;
    public ComputerControlCapability NewlyAllowedOutsideLease =>
        HasLeaseForCurrentSession
            ? Policy.AllowedCapabilities & ~Lease!.GrantedCapabilities
            : ComputerControlCapability.None;
    public bool RequiresLeaseRestart => NewlyAllowedOutsideLease != ComputerControlCapability.None;

    public bool Allows(ComputerControlCapability capability) =>
        (EffectiveCapabilities & capability) == capability;

    public bool AllowsCurrentPhysicalExecutorAction(string action) => action switch
    {
        "move" or "click" or "double_click" or "right_click" or "drag" or "scroll" =>
            CanUsePhysicalCursor,
        "type_text" or "key" =>
            CanUseKeyboard,
        _ => false,
    };

    public string DescribeForPerson()
    {
        if (!IsLeaseActive) return "Computer control is disabled.";
        var capabilities = new List<string>();
        if (CanOpenApplicationsAndFolders) capabilities.Add("open applications and ordinary folders, and manage currently open windows");
        if (CanUseKeyboard) capabilities.Add("use approved keyboard input");
        if (CanInteractWithVirtualCursor)
            capabilities.Add(Policy.ShowVirtualCursor ? "use a visible virtual interaction cursor" : "use a hidden virtual interaction cursor");
        else if (CanShowVirtualCursor)
            capabilities.Add("demonstrate with a virtual cursor");
        if (CanUsePhysicalCursor) capabilities.Add("use your physical cursor");
        return $"Active for this session: {string.Join(", ", capabilities)}.";
    }

    public string DescribeForModel()
    {
        if (!string.IsNullOrWhiteSpace(RuntimeBlockReason))
            return $"Current runtime capability state: Computer Control is unavailable because {RuntimeBlockReason} Do not attempt a desktop action or ask the person to change ordinary capability settings. Tell them to return to an unlocked local Windows console. Never claim that an action happened.";

        if (!HasLeaseForCurrentSession)
            return "Current runtime capability state: no Computer Control lease is active for this shared-attention session. If the person asks for a desktop action, explain briefly that they must allow the required capability in Settings and then enable Computer Control. Do not tell them to create a new shared-attention session when one is already active. Never claim that an action happened.";

        if (!IsLeaseActive)
            return "Current runtime capability state: a Computer Control lease exists for this shared-attention session, but none of its originally granted capabilities are currently effective. Tell the person to check Settings, then stop and restart Computer Control. A new shared-attention session is not required. Never claim that an action happened.";

        var enabled = new List<string>();
        if (CanOpenApplicationsAndFolders) enabled.Add("opening installed applications and ordinary folders, and activating, closing, minimizing, maximizing, or restoring currently open windows");
        if (CanUseKeyboard) enabled.Add("approved keyboard input");
        if (CanUsePhysicalCursor) enabled.Add("visible physical pointer input");
        if (CanInteractWithVirtualCursor)
            enabled.Add(RuntimeStatus?.CuaConnected == true
                ? $"CUA Driver background virtual-pointer interaction for clicking, double-clicking, right-clicking, dragging, and scrolling{(Policy.ShowVirtualCursor ? " with a visible agent cursor" : " with its indicator hidden")}"
                : "virtual-cursor interaction is permitted by policy, but CUA Driver is not currently connected; the runtime may still use a narrow accessibility fallback for supported controls");
        else if (CanShowVirtualCursor)
            enabled.Add("visual cursor demonstration only");

        var restart = RequiresLeaseRestart
            ? $" Settings now also allow {NewlyAllowedOutsideLease}, but the active lease does not. Tell the person to stop and restart Computer Control, or simply use Restart computer control; a new shared-attention session is not required."
            : string.Empty;
        return $"Current runtime capability state: a Computer Control lease is ACTIVE for this shared-attention session. Effective capabilities: {string.Join("; ", enabled)}.{restart} Use only a capability explicitly listed here. If the person requests a capability that is not listed, tell them to enable it in Settings and then restart Computer Control; do not ask for a new shared-attention session. Never substitute physical input for virtual control, and never claim success without the tool result and verification.";
    }

    // Compatibility helper retained for callers and tests compiled against the
    // earlier availability flag. New runtime code passes the detailed status.
    public string DescribeForModel(bool virtualInteractionConnected) =>
        (this with
        {
            RuntimeStatus = new ComputerControlRuntimeStatus(
                CuaExecutableAvailable: virtualInteractionConnected,
                CuaDaemonRunning: virtualInteractionConnected),
        }).DescribeForModel();
}
