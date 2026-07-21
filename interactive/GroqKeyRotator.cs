#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SharedInference;

/// <summary>
/// Dependency-free, process-local Groq key rotation. The class deliberately
/// knows nothing about ASHA's prompts, models, UI, or request payloads, so it
/// can be copied into another .NET project without bringing ASHA with it.
/// </summary>
public sealed class GroqKeyRotator
{
    private const string MultiKeyVariable = "ASHA_GROQ_KEYS";
    private const string LegacySingleKeyVariable = "ASHA_GROQ_API_KEY";
    private static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(1);
    private readonly string[] _keys;
    private readonly DateTimeOffset?[] _cooldownUntil;
    private readonly object _gate = new();
    private int _stickyIndex;

    public GroqKeyRotator(IEnumerable<string> keys)
    {
        _keys = Normalize(keys).ToArray();
        _cooldownUntil = new DateTimeOffset?[_keys.Length];
    }

    public int Count => _keys.Length;
    public bool IsConfigured => Count > 0;

    public static GroqKeyRotator LoadDefault() => new(ResolveConfiguredKeys());

    /// <summary>
    /// Sends one logical request. HTTP 429 and transient transport failures
    /// rotate; every other HTTP response is returned immediately so the caller
    /// can surface its real error. A successful key becomes the sticky start
    /// point for the next logical request.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        Func<string, CancellationToken, Task<HttpResponseMessage>> sendAttempt,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sendAttempt);
        if (!IsConfigured)
            throw new InvalidOperationException("No Groq keys are configured.");
        if (attemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));

        var now = DateTimeOffset.UtcNow;
        var candidates = CandidateIndices(now);
        if (candidates.Count == 0)
            throw CreateRateLimitException(now);

        Exception? lastTransportError = null;
        foreach (var index in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(attemptTimeout);

            HttpResponseMessage response;
            try
            {
                response = await sendAttempt(_keys[index], attemptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                lastTransportError = new TimeoutException("A Groq request attempt timed out.", error);
                continue;
            }
            catch (HttpRequestException error)
            {
                lastTransportError = error;
                continue;
            }
            catch (TimeoutException error)
            {
                lastTransportError = error;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                RememberCooldown(index, response, DateTimeOffset.UtcNow);
                response.Dispose();
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                lock (_gate) _stickyIndex = index;
            }

            // Fail fast for 400/401/403/404/5xx. Repeating a structurally bad
            // request with another credential only wastes quota and time.
            return response;
        }

        if (CoolingKeyCount(DateTimeOffset.UtcNow) >= Count)
            throw CreateRateLimitException(DateTimeOffset.UtcNow);

        throw new GroqKeysUnavailableException(
            "No configured Groq key could reach the service. Please check the network and try again.",
            lastTransportError);
    }

    private IReadOnlyList<int> CandidateIndices(DateTimeOffset now)
    {
        lock (_gate)
        {
            var candidates = new List<int>(_keys.Length);
            for (var offset = 0; offset < _keys.Length; offset++)
            {
                var index = (_stickyIndex + offset) % _keys.Length;
                if (_cooldownUntil[index] is { } until && until > now) continue;
                candidates.Add(index);
            }
            return candidates;
        }
    }

    private int CoolingKeyCount(DateTimeOffset now)
    {
        lock (_gate) return _cooldownUntil.Count(until => until is { } value && value > now);
    }

    private void RememberCooldown(int index, HttpResponseMessage response, DateTimeOffset now)
    {
        var until = ResolveCooldownUntil(response.Headers, now) ?? now + DefaultRateLimitCooldown;
        lock (_gate) _cooldownUntil[index] = until;
    }

    private AllGroqKeysRateLimitedException CreateRateLimitException(DateTimeOffset now)
    {
        DateTimeOffset? earliest;
        lock (_gate)
        {
            earliest = _cooldownUntil
                .Where(value => value is { } until && until > now)
                .Min();
        }
        return new AllGroqKeysRateLimitedException(
            "All configured Groq keys are temporarily rate-limited.",
            earliest);
    }

    private static IEnumerable<string> ResolveConfiguredKeys()
    {
        var fromEnvironment = ParseCommaSeparated(Environment.GetEnvironmentVariable(MultiKeyVariable));
        if (fromEnvironment.Count > 0) return fromEnvironment;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plainTextPath = Path.Combine(userProfile, ".groq", "keys.txt");
        var fromPlainText = ReadPlainText(plainTextPath);
        if (fromPlainText.Count > 0) return fromPlainText;

        var lanternPath = Path.Combine(userProfile, ".lantern", "keys.json");
        var fromLantern = ReadJsonField(lanternPath, MultiKeyVariable);
        if (fromLantern.Count > 0) return fromLantern;

        // Compatibility for existing ASHA installations. New setup writes the
        // multi-key variable, but an already configured single key must not
        // suddenly stop working after this upgrade.
        return ParseCommaSeparated(Environment.GetEnvironmentVariable(LegacySingleKeyVariable));
    }

    private static IReadOnlyList<string> ReadPlainText(string path)
    {
        try { return File.Exists(path) ? ParseCommaSeparated(File.ReadAllText(path)) : []; }
        catch { return []; }
    }

    private static IReadOnlyList<string> ReadJsonField(string path, string field)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(field, out var value)) return [];
            if (value.ValueKind == JsonValueKind.String)
                return ParseCommaSeparated(value.GetString());
            if (value.ValueKind == JsonValueKind.Array)
                return Normalize(value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)).ToArray();
        }
        catch
        {
            // Configuration failures never reveal file content or credentials.
        }
        return [];
    }

    private static IReadOnlyList<string> ParseCommaSeparated(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : Normalize(raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();

    private static IEnumerable<string> Normalize(IEnumerable<string> keys) =>
        keys.Select(key => key.Trim())
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal);

    private static DateTimeOffset? ResolveCooldownUntil(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (headers.RetryAfter?.Date is { } retryDate) return retryDate;
        if (headers.RetryAfter?.Delta is { } retryDelta) return now + retryDelta;

        foreach (var header in new[] { "x-ratelimit-reset-tokens", "x-ratelimit-reset-requests", "x-ratelimit-reset" })
        {
            if (!headers.TryGetValues(header, out var values)) continue;
            foreach (var value in values)
            {
                if (TryParseReset(value, now, out var until)) return until;
            }
        }
        return null;
    }

    private static bool TryParseReset(string raw, DateTimeOffset now, out DateTimeOffset until)
    {
        until = default;
        var value = raw.Trim();
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            until = date;
            return true;
        }

        var duration = Regex.Match(value, @"^(?:(?<h>\d+(?:\.\d+)?)h)?(?:(?<m>\d+(?:\.\d+)?)m)?(?:(?<s>\d+(?:\.\d+)?)s)?$", RegexOptions.IgnoreCase);
        if (duration.Success && duration.Groups.Cast<Group>().Skip(1).Any(group => group.Success))
        {
            static double Number(Group group) => group.Success
                ? double.Parse(group.Value, CultureInfo.InvariantCulture)
                : 0d;
            until = now + TimeSpan.FromHours(Number(duration.Groups["h"]))
                        + TimeSpan.FromMinutes(Number(duration.Groups["m"]))
                        + TimeSpan.FromSeconds(Number(duration.Groups["s"]));
            return true;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        if (number > 1_000_000_000)
            until = DateTimeOffset.FromUnixTimeSeconds((long)number);
        else
            until = now + TimeSpan.FromSeconds(Math.Max(0, number));
        return true;
    }
}

public sealed class AllGroqKeysRateLimitedException : Exception
{
    public AllGroqKeysRateLimitedException(string message, DateTimeOffset? retryAtUtc) : base(message) =>
        RetryAtUtc = retryAtUtc;

    public DateTimeOffset? RetryAtUtc { get; }
}

public sealed class GroqKeysUnavailableException : Exception
{
    public GroqKeysUnavailableException(string message, Exception? innerException) : base(message, innerException) { }
}
