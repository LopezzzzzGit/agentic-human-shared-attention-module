using System.Media;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace AshaLive;

/// <summary>
/// Captures one short voice turn as a 16 kHz mono WAV file. This is the
/// established microphone path used by ASHA before continuous turn-taking.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private readonly object _gate = new();
    private WaveInEvent? _recorder;
    private MemoryStream? _pcm;
    private TaskCompletionSource<byte[]>? _stopped;
    private bool _disposed;

    public event Action<double>? EnergyChanged;
    public bool IsRecording { get; private set; }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MicrophoneCapture));
        if (IsRecording) return;

        var recorder = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16_000, 16, 1),
            BufferMilliseconds = 60,
        };
        recorder.DataAvailable += Recorder_DataAvailable;
        recorder.RecordingStopped += Recorder_RecordingStopped;

        lock (_gate)
        {
            _pcm = new MemoryStream();
            _stopped = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _recorder = recorder;
            IsRecording = true;
        }

        try
        {
            recorder.StartRecording();
        }
        catch
        {
            lock (_gate)
            {
                IsRecording = false;
                _recorder = null;
                _pcm?.Dispose();
                _pcm = null;
                _stopped = null;
            }
            recorder.Dispose();
            throw;
        }
    }

    public async Task<byte[]> StopAsync()
    {
        WaveInEvent? recorder;
        Task<byte[]>? stopped;
        lock (_gate)
        {
            recorder = _recorder;
            stopped = _stopped?.Task;
        }

        if (recorder is null || stopped is null) return [];
        recorder.StopRecording();
        return await stopped.ConfigureAwait(false);
    }

    public void KeepRecentAudio(TimeSpan duration)
    {
        var bytesToKeep = Math.Max(0, (int)Math.Ceiling(16_000 * 2 * duration.TotalSeconds));
        lock (_gate)
        {
            if (_pcm is null || _pcm.Length <= bytesToKeep) return;
            var audio = _pcm.ToArray();
            var start = Math.Max(0, audio.Length - bytesToKeep);
            _pcm.SetLength(0);
            _pcm.Write(audio, start, audio.Length - start);
        }
    }

    private void Recorder_DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            _pcm?.Write(e.Buffer, 0, e.BytesRecorded);
        }
        EnergyChanged?.Invoke(CalculateEnergy(e.Buffer, e.BytesRecorded));
    }

    private void Recorder_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        WaveInEvent? recorder;
        MemoryStream? pcm;
        TaskCompletionSource<byte[]>? stopped;
        lock (_gate)
        {
            recorder = _recorder;
            pcm = _pcm;
            stopped = _stopped;
            _recorder = null;
            _pcm = null;
            _stopped = null;
            IsRecording = false;
        }

        try
        {
            if (e.Exception is not null) throw e.Exception;
            var wav = EncodeWav(pcm?.ToArray() ?? []);
            stopped?.TrySetResult(wav);
        }
        catch (Exception error)
        {
            stopped?.TrySetException(error);
        }
        finally
        {
            pcm?.Dispose();
            recorder?.Dispose();
        }
    }

    private static byte[] EncodeWav(byte[] pcm)
    {
        var normalizedPcm = NormalizeSpeechLevel(pcm);
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(16_000, 16, 1)))
        {
            writer.Write(normalizedPcm, 0, normalizedPcm.Length);
        }
        return stream.ToArray();
    }

    private static byte[] NormalizeSpeechLevel(byte[] pcm)
    {
        if (pcm.Length < 2) return pcm;

        var peak = 0;
        for (var offset = 0; offset + 1 < pcm.Length; offset += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, offset)));

        // Leave silence and already healthy microphone levels untouched. For
        // a quiet microphone, lift the captured utterance before Whisper sees
        // it, while capping gain so room noise is not amplified without bound.
        if (peak < 64 || peak >= 24_000) return pcm;
        var gain = Math.Min(6d, 24_000d / peak);
        if (gain <= 1.05) return pcm;

        var normalized = new byte[pcm.Length];
        for (var offset = 0; offset + 1 < pcm.Length; offset += 2)
        {
            var sample = BitConverter.ToInt16(pcm, offset);
            var lifted = (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue);
            normalized[offset] = (byte)(lifted & 0xff);
            normalized[offset + 1] = (byte)((lifted >> 8) & 0xff);
        }
        return normalized;
    }

    private static double CalculateEnergy(byte[] buffer, int length)
    {
        if (length < 2) return 0;
        double sum = 0;
        var samples = length / 2;
        for (var offset = 0; offset + 1 < length; offset += 2)
        {
            var sample = BitConverter.ToInt16(buffer, offset) / 32768d;
            sum += sample * sample;
        }
        return Math.Clamp(Math.Sqrt(sum / samples) * 4.2, 0, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WaveInEvent? recorder;
        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
            _pcm?.Dispose();
            _pcm = null;
            _stopped?.TrySetCanceled();
            _stopped = null;
            IsRecording = false;
        }
        recorder?.Dispose();
    }
}

