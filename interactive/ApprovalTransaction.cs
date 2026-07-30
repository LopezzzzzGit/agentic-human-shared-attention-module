namespace AshaLive;

internal enum ApprovalTransactionState
{
    Pending,
    Approved,
    Declined,
    Cancelled,
    Expired,
    Consumed,
}

internal sealed record ApprovalBinding(
    string Operation,
    DesktopAction Action,
    DesktopSurfaceIdentity Target,
    string SharedAttentionSessionId,
    string ControlLeaseId);

internal sealed record ApprovalTransaction(
    Guid Id,
    ApprovalBinding Binding,
    string Summary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ApprovalTransactionState State);

/// <summary>
/// Process-local, one-active-at-a-time approval transactions. Model tool calls
/// never receive this object or an operation that can mutate it.
/// </summary>
internal sealed class ApprovalTransactionManager
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private ApprovalTransaction? _current;

    public ApprovalTransactionManager(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public ApprovalTransaction Create(
        ApprovalBinding binding,
        string summary,
        TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var now = _utcNow();
        var duration = lifetime ?? DefaultLifetime;
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        lock (_gate)
        {
            if (_current is { State: ApprovalTransactionState.Pending or ApprovalTransactionState.Approved })
                _current = _current with { State = ApprovalTransactionState.Cancelled };
            _current = new ApprovalTransaction(
                Guid.NewGuid(),
                binding,
                SanitizeSummary(summary),
                now,
                now + duration,
                ApprovalTransactionState.Pending);
            return _current;
        }
    }

    public ApprovalTransaction? Current
    {
        get
        {
            lock (_gate)
            {
                ExpireLocked();
                return _current;
            }
        }
    }

    public bool TryApprove(Guid id)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (_current is not { State: ApprovalTransactionState.Pending } current ||
                current.Id != id)
                return false;
            _current = current with { State = ApprovalTransactionState.Approved };
            return true;
        }
    }

    public bool TryDecline(Guid id)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (_current is not { State: ApprovalTransactionState.Pending } current ||
                current.Id != id)
                return false;
            _current = current with { State = ApprovalTransactionState.Declined };
            return true;
        }
    }

    public bool TryConsume(Guid id, ApprovalBinding binding)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (_current is not { State: ApprovalTransactionState.Approved } current ||
                current.Id != id ||
                current.Binding != binding)
                return false;
            _current = current with { State = ApprovalTransactionState.Consumed };
            return true;
        }
    }

    public ApprovalTransaction? CancelAll()
    {
        lock (_gate)
        {
            ExpireLocked();
            if (_current is null) return null;
            if (_current.State is ApprovalTransactionState.Pending or ApprovalTransactionState.Approved)
                _current = _current with { State = ApprovalTransactionState.Cancelled };
            return _current;
        }
    }

    private void ExpireLocked()
    {
        if (_current is { State: ApprovalTransactionState.Pending or ApprovalTransactionState.Approved } current &&
            current.ExpiresAtUtc <= _utcNow())
            _current = current with { State = ApprovalTransactionState.Expired };
    }

    private static string SanitizeSummary(string? value)
    {
        var normalized = string.Join(
            " ",
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length switch
        {
            0 => "Use the physical pointer for this one action.",
            > 160 => normalized[..159] + "…",
            _ => normalized,
        };
    }
}
