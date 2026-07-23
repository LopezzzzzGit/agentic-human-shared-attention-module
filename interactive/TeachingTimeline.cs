using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AshaLive;

/// <summary>
/// A review item keeps the immutable recorder line intact while allowing the
/// person to decide what belongs in a teaching episode. Pointing is evidence;
/// demonstration is a possible procedure action. They are intentionally not
/// the same thing.
/// </summary>
public sealed class TeachingTimelineItem : INotifyPropertyChanged
{
    private bool _include;
    private string _label;

    public TeachingTimelineItem(DateTime timestamp, string source, string kind, string mode, string app, string target, string control, int? x, int? y, int? width, int? height, string summary, string? rawJson, bool include, bool candidateAction)
    {
        Timestamp = timestamp;
        Source = source;
        Kind = kind;
        Mode = mode;
        App = app;
        Target = target;
        Control = control;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Summary = summary;
        RawJson = rawJson;
        _include = include;
        CandidateAction = candidateAction;
        _label = target;
    }

    public DateTime Timestamp { get; }
    public string Source { get; }
    public string Kind { get; }
    public string Mode { get; }
    public string App { get; }
    public string Target { get; }
    public string Control { get; }
    public int? X { get; }
    public int? Y { get; }
    public int? Width { get; }
    public int? Height { get; }
    public string Summary { get; }
    public string? RawJson { get; }
    public bool CandidateAction { get; }
    public bool CanInclude => Source != "conversation";