/// <summary>
/// ASHA's first local conversation runtime. It keeps a compact in-memory
/// conversation, talks to the local Whisper/Kokoro service, and uses Groq only
/// for inference. The API key never enters the UI or leaves this process.
/// </summary>
public sealed class AshaVoiceSession : IDisposable
{
    private const string SpeechBaseUrl = "http://127.0.0.1:9010";
    private const string GroqBaseUrl = "https://api.groq.com/openai/v1";
    private const string DefaultModel = "llama-3.3-70b-versatile";
    private const string SystemPrompt = """
        You are ASHA — a warm, concise shared-attention companion living on
        the user's desktop. You know that your role is to help a person notice,
        understand, learn, and act at their computer together with you.

        This is normally a live spoken conversation. The person speaks into a
        microphone, ASHA's local speech recognition turns that audio into the
        words you receive, and ASHA's local voice speaks your answer aloud.
        Treat those transcribed words simply as something the person said to
        you. Stay in character as ASHA. In ordinary conversation, answer a
        question such as "Can you hear me?" naturally: "Yes, I can hear you."
        Do not expose speech recognition, transcription, models, providers, or
        other implementation machinery unless the person explicitly asks how
        your hearing works or requests a technical explanation. Then explain
        honestly that local speech recognition mediates the conversation. If
        a phrase is unclear, ask naturally for it to be repeated rather than
        discussing the transcription pipeline.

        Always use human-facing language. Speak naturally, warmly, and in
        complete short sentences; prefer familiar words over engineering
        jargon. Write out units and abbreviations a listener should hear, for
        example "ten kilometers", "thirty seconds", and "twenty percent",
        never "10 km", "30 s", or "20%". Do not use Markdown decoration,
        asterisks, headings, bullet glyphs, code fences, raw JSON, file paths,
        stack traces, or command syntax in a normal answer. Do not read code or
        punctuation aloud. If technical detail is genuinely needed, explain it
        in ordinary language first and offer to show the detail in the chat.

        Keep most answers under four short sentences. You can explain, guide,
        and ask clarifying questions, but never claim to have clicked, moved,
        seen, or changed something unless the ASHA runtime explicitly tells
        you that happened.

        If the runtime supplies an image, it is one deliberately selected,
        recent piece of local desktop evidence — not a live feed and not proof
        of anything beyond what is visible in that image. Treat it as your
        current shared visual context: use it to answer the person directly,
        mention the visible application or target when helpful, and say when
        you are uncertain. Do not say that you cannot see the screen when an
        image is supplied.

        When the runtime gives you the safe asha_mark tool together with a
        coordinate-mapped image, you may use it once to point out a visible
        target. It creates a visual overlay only; it never moves the person's
        mouse, clicks, types, or changes their computer. Use it only when the
        target is genuinely visible and you can identify it with care. When a
        person explicitly asks where something is, asks you to show them, or
        asks which visible button to use, prefer one helpful asha_mark before
        you answer in words.

        If the runtime additionally supplies asha_desktop_action, the person
        has explicitly enabled a visible control session. Use it only for one
        concrete action the person directly requested, only on a target you
        can see in the supplied image, and only when its effect is appropriate
        and low-risk. Never type passwords, secrets, payment details, recovery
        codes, or other credentials. Prefer a visual mark and a clarifying
        question when you are unsure. The runtime will visibly move the
        physical mouse or type; do not claim success beyond the input that the
        runtime confirms.
        """;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly List<ChatTurn> _history = [];
    private bool _disposed;

    public bool IsGroqConfigured => !string.IsNullOrWhiteSpace(GroqApiKey());
    public bool SupportsVision => string.Equals(GroqModel(), "qwen/qwen3.6-27b", StringComparison.OrdinalIgnoreCase);

