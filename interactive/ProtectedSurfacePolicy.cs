using System.Diagnostics;
using System.IO;

namespace AshaLive;

internal sealed record DesktopSurfaceIdentity(
    int ProcessId,
    long WindowId,
    string ProcessName,
    string WindowTitle,
    string WindowClass);

internal sealed record ProtectedActionAuthorization(
    ProtectedSurfacePolicy.DesktopActionPermit? Permit,
    string? DenialReason,
    string? HumanMessage)
{
    public bool Allowed => Permit is not null;
}

internal sealed class DesktopActionAuthorizationException : InvalidOperationException
{
    public DesktopActionAuthorizationException(string message) : base(message) { }
}

/// <summary>
/// Immutable, model-independent protection for ASHA's human control plane.
/// It issues a short-lived permit only for one exact action whose live target
/// surfaces are known and are outside ASHA's process.
/// </summary>
internal sealed class ProtectedSurfacePolicy
{
    private static readonly TimeSpan PermitLifetime = TimeSpan.FromSeconds(5);
    private readonly Guid _issuerId = Guid.NewGuid();
    private readonly int _protectedProcessId;
    private readonly string _protectedProcessName;
    private readonly TimeSpan _permitLifetime;

    public ProtectedSurfacePolicy(
        int? protectedProcessId = null,
        string? protectedProcessName = null,
        TimeSpan? permitLifetime = null)
    {
        _protectedProcessId = protectedProcessId ?? Environment.ProcessId;
        _protectedProcessName = NormalizeProcessName(
            protectedProcessName ?? Process.GetCurrentProcess().ProcessName);
        _permitLifetime = permitLifetime ?? PermitLifetime;
    }

    public int ProtectedProcessId => _protectedProcessId;

    public bool IsProtectedProcess(int processId) =>
        processId > 0 && processId == _protectedProcessId;

    public bool IsProtectedSurface(DesktopSurfaceIdentity surface) =>
        IsProtectedProcess(surface.ProcessId) ||
        (surface.ProcessId <= 0 &&
         NormalizeProcessName(surface.ProcessName) == _protectedProcessName);

    public ProtectedActionAuthorization Authorize(
        DesktopAction action,
        IEnumerable<DesktopSurfaceIdentity> liveSurfaces)
    {
        var surfaces = liveSurfaces
            .Distinct()
            .ToArray();
        if (surfaces.Length == 0 || surfaces.Any(surface => !IsVerifiedSurface(surface)))
            return Denied(
                "target_not_verified",
                "I couldn't verify the live target surface, so I didn't send input.");
        if (surfaces.Any(IsProtectedSurface))
            return Denied(
                "protected_self_surface",
                "I can't operate my own protected interface. You can still use it directly.");

        return new ProtectedActionAuthorization(
            new DesktopActionPermit(
                _issuerId,
                Guid.NewGuid(),
                action,
                surfaces,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow + _permitLifetime),
            null,
            null);
    }

    public bool TryValidatePermit(
        DesktopActionPermit? permit,
        DesktopAction action,
        out string error)
    {
        if (permit is null ||
            permit.IssuerId != _issuerId ||
            permit.ExpiresAtUtc < DateTimeOffset.UtcNow ||
            permit.Action != action ||
            permit.Surfaces.Count == 0 ||
            permit.Surfaces.Any(surface => !IsVerifiedSurface(surface)) ||
            permit.Surfaces.Any(IsProtectedSurface))
        {
            error = "The desktop-action authorization is missing, expired, changed, or protected.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public bool TryValidateCurrentSurface(
        DesktopActionPermit? permit,
        DesktopAction action,
        DesktopSurfaceIdentity? currentSurface,
        out string error)
    {
        if (!TryValidatePermit(permit, action, out error)) return false;
        if (currentSurface is null || IsProtectedSurface(currentSurface))
        {
            error = "The focused surface is unavailable or belongs to ASHA's protected interface.";
            return false;
        }
        if (!permit!.Surfaces.Any(permitted => SameWindow(permitted, currentSurface)))
        {
            error = "The focused window changed after the action was authorized.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    internal static bool SameWindowForTesting(
        DesktopSurfaceIdentity left,
        DesktopSurfaceIdentity right) =>
        SameWindow(left, right);

    private static bool SameWindow(
        DesktopSurfaceIdentity left,
        DesktopSurfaceIdentity right)
    {
        if (left.WindowId != 0 && right.WindowId != 0)
            return left.WindowId == right.WindowId &&
                   left.ProcessId == right.ProcessId;
        return left.ProcessId > 0 && left.ProcessId == right.ProcessId;
    }

    private static ProtectedActionAuthorization Denied(string reason, string message) =>
        new(null, reason, message);

    private static bool IsVerifiedSurface(DesktopSurfaceIdentity surface) =>
        surface.ProcessId > 0 && surface.WindowId != 0;

    private static string NormalizeProcessName(string? value) =>
        Path.GetFileNameWithoutExtension(value ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

    internal sealed class DesktopActionPermit
    {
        internal DesktopActionPermit(
            Guid issuerId,
            Guid permitId,
            DesktopAction action,
            IReadOnlyList<DesktopSurfaceIdentity> surfaces,
            DateTimeOffset issuedAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            IssuerId = issuerId;
            PermitId = permitId;
            Action = action;
            Surfaces = surfaces;
            IssuedAtUtc = issuedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        internal Guid IssuerId { get; }
        public Guid PermitId { get; }
        public DesktopAction Action { get; }
        public IReadOnlyList<DesktopSurfaceIdentity> Surfaces { get; }
        public DateTimeOffset IssuedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }
}