    public bool Include
    {
        get => _include;
        set
        {
            if (_include == value) return;
            _include = value;
            OnPropertyChanged();
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_label == normalized) return;
            _label = normalized;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public static class TeachingRecording
{
    private static readonly HashSet<string> CandidateActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "click", "dblclick", "rightclick", "dragstart", "dragend", "text", "hotkey",
    };

    public static IReadOnlyList<TeachingTimelineItem> Read(string recordingPath)
    {
        var items = new List<TeachingTimelineItem>();
        foreach (var line in File.ReadLines(recordingPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { items.Add(Parse(line)); }
            catch { /* A malformed raw line stays in the immutable source but does not break review. */ }
        }
        return items;
    }

    public static IEnumerable<object> ToSemanticEvents(IEnumerable<TeachingTimelineItem> items)
    {
        foreach (var item in items.Where(item => item.Source == "recording"))
        {
            yield return new
            {
                actor = "human",
                type = $"teaching.recorded_{item.Kind}",
                intent = string.Equals(item.Mode, "point", StringComparison.OrdinalIgnoreCase) ? "point_context" : "demonstrate_action",
                note = $"Recorded {item.Mode} {item.Kind}{(string.IsNullOrWhiteSpace(item.Target) ? string.Empty : $" on {item.Target}")}. Raw timing and detail remain in the private recording.",
                target = new { app = item.App, label = item.Target, control = item.Control, x = item.X, y = item.Y, w = item.Width, h = item.Height },
            };
        }
    }

    public static async Task<int> SaveCuratedCandidateRecordingAsync(string path, IEnumerable<TeachingTimelineItem> items)
    {
        var candidates = items.Where(item => item.Include && item.CandidateAction && !string.IsNullOrWhiteSpace(item.RawJson))
            .Select(item => item.RawJson!)
            .ToArray();
        await File.WriteAllLinesAsync(path, candidates);
        return candidates.Length;
    }

    public static async Task SaveCurationManifestAsync(string path, string sourceRecordingPath, string sessionId, IEnumerable<TeachingTimelineItem> items)
    {
        var retained = items.Where(item => item.CanInclude && item.Include).Select(item => new
        {
            at = item.Timestamp.ToUniversalTime().ToString("O"),
            mode = item.Mode,
            kind = item.Kind,
            label = item.Label,
            candidateAction = item.CandidateAction,
            target = new { app = item.App, control = item.Control, x = item.X, y = item.Y, w = item.Width, h = item.Height },
        });
        var manifest = new
        {
            createdAt = DateTime.UtcNow.ToString("O"),
            sourceRecording = Path.GetFileName(sourceRecordingPath),
            sessionId,
            retained,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static TeachingTimelineItem Parse(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var kind = ReadString(root, "type") ?? "event";
        var mode = ReadString(root, "mode") ?? "context";
        var timestamp = DateTime.TryParse(ReadString(root, "t"), out var parsed) ? parsed.ToLocalTime() : DateTime.Now;
        var app = ReadString(root, "app") ?? "desktop";
        var control = ReadString(root, "controlType") ?? string.Empty;
        var target = ReadString(root, "name") ?? ReadString(root, "window") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target) && string.Equals(kind, "text", StringComparison.OrdinalIgnoreCase)) target = "typed description";
        var (x, y) = (ReadInt(root, "x"), ReadInt(root, "y"));
        var (width, height) = ReadRectDimensions(root);
        var candidate = string.Equals(mode, "demo", StringComparison.OrdinalIgnoreCase) && CandidateActions.Contains(kind);
        var include = candidate || string.Equals(mode, "point", StringComparison.OrdinalIgnoreCase);
        var targetText = string.IsNullOrWhiteSpace(target) ? "unresolved target" : target;
        var summary = kind switch
        {
            "mode" => $"Switched to {mode} mode",
            "wait" => "Pause between actions",
            _ => $"{ModeDisplay(mode)}: {kind} on {targetText}",
        };
        return new TeachingTimelineItem(timestamp, "recording", kind, mode, app, target, control, x, y, width, height, summary, rawJson, include, candidate);
    }

    private static TeachingTimelineItem Conversation(ConversationMessage message) => new(
        message.Timestamp, "conversation", "message", "conversation", "ASHA", message.Speaker, "conversation", null, null, null, null,
        $"{message.Speaker}: {message.Text}", null, false, false);

    public static IEnumerable<TeachingTimelineItem> ConversationItems(IEnumerable<ConversationMessage> messages) => messages.Select(Conversation);

    public static IEnumerable<TeachingTimelineItem> AttentionItems(JsonElement sessionView)
    {
        if (!sessionView.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) yield break;
        foreach (var raw in events.EnumerateArray())
        {
            var type = ReadString(raw, "type") ?? string.Empty;
            if (type is not "attention.surface_marked" and not "vision.evidence_captured" and not "guidance.visual_mark_shown"
                and not "cue.created" and not "cue.updated" and not "cue.moved" and not "cue.deleted") continue;
            var target = raw.TryGetProperty("target", out var targetValue) && targetValue.ValueKind == JsonValueKind.Object ? targetValue : default;
            var cue = raw.TryGetProperty("cue", out var cueValue) && cueValue.ValueKind == JsonValueKind.Object ? cueValue : default;
            var timestamp = DateTime.TryParse(ReadString(raw, "at"), out var parsed) ? parsed.ToLocalTime() : DateTime.Now;
            var app = target.ValueKind == JsonValueKind.Object ? ReadString(target, "app") ?? "desktop" : "desktop";
            var label = target.ValueKind == JsonValueKind.Object ? ReadString(target, "label") ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(label) && cue.ValueKind == JsonValueKind.Object) label = ReadString(cue, "label") ?? string.Empty;
            var control = target.ValueKind == JsonValueKind.Object ? ReadString(target, "control") ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(control) && cue.ValueKind == JsonValueKind.Object) control = ReadString(cue, "kind") ?? string.Empty;
            var x = target.ValueKind == JsonValueKind.Object ? ReadInt(target, "x") : null;
            var y = target.ValueKind == JsonValueKind.Object ? ReadInt(target, "y") : null;
            var width = target.ValueKind == JsonValueKind.Object ? ReadInt(target, "w") : null;
            var height = target.ValueKind == JsonValueKind.Object ? ReadInt(target, "h") : null;
            x ??= cue.ValueKind == JsonValueKind.Object ? ReadInt(cue, "x") : null;
            y ??= cue.ValueKind == JsonValueKind.Object ? ReadInt(cue, "y") : null;
            width ??= cue.ValueKind == JsonValueKind.Object ? ReadInt(cue, "w") : null;
            height ??= cue.ValueKind == JsonValueKind.Object ? ReadInt(cue, "h") : null;
            var (source, kind, mode, summary) = type switch
            {
                "attention.surface_marked" => ("attention", "visual cue", "point", $"Visual cue: {label}"),
                "vision.evidence_captured" => ("attention", "visual evidence", "point", "Local screenshot evidence captured"),
                "cue.created" => ("attention", "visual cue", "point", $"Created {control} cue: {label}"),
                "cue.updated" => ("attention", "visual cue edit", "point", $"Edited {control} cue: {label}"),
                "cue.moved" => ("attention", "visual cue move", "point", $"Moved {control} cue: {label}"),
                "cue.deleted" => ("attention", "visual cue removal", "point", $"Removed {control} cue: {label}"),
                _ => ("guidance", "ASHA guidance", "assist", $"ASHA showed guidance: {label}"),
            };
            yield return new TeachingTimelineItem(timestamp, source, kind, mode, app, label, control, x, y, width, height, summary, null, true, false);
        }
    }

    private static string ModeDisplay(string mode) => mode switch
    {
        "point" => "Pointing",
        "demo" => "Demonstration",
        _ => "Context",
    };

    private static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? ReadInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static (int? Width, int? Height) ReadRectDimensions(JsonElement root)
    {
        if (!root.TryGetProperty("rect", out var rect) || rect.ValueKind != JsonValueKind.Array || rect.GetArrayLength() < 4) return (null, null);
        var values = rect.EnumerateArray().Take(4).Select(value => value.TryGetInt32(out var number) ? number : 0).ToArray();
        return (Math.Max(0, values[2] - values[0]), Math.Max(0, values[3] - values[1]));
    }
}