    public async Task<VoiceTurnResult> RespondAsync(
        byte[] wav,
        Func<string, CancellationToken, Task<VisionAttachment?>>? visionResolver,
        Func<AshaVisualToolCall, VisionAttachment, CancellationToken, Task<string>>? visualToolExecutor,
        bool allowComputerControl,
        CancellationToken cancellationToken)
    {
        var transcript = await TranscribeAsync(wav, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(transcript))
            return new VoiceTurnResult("", "I did not catch that. Please try again.");

        var vision = visionResolver is null ? null : await visionResolver(transcript, cancellationToken).ConfigureAwait(false);
        var reply = await AskGroqAsync(transcript, vision, visualToolExecutor, allowComputerControl, cancellationToken).ConfigureAwait(false);
        return new VoiceTurnResult(transcript, reply);
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using var response = await _http.PostAsJsonAsync(
            $"{SpeechBaseUrl}/tts",
            new { text, voice = Environment.GetEnvironmentVariable("ASHA_TTS_VOICE") ?? "af_heart", speed = 1.0 },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Local TTS", cancellationToken).ConfigureAwait(false);

        var wav = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            using var stream = new MemoryStream(wav, writable: false);
            using var player = new SoundPlayer(stream);
            player.Load();
            player.PlaySync();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "asha-turn.wav");

        using var response = await _http.PostAsync($"{SpeechBaseUrl}/stt", form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Local speech recognition", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? "" : "";
    }

    private async Task<string> AskGroqAsync(
        string userText,
        VisionAttachment? vision,
        Func<AshaVisualToolCall, VisionAttachment, CancellationToken, Task<string>>? visualToolExecutor,
        bool allowComputerControl,
        CancellationToken cancellationToken)
    {
        var key = GroqApiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Groq is not configured. Run configure-groq.bat, then restart ASHA.");

        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        messages.AddRange(_history.Select(turn => new { role = turn.Role, content = turn.Content }));
        object currentUserContent = userText;
        if (vision is not null)
        {
            currentUserContent = new object[]
            {
                new { type = "text", text = $"{userText}\n\nASHA attached one current desktop image under the person's enabled shared-attention consent. Use it as visual context for this response. {vision.CoordinateInstruction}" },
                new { type = "image_url", image_url = new { url = vision.DataUrl } },
            };
        }
        messages.Add(new { role = "user", content = currentUserContent });

        var baseUrl = (Environment.GetEnvironmentVariable("ASHA_GROQ_BASE_URL") ?? GroqBaseUrl).TrimEnd('/');
        var model = GroqModel();
        var tools = vision is not null && vision.HasDesktopMapping && visualToolExecutor is not null && SupportsVision
            ? allowComputerControl ? VisualAndControlToolDefinitions : VisualToolDefinitions
            : null;
        string? reply;
        using (var first = await SendChatCompletionAsync(key, baseUrl, model, messages, tools, cancellationToken).ConfigureAwait(false))
        {
            var message = first.RootElement.GetProperty("choices")[0].GetProperty("message");
            var toolCalls = ReadToolCalls(message);
            if (toolCalls.Count == 0 || vision is null || visualToolExecutor is null)
            {
                reply = ReadMessageContent(message);
            }
            else
            {
                messages.Add(new
                {
                    role = "assistant",
                    content = ReadMessageContent(message) ?? string.Empty,
                    tool_calls = toolCalls.Select(call => new
                    {
                        id = call.Id,
                        type = "function",
                        function = new { name = call.Name, arguments = call.Arguments.GetRawText() },
                    }).ToArray(),
                });

                foreach (var toolCall in toolCalls)
                {
                    string output;
                    try { output = await visualToolExecutor(toolCall, vision, cancellationToken).ConfigureAwait(false); }
                    catch (Exception error) { output = JsonSerializer.Serialize(new { ok = false, error = error.Message }); }
                    messages.Add(new { role = "tool", tool_call_id = toolCall.Id, content = output });
                }

                using var final = await SendChatCompletionAsync(key, baseUrl, model, messages, tools, cancellationToken).ConfigureAwait(false);
                reply = ReadMessageContent(final.RootElement.GetProperty("choices")[0].GetProperty("message"));
            }
        }
        reply = NormalizeHumanFacingReply(StripReasoningMarkup(reply));
        if (string.IsNullOrWhiteSpace(reply)) reply = "I have highlighted the relevant area for you.";

        _history.Add(new ChatTurn("user", userText));
        _history.Add(new ChatTurn("assistant", reply));
        while (_history.Count > 16) _history.RemoveRange(0, 2);
        return reply;
    }

    private async Task<JsonDocument> SendChatCompletionAsync(
        string key,
        string baseUrl,
        string model,
        List<object> messages,
        IReadOnlyList<object>? tools,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0.55,
            ["max_tokens"] = 420,
        };
        if (tools is not null)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        // Qwen 3.6 otherwise exposes its internal thinking as spoken text. ASHA's
        // normal voice mode needs a fast, concise final answer instead.
        if (string.Equals(model, "qwen/qwen3.6-27b", StringComparison.OrdinalIgnoreCase))
            payload["reasoning_effort"] = "none";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Groq", cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string? ReadMessageContent(JsonElement message) =>
        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString()?.Trim() : null;

    private static List<AshaVisualToolCall> ReadToolCalls(JsonElement message)
    {
        var calls = new List<AshaVisualToolCall>();
        if (!message.TryGetProperty("tool_calls", out var rawCalls) || rawCalls.ValueKind != JsonValueKind.Array) return calls;
        foreach (var rawCall in rawCalls.EnumerateArray())
        {
            if (!rawCall.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !rawCall.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
                !function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String)
                continue;
            try
            {
                using var argumentDocument = JsonDocument.Parse(arguments.GetString()!);
                calls.Add(new AshaVisualToolCall(id.GetString()!, name.GetString()!, argumentDocument.RootElement.Clone()));
            }
            catch
            {
                calls.Add(new AshaVisualToolCall(id.GetString()!, name.GetString()!, JsonDocument.Parse("{}").RootElement.Clone()));
            }
        }
        return calls;
    }

    private static readonly IReadOnlyList<object> VisualToolDefinitions =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "asha_mark",
                description = "Place one safe, click-through ASHA overlay on a target visible in the supplied coordinate-mapped desktop image. This is visual guidance only and never controls the mouse or keyboard.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "kind", "x", "y" },
                    properties = new
                    {
                        kind = new { type = "string", @enum = new[] { "dot", "circle", "box", "arrow", "label" } },
                        x = new { type = "number", description = "Desktop pixel x coordinate." },
                        y = new { type = "number", description = "Desktop pixel y coordinate." },
                        w = new { type = "number", description = "Box width or signed arrow horizontal distance in pixels." },
                        h = new { type = "number", description = "Box height or signed arrow vertical distance in pixels." },
                        label = new { type = "string", description = "A short human-facing label." },
                        color = new { type = "string", description = "Optional hex color." },
                    },
                },
            },
        },
    ];

    private static readonly IReadOnlyList<object> VisualAndControlToolDefinitions =
        VisualToolDefinitions.Concat(
        [
            new
            {
                type = "function",
                function = new
                {
                    name = "asha_desktop_action",
                    description = "Perform one visible, permission-gated physical desktop input during an explicitly enabled ASHA control session. Use only for an action the person directly requested on a target visible in the supplied image.",
                    parameters = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "action" },
                        properties = new
                        {
                            action = new { type = "string", @enum = new[] { "click", "double_click", "right_click", "drag", "scroll", "type_text", "key" } },
                            x = new { type = "number", description = "Visible target desktop x coordinate. Required for click, double click, right click, and drag." },
                            y = new { type = "number", description = "Visible target desktop y coordinate. Required for click, double click, right click, and drag." },
                            end_x = new { type = "number", description = "Drag destination desktop x coordinate." },
                            end_y = new { type = "number", description = "Drag destination desktop y coordinate." },
                            delta = new { type = "number", description = "Wheel delta, between minus 1200 and 1200." },
                            text = new { type = "string", description = "Short non-sensitive text for the currently focused field." },
                            key = new { type = "string", @enum = new[] { "enter", "escape", "tab", "space", "backspace", "up", "down", "left", "right" } },
                        },
                    },
                },
            },
        ]).ToArray();

    private static string? GroqApiKey() => Environment.GetEnvironmentVariable("ASHA_GROQ_API_KEY");
    private static string GroqModel() => Environment.GetEnvironmentVariable("ASHA_GROQ_MODEL") ?? DefaultModel;

    /// <summary>
    /// A provider can still return raw reasoning markup even when it was asked
    /// not to. Never let private chain-of-thought text reach ASHA's transcript
    /// or speech output.
    /// </summary>
    private static string? StripReasoningMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        const string opening = "<think>";
        const string closing = "</think>";
        var cleaned = text;

        while (true)
        {
            var start = cleaned.IndexOf(opening, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return cleaned.Trim();

            var end = cleaned.IndexOf(closing, start + opening.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return cleaned[..start].Trim();

            cleaned = (cleaned[..start] + cleaned[(end + closing.Length)..]).Trim();
        }
    }

    /// <summary>
    /// The prompt is the main style guide. This small final safeguard prevents
    /// an occasional provider-style Markdown response from being read aloud as
    /// punctuation and expands the most common measurements for both TTS and
    /// the human-facing transcript.
    /// </summary>
    private static string? NormalizeHumanFacingReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"(?s)```.*?```", "I have kept the technical detail out of the spoken reply.");
        cleaned = Regex.Replace(cleaned, @"(?m)^\s{0,3}#{1,6}\s*", "");
        cleaned = Regex.Replace(cleaned, @"(?m)^\s*[-*•]\s+", "");
        cleaned = cleaned.Replace("**", "").Replace("__", "").Replace("`", "");
        cleaned = ExpandCommonUnits(cleaned);
        return Regex.Replace(cleaned, @"[ \t]+", " ").Trim();
    }

    private static string ExpandCommonUnits(string text)
    {
        var units = new Dictionary<string, (string Singular, string Plural)>(StringComparer.OrdinalIgnoreCase)
        {
            ["km"] = ("kilometer", "kilometers"),
            ["m"] = ("meter", "meters"),
            ["cm"] = ("centimeter", "centimeters"),
            ["mm"] = ("millimeter", "millimeters"),
            ["kg"] = ("kilogram", "kilograms"),
            ["g"] = ("gram", "grams"),
            ["ms"] = ("millisecond", "milliseconds"),
            ["s"] = ("second", "seconds"),
            ["min"] = ("minute", "minutes"),
            ["h"] = ("hour", "hours"),
            ["hz"] = ("hertz", "hertz"),
            ["khz"] = ("kilohertz", "kilohertz"),
            ["mhz"] = ("megahertz", "megahertz"),
            ["gb"] = ("gigabyte", "gigabytes"),
            ["mb"] = ("megabyte", "megabytes"),
        };

        text = Regex.Replace(text, @"(?<![\p{L}\p{N}])(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>km|cm|mm|kg|ms|min|khz|mhz|gb|mb|hz|m|g|s|h)(?![\p{L}\p{N}])", match =>
        {
            var value = match.Groups["value"].Value;
            var unit = match.Groups["unit"].Value;
            if (!units.TryGetValue(unit, out var words)) return match.Value;
            return $"{value} {((value == "1" || value == "1.0" || value == "1,0") ? words.Singular : words.Plural)}";
        }, RegexOptions.IgnoreCase);

        return Regex.Replace(text, @"(?<![\p{L}\p{N}])(?<value>\d+(?:[.,]\d+)?)\s*%(?![\p{L}\p{N}])", "${value} percent");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string service, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (detail.Length > 300) detail = detail[..300] + "…";
        throw new InvalidOperationException($"{service} failed ({(int)response.StatusCode}): {detail}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    private sealed record ChatTurn(string Role, string Content);
}

public sealed record VoiceTurnResult(string Transcript, string Reply);
public sealed record VisionAttachment(
    string Name,
    byte[] Bytes,
    int? ContextX,
    int? ContextY,
    int? ContextWidth,
    int? ContextHeight)
{
    public string DataUrl => $"data:image/png;base64,{Convert.ToBase64String(Bytes)}";
    public bool HasDesktopMapping => ContextX.HasValue && ContextY.HasValue && ContextWidth.GetValueOrDefault() > 0 && ContextHeight.GetValueOrDefault() > 0;
    public string CoordinateInstruction => HasDesktopMapping
        ? $"The image is a {ContextWidth} by {ContextHeight} pixel desktop crop. Its top-left image pixel maps to desktop coordinate {ContextX}, {ContextY}. If you use asha_mark, return full desktop pixel coordinates inside this crop."
        : "The image does not include a reliable desktop coordinate map, so do not use a visual marking tool.";
}

public sealed record AshaVisualToolCall(string Id, string Name, JsonElement Arguments);
