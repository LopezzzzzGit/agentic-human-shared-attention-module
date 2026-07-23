using System.Media;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SharedInference;

namespace AshaLive;

public sealed record ModelRequestMeasurement(
    DateTime MeasuredAtUtc,
    string Model,
    int MessageTextCharacters,
    int ToolSchemaCharacters,
    int ImageCount,
    int ImagePayloadCharacters,
    int? PromptTokens,
    int? CompletionTokens);

public sealed record ModelToolPhaseMeasurement(
    DateTime MeasuredAtUtc,
    string Phase,
    string? Capability,
    IReadOnlyList<string> DisclosedTools,
    string ToolChoice);

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
    private const string DefaultModel = "qwen/qwen3.6-27b";
    private const string SystemPrompt = """
        You are ASHA, a warm and concise shared-attention companion on the
        person's desktop. This is usually a spoken conversation mediated by
        local speech recognition and local speech. Stay in character and speak
        naturally. Mention implementation details only when explicitly asked.
        Keep ordinary answers under four short sentences, use complete
        human-facing words, and avoid Markdown, raw JSON, code, paths, logs,
        and engineering jargon in speech.

        Runtime evidence outranks your assumptions. Never claim that you saw,
        opened, clicked, selected, moved, typed, highlighted, or changed
        anything unless current image, state, or successful tool evidence
        establishes it. If evidence is missing or ambiguous, look again, ask,
        or decline instead of guessing. Never expose or enter credentials,
        payment data, recovery codes, or secrets.

        Images are permission-gated current evidence, not a continuous feed.
        Use the smallest useful view. Pointer area is only for explicit
        references to the person's pointer or “here”; otherwise prefer the
        foreground window for its controls, a named side or quadrant when
        requested, and the entire desktop only to locate an unknown region.
        The pointer is a salience hint, never a camera constraint. Request a
        closer crop when text or a target is too small.

        ASHA exposes capabilities progressively. When a capability-selection
        tool is available, use it only when the request genuinely needs a
        desktop capability. Answer ordinary conversation directly. At every
        phase, use only tools present in the current request. Never infer,
        remember, or call a tool from an earlier or later phase.

        A foreground UI snapshot is versioned. Local element IDs and old
        coordinates expire when the state changes. Address controls by exact
        accessible or visible name, semantic role, and named container. A
        heading with the same words as an interactive item is not that item.
        Spoken words may contain recognition errors. Prefer one unambiguous
        close match from the current visible or accessible names; ask when
        multiple plausible matches remain.
        During a bounded desktop task perform one tool action, inspect the
        fresh state, and continue only while the original goal remains.
        """;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly GroqKeyRotator _groqKeys = GroqKeyRotator.LoadDefault();
    private readonly List<ChatTurn> _history = [];
    private string _sessionSummary = string.Empty;
    private bool _disposed;

    public event Action<ModelRequestMeasurement>? ModelRequestMeasured;
    public event Action<ModelToolPhaseMeasurement>? ModelToolPhaseMeasured;
    public bool IsGroqConfigured => _groqKeys.IsConfigured;
    public int GroqKeyCount => _groqKeys.Count;
    public bool SupportsVision => ConfiguredModelSupportsVision();

    private static bool ConfiguredModelSupportsVision()
    {
        var declared = Environment.GetEnvironmentVariable("ASHA_MODEL_SUPPORTS_VISION");
        if (bool.TryParse(declared, out var supported)) return supported;

        // Preserve a safe default for the demonstrator's bundled model. Other
        // providers and local adapters declare their capability explicitly
        // instead of requiring ASHA's orchestration to know model brand names.
        return string.Equals(GroqModel(), DefaultModel, StringComparison.OrdinalIgnoreCase);
    }

    public void LoadConversationMemory(IEnumerable<ConversationMessage> recentMessages, string? summary)
    {
        _history.Clear();
        foreach (var message in recentMessages)
        {
            var role = string.Equals(message.Speaker, "ASHA", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
            _history.Add(new ChatTurn(role, message.Text));
        }
        _sessionSummary = summary?.Trim() ?? string.Empty;
        TrimRecentHistory();
    }

    public void ResetConversationMemory()
    {
        _history.Clear();
        _sessionSummary = string.Empty;
    }

    public async Task<string> CompressConversationAsync(
        string? existingSummary,
        IReadOnlyList<ConversationMessage> additionalMessages,
        CancellationToken cancellationToken)
    {
        if (additionalMessages.Count == 0) return existingSummary?.Trim() ?? string.Empty;
        if (!IsGroqConfigured) return existingSummary?.Trim() ?? string.Empty;

        var transcript = string.Join("\n", additionalMessages.Select(message =>
            $"[{message.Timestamp:yyyy-MM-dd HH:mm}] {message.Speaker}: {message.Text}"));
        var previous = string.IsNullOrWhiteSpace(existingSummary) ? "No earlier summary exists." : existingSummary.Trim();
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = "Maintain ASHA's durable session-memory summary. Preserve the person's goals, decisions, preferences, named applications and targets, explanations, unresolved questions, demonstrations, permissions, actions, visual observations, and verified outcomes. Remove repetition and small talk, but never invent facts. Return compact plain prose without Markdown. This is a derived index; the full transcript remains stored separately.",
            },
            new
            {
                role = "user",
                content = $"Earlier session summary:\n{previous}\n\nNew full-log segment to incorporate:\n{transcript}\n\nReturn the updated session summary.",
            },
        };
        var baseUrl = (Environment.GetEnvironmentVariable("ASHA_GROQ_BASE_URL") ?? GroqBaseUrl).TrimEnd('/');
        using var response = await SendChatCompletionAsync(baseUrl, GroqModel(), messages, null, cancellationToken, 900).ConfigureAwait(false);
        var summary = StripReasoningMarkup(ReadMessageContent(response.RootElement.GetProperty("choices")[0].GetProperty("message")));
        return string.IsNullOrWhiteSpace(summary) ? previous : Regex.Replace(summary, @"\s+", " ").Trim();
    }

    public async Task<VoiceTurnResult> RespondAsync(
        byte[] wav,
        Func<string, VisionRequest, CancellationToken, Task<VisionAttachment?>>? visionResolver,
        Func<AshaVisualToolCall, VisionAttachment?, CancellationToken, Task<string>>? visualToolExecutor,
        ComputerControlAccess controlAccess,
        bool allowModelRequestedVision,
        DesktopAwarenessContext? awarenessContext,
        CancellationToken cancellationToken)
    {
        var transcript = await TranscribeTurnAsync(wav, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(transcript))
            return new VoiceTurnResult("", "I did not catch that. Please try again.");

        var reply = await RespondToTranscriptAsync(
            transcript,
            visionResolver,
            visualToolExecutor,
            controlAccess,
            allowModelRequestedVision,
            awarenessContext,
            cancellationToken).ConfigureAwait(false);
        return new VoiceTurnResult(transcript, reply);
    }

    public Task<string> TranscribeTurnAsync(byte[] wav, CancellationToken cancellationToken) =>
        TranscribeAsync(wav, cancellationToken);

    public async Task<string> RespondToTranscriptAsync(
        string transcript,
        Func<string, VisionRequest, CancellationToken, Task<VisionAttachment?>>? visionResolver,
        Func<AshaVisualToolCall, VisionAttachment?, CancellationToken, Task<string>>? visualToolExecutor,
        ComputerControlAccess controlAccess,
        bool allowModelRequestedVision,
        DesktopAwarenessContext? awarenessContext,
        CancellationToken cancellationToken)
    {
        var perceptionPlan = InferPerceptionPlan(transcript);
        var vision = visionResolver is null
            ? null
            : await visionResolver(transcript, new VisionRequest(VisionRequestKind.PersonSelected, VisionRequestScope.PointerArea), cancellationToken).ConfigureAwait(false);
        if (vision is null &&
            visionResolver is not null &&
            allowModelRequestedVision &&
            perceptionPlan.RequiresFreshEvidence)
        {
            vision = await visionResolver(
                transcript,
                new VisionRequest(
                    VisionRequestKind.ModelRequested,
                    perceptionPlan.Scope,
                    PreferTextDetail: perceptionPlan.PreferTextDetail),
                cancellationToken).ConfigureAwait(false);
        }
        try
        {
            return await AskGroqAsync(
                transcript,
                vision,
                visionResolver,
                visualToolExecutor,
                controlAccess,
                allowModelRequestedVision,
                awarenessContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GroqRequestException error) when (
            vision is not null &&
            perceptionPlan.Goal is ActivePerceptionGoal.Observe or ActivePerceptionGoal.Locate or ActivePerceptionGoal.Verify &&
            error.StatusCode is 400 or 413 or 422 &&
            !cancellationToken.IsCancellationRequested)
        {
            // If a provider rejects the richer multi-turn/tool payload, retain
            // the person's turn and retry once with the smallest valid vision
            // request. This is provider-neutral degradation, not a second
            // interpretation of the desktop or an invented answer.
            return await AskMinimalVisualRecoveryAsync(transcript, vision, cancellationToken).ConfigureAwait(false);
        }
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
        var playbackWav = AddPlaybackLeadIn(wav, TimeSpan.FromMilliseconds(140));
        await Task.Run(() =>
        {
            using var stream = new MemoryStream(playbackWav, writable: false);
            using var player = new SoundPlayer(stream);
            player.Load();
            player.PlaySync();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives a sleeping Windows audio endpoint time to open before ASHA's first
    /// phoneme arrives.  The silence is part of the same WAV stream, so it also
    /// protects the first reply after a speaker or Bluetooth device wakes up.
    /// </summary>
    internal static byte[] AddPlaybackLeadIn(byte[] wav, TimeSpan leadIn)
    {
        if (wav.Length == 0 || leadIn <= TimeSpan.Zero) return wav;

        using var input = new MemoryStream(wav, writable: false);
        using var reader = new WaveFileReader(input);
        using var output = new MemoryStream(wav.Length + reader.WaveFormat.AverageBytesPerSecond / 4);
        using (var writer = new WaveFileWriter(output, reader.WaveFormat))
        {
            var silenceLength = (int)Math.Ceiling(reader.WaveFormat.AverageBytesPerSecond * leadIn.TotalSeconds);
            silenceLength -= silenceLength % reader.WaveFormat.BlockAlign;
            if (silenceLength > 0) writer.Write(new byte[silenceLength], 0, silenceLength);

            var buffer = new byte[16 * 1024];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                writer.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public async Task<string?> DescribeDesktopViewAsync(VisionAttachment vision, AwarenessScene? scene, CancellationToken cancellationToken)
    {
        if (!SupportsVision) return null;
        if (!IsGroqConfigured) return null;

        var foreground = scene?.Foreground?.DisplayName ?? "unknown application";
        var hovered = scene?.Hovered?.DisplayName;
        var localContext = string.IsNullOrWhiteSpace(hovered) || string.Equals(hovered, foreground, StringComparison.Ordinal)
            ? $"Windows reports the current foreground surface as {foreground}."
            : $"Windows reports the current foreground surface as {foreground}, with the pointer over {hovered}.";
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = "You are ASHA's private visual-awareness sensor. Describe only what is clearly visible in the supplied current desktop crop. Return one or two compact factual sentences for ASHA's later spoken context. Name the application, prominent content, open dialog, and likely focus when visible. Do not address the person, speculate, use Markdown, or mention screenshots, image models, or these instructions.",
            },
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = $"Create the current desktop awareness summary. {localContext}" },
                    new { type = "image_url", image_url = new { url = vision.DataUrl } },
                },
            },
        };
        var baseUrl = (Environment.GetEnvironmentVariable("ASHA_GROQ_BASE_URL") ?? GroqBaseUrl).TrimEnd('/');
        using var response = await SendChatCompletionAsync(baseUrl, GroqModel(), messages, null, cancellationToken).ConfigureAwait(false);
        var summary = StripReasoningMarkup(ReadMessageContent(response.RootElement.GetProperty("choices")[0].GetProperty("message")));
        return string.IsNullOrWhiteSpace(summary) ? null : Regex.Replace(summary, @"\s+", " ").Trim();
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

    private async Task<string> AskMinimalVisualRecoveryAsync(
        string userText,
        VisionAttachment vision,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = "You are ASHA, a warm and concise desktop companion. Answer the person's current question only from the attached current desktop image and its Windows context. State what is clearly visible, admit uncertainty, and never claim that you clicked, opened, moved, or changed anything. Use one or two natural spoken sentences without Markdown or technical implementation language.",
            },
            CreateUserMessage(userText, vision),
        };
        var baseUrl = (Environment.GetEnvironmentVariable("ASHA_GROQ_BASE_URL") ?? GroqBaseUrl).TrimEnd('/');
        using var response = await SendChatCompletionAsync(
            baseUrl,
            GroqModel(),
            messages,
            tools: null,
            cancellationToken: cancellationToken,
            maxTokens: 260).ConfigureAwait(false);
        var message = response.RootElement.GetProperty("choices")[0].GetProperty("message");
        var reply = NormalizeHumanFacingReply(StripReasoningMarkup(ReadMessageContent(message)));
        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("The model returned no grounded visual answer.");

        _history.Add(new ChatTurn("user", userText));
        _history.Add(new ChatTurn("assistant", reply));
        TrimRecentHistory();
        return reply;
    }

    private async Task<string> AskGroqAsync(
        string userText,
        VisionAttachment? vision,
        Func<string, VisionRequest, CancellationToken, Task<VisionAttachment?>>? visionResolver,
        Func<AshaVisualToolCall, VisionAttachment?, CancellationToken, Task<string>>? visualToolExecutor,
        ComputerControlAccess controlAccess,
        bool allowModelRequestedVision,
        DesktopAwarenessContext? awarenessContext,
        CancellationToken cancellationToken)
    {
        // Removing ASHA's own guidance is a local UI operation. Handle it
        // before provider configuration and network access so it remains
        // immediate when Groq is cooling down or unavailable. The UI executor
        // still guarantees that human teaching cues, evidence, session data
        // and the computer-control frame are outside this operation's scope.
        if (TryExtractGuidanceClearRequest(userText, out var localClearScope) &&
            visualToolExecutor is not null)
        {
            using var localArguments = JsonDocument.Parse(JsonSerializer.Serialize(new { scope = localClearScope }));
            var localCall = new AshaVisualToolCall(
                $"asha-runtime-{Guid.NewGuid():N}",
                "asha_clear_guidance",
                localArguments.RootElement.Clone());
            string localOutput;
            try { localOutput = await visualToolExecutor(localCall, vision, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { localOutput = JsonSerializer.Serialize(new { ok = false, error = error.Message }); }
            var localReply = TryRenderToolResult(localCall, localOutput, out var rendered)
                ? rendered
                : "I couldn't verify that the visual guidance was removed, so I won't claim that it was.";
            localReply = NormalizeHumanFacingReply(localReply) ?? "I couldn't remove that visual guidance.";
            _history.Add(new ChatTurn("user", userText));
            _history.Add(new ChatTurn("assistant", localReply));
            TrimRecentHistory();
            return localReply;
        }

        if (!IsGroqConfigured)
            throw new InvalidOperationException("Set ASHA_GROQ_KEYS or configure a Groq key in settings, then restart ASHA.");

        var allowApplicationControl = controlAccess.CanOpenApplicationsAndFolders;
        var allowDesktopAction =
            controlAccess.CanUseKeyboard ||
            controlAccess.CanUsePhysicalCursor ||
            controlAccess.CanInteractWithVirtualCursor;
        var perceptionPlan = InferPerceptionPlan(userText);
        if (vision is null &&
            perceptionPlan.RequiresFreshEvidence &&
            allowModelRequestedVision &&
            visionResolver is not null &&
            SupportsVision)
        {
            vision = await visionResolver(
                userText,
                new VisionRequest(
                    VisionRequestKind.ModelRequested,
                    perceptionPlan.Scope,
                    PreferTextDetail: perceptionPlan.PreferTextDetail),
                cancellationToken).ConfigureAwait(false);
        }

        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt },
            new
            {
                role = "system",
                content = controlAccess.DescribeForModel(virtualInteractionConnected: true),
            },
        };
        if (!string.IsNullOrWhiteSpace(_sessionSummary))
        {
            messages.Add(new
            {
                role = "system",
                content = $"Retained memory from the active ASHA session: {_sessionSummary} Treat it as prior conversation context. The complete log remains local; ask naturally when a remembered detail is ambiguous.",
            });
        }
        if (awarenessContext is not null && DateTime.UtcNow - awarenessContext.ObservedAtUtc <= TimeSpan.FromSeconds(30))
        {
            var age = Math.Max(0, (int)Math.Round((DateTime.UtcNow - awarenessContext.ObservedAtUtc).TotalSeconds));
            messages.Add(new
            {
                role = "system",
                content = $"ASHA live awareness observed the desktop about {age} seconds ago: {awarenessContext.Summary} Use this as genuine recent context. Request a fresh view when the person's question requires details not established by this summary.",
            });
        }
        if (vision is not null && perceptionPlan.RequiresFreshEvidence)
        {
            messages.Add(new
            {
                role = "system",
                content = FreshEvidenceInstruction(perceptionPlan.Goal),
            });
        }
        messages.AddRange(_history.Select(turn => new { role = turn.Role, content = turn.Content }));
        messages.Add(CreateUserMessage(userText, vision));

        var baseUrl = (Environment.GetEnvironmentVariable("ASHA_GROQ_BASE_URL") ?? GroqBaseUrl).TrimEnd('/');
        var model = GroqModel();
        var executedToolNames = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<object>? tools = SelectInitialTools(
            perceptionPlan,
            hasGroundedVision: vision is not null && vision.HasDesktopMapping && SupportsVision,
            allowApplicationControl: allowApplicationControl,
            allowDesktopAction: allowDesktopAction,
            canRequestVision: allowModelRequestedVision && visionResolver is not null && SupportsVision,
            hasToolExecutor: visualToolExecutor is not null);
        var initialToolChoice = InitialToolChoice(perceptionPlan, vision, allowDesktopAction, tools);
        ReportToolPhase("initial", null, tools, initialToolChoice);
        string? reply;
        using (var first = await SendChatCompletionAsync(
                   baseUrl, model, messages, tools, cancellationToken,
                   toolChoice: initialToolChoice).ConfigureAwait(false))
        {
            var message = first.RootElement.GetProperty("choices")[0].GetProperty("message");
            var toolCalls = ReadToolCalls(message);
            var capabilityRequests = toolCalls
                .Where(call => string.Equals(call.Name, "asha_choose_capability", StringComparison.Ordinal))
                .ToArray();
            var viewRequests = toolCalls.Where(call => string.Equals(call.Name, "asha_request_view", StringComparison.Ordinal)).ToArray();
            if (capabilityRequests.Length > 0)
            {
                reply = await CompleteCapabilitySelectionAsync(
                    messages,
                    message,
                    capabilityRequests[0],
                    userText,
                    vision,
                    visionResolver,
                    visualToolExecutor,
                    perceptionPlan,
                    allowApplicationControl,
                    allowDesktopAction,
                    allowModelRequestedVision,
                    baseUrl,
                    model,
                    cancellationToken,
                    executedToolNames).ConfigureAwait(false);
            }
            else if (vision is null && viewRequests.Length > 0 && visionResolver is not null)
            {
                vision = await visionResolver(userText, ReadVisionRequest(viewRequests[0]), cancellationToken).ConfigureAwait(false);
                if (vision is not null)
                {
                    messages[^1] = CreateUserMessage(userText, vision);
                    tools = vision.HasDesktopMapping && visualToolExecutor is not null
                        ? SelectGroundedToolsAfterModelView(perceptionPlan, allowDesktopAction)
                        : null;
                    var groundedChoice = ToolChoiceForPlan(perceptionPlan, vision, allowDesktopAction, tools);
                    ReportToolPhase("model_requested_view_grounded", null, tools, groundedChoice);
                    using var grounded = await SendChatCompletionAsync(
                        baseUrl, model, messages, tools, cancellationToken,
                        toolChoice: groundedChoice).ConfigureAwait(false);
                    var groundedMessage = grounded.RootElement.GetProperty("choices")[0].GetProperty("message");
                    reply = await CompleteGroundedTurnAsync(
                        messages, groundedMessage, userText, vision, visionResolver, visualToolExecutor,
                        allowDesktopAction, perceptionPlan, tools, baseUrl, model, cancellationToken, executedToolNames).ConfigureAwait(false);
                }
                else
                {
                    AppendAssistantToolCalls(messages, message, viewRequests);
                    foreach (var request in viewRequests)
                    {
                        messages.Add(new
                        {
                            role = "tool",
                            tool_call_id = request.Id,
                            content = JsonSerializer.Serialize(new { ok = false, error = "A current desktop view is not available under the active session and privacy settings." }),
                        });
                    }
                    using var unavailable = await SendChatCompletionAsync(baseUrl, model, messages, null, cancellationToken).ConfigureAwait(false);
                    reply = ReadMessageContent(unavailable.RootElement.GetProperty("choices")[0].GetProperty("message"));
                }
            }
            else if (toolCalls.Count > 0 && visualToolExecutor is not null)
                reply = vision is not null
                    ? await CompleteGroundedTurnAsync(
                        messages, message, userText, vision, visionResolver, visualToolExecutor,
                        allowDesktopAction, perceptionPlan, tools, baseUrl, model, cancellationToken, executedToolNames).ConfigureAwait(false)
                    : await CompleteVisualToolCallsAsync(
                        messages, message, vision, visualToolExecutor, tools, baseUrl, model,
                        cancellationToken, executedToolNames, visionResolver, userText, allowDesktopAction).ConfigureAwait(false);
            else
                reply = ReadMessageContent(message);
        }

        if (executedToolNames.Count == 0 && ClaimsVisualActionSucceeded(reply))
            reply = "I couldn't verify that visual action, so I haven't claimed it succeeded.";
        reply = NormalizeHumanFacingReply(StripReasoningMarkup(reply));
        if (string.IsNullOrWhiteSpace(reply)) reply = "I have highlighted the relevant area for you.";

        _history.Add(new ChatTurn("user", userText));
        _history.Add(new ChatTurn("assistant", reply));
        TrimRecentHistory();
        return reply;
    }

    private async Task<string?> CompleteCapabilitySelectionAsync(
        List<object> messages,
        JsonElement selectionMessage,
        AshaVisualToolCall selectionCall,
        string userText,
        VisionAttachment? vision,
        Func<string, VisionRequest, CancellationToken, Task<VisionAttachment?>>? visionResolver,
        Func<AshaVisualToolCall, VisionAttachment?, CancellationToken, Task<string>>? visualToolExecutor,
        ActivePerceptionPlan perceptionPlan,
        bool allowApplicationControl,
        bool allowDesktopAction,
        bool allowModelRequestedVision,
        string baseUrl,
        string model,
        CancellationToken cancellationToken,
        ISet<string> executedToolNames)
    {
        var capability = selectionCall.Arguments.TryGetProperty("capability", out var rawCapability) &&
                         rawCapability.ValueKind == JsonValueKind.String
            ? rawCapability.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        var disclosedTools = SelectToolsForCapability(
            capability,
            vision is { HasDesktopMapping: true },
            allowApplicationControl,
            allowDesktopAction,
            allowModelRequestedVision && visionResolver is not null && SupportsVision);

        AppendAssistantToolCalls(messages, selectionMessage, [selectionCall]);
        messages.Add(new
        {
            role = "tool",
            tool_call_id = selectionCall.Id,
            content = disclosedTools is null
                ? JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "That capability is not available under the current session permissions and evidence.",
                })
                : JsonSerializer.Serialize(new
                {
                    ok = true,
                    selected = capability,
                    instruction = "The selected capability's exact tool contract is available on the next turn. Use only that contract.",
                }),
        });

        if (disclosedTools is null)
        {
            using var unavailable = await SendChatCompletionAsync(
                baseUrl, model, messages, null, cancellationToken).ConfigureAwait(false);
            return ReadMessageContent(unavailable.RootElement.GetProperty("choices")[0].GetProperty("message"));
        }

        var toolChoice = ToolChoiceForCapabilityPhase(capability, vision, disclosedTools);
        ReportToolPhase("capability_disclosed", capability, disclosedTools, toolChoice);
        using var disclosed = await SendChatCompletionAsync(
            baseUrl,
            model,
            messages,
            disclosedTools,
            cancellationToken,
            toolChoice: toolChoice).ConfigureAwait(false);
        var disclosedMessage = disclosed.RootElement.GetProperty("choices")[0].GetProperty("message");
        var disclosedCalls = ReadToolCalls(disclosedMessage);
        var viewRequest = disclosedCalls.FirstOrDefault(call =>
            string.Equals(call.Name, "asha_request_view", StringComparison.Ordinal));

        if (vision is null && viewRequest is not null && visionResolver is not null)
        {
            var refreshed = await visionResolver(
                userText,
                ReadVisionRequest(viewRequest),
                cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
                return "I cannot access a current view under the active privacy and session settings.";

            vision = refreshed;
            AppendAssistantToolCalls(messages, disclosedMessage, [viewRequest]);
            messages.Add(new
            {
                role = "tool",
                tool_call_id = viewRequest.Id,
                content = JsonSerializer.Serialize(new { ok = true, supplied = "fresh_current_view" }),
            });
            RemovePriorVisionMessages(messages);
            messages.Add(CreateUserMessage($"Use this current view to continue: {userText}", vision));
            disclosedTools = SelectToolsForCapability(
                capability,
                hasGroundedVision: true,
                allowApplicationControl,
                allowDesktopAction,
                canRequestVision: true);
            if (disclosedTools is null)
                return "The requested capability is not available for this current view.";

            var groundedToolChoice =
                capability is "desktop_interaction" or "visual_guidance" ? "required" : "auto";
            ReportToolPhase("grounded_capability", capability, disclosedTools, groundedToolChoice);
            using var grounded = await SendChatCompletionAsync(
                baseUrl,
                model,
                messages,
                disclosedTools,
                cancellationToken,
                toolChoice: groundedToolChoice).ConfigureAwait(false);
            disclosedMessage = grounded.RootElement.GetProperty("choices")[0].GetProperty("message");
        }

        var finalCalls = ReadToolCalls(disclosedMessage);
        if (finalCalls.Count == 0) return ReadMessageContent(disclosedMessage);
        if (visualToolExecutor is null)
            return "The selected desktop capability has no connected executor.";

        return vision is not null
            ? await CompleteGroundedTurnAsync(
                messages,
                disclosedMessage,
                userText,
                vision,
                visionResolver,
                visualToolExecutor,
                allowDesktopAction,
                perceptionPlan,
                disclosedTools,
                baseUrl,
                model,
                cancellationToken,
                executedToolNames).ConfigureAwait(false)
            : await CompleteVisualToolCallsAsync(
                messages,
                disclosedMessage,
                vision,
                visualToolExecutor,
                disclosedTools,
                baseUrl,
                model,
                cancellationToken,
                executedToolNames,
                visionResolver,
                userText,
                allowDesktopAction).ConfigureAwait(false);
    }

    private void TrimRecentHistory()
    {
        var characters = _history.Sum(turn => turn.Content.Length + turn.Role.Length + 8);
        while (_history.Count > 2 && characters > SessionMemoryStore.RecentCharacterBudget)
        {
            var remove = Math.Min(2, _history.Count - 2);
            for (var index = 0; index < remove; index++)
                characters -= _history[index].Content.Length + _history[index].Role.Length + 8;
            _history.RemoveRange(0, remove);
        }
    }

    private ActivePerceptionPlan InferPerceptionPlan(string userText)
    {
        var plan = ActivePerceptionPlanner.Infer(userText);
        if (plan.RequiresFreshEvidence ||
            !Regex.IsMatch(
                userText,
                @"\b(?:try|do|send|click|open|select)\s+(?:it\s+)?again\b|\b(?:again|retry|nochmal|erneut|versuch(?:e|en)?\s+es\s+nochmal)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return plan;

        for (var index = _history.Count - 1; index >= 0; index--)
        {
            var previous = _history[index];
            if (!string.Equals(previous.Role, "user", StringComparison.Ordinal)) continue;
            var previousPlan = ActivePerceptionPlanner.Infer(previous.Content);
            if (previousPlan.Goal is ActivePerceptionGoal.Act or ActivePerceptionGoal.Annotate or ActivePerceptionGoal.Verify)
                return previousPlan;
        }
        return plan;
    }

    private static object CreateUserMessage(string userText, VisionAttachment? vision)
    {
        if (vision is null) return new { role = "user", content = (object)userText };
        return new
        {
            role = "user",
            content = (object)new object[]
            {
                new { type = "text", text = $"{userText}\n\nASHA attached one current desktop image under the person's enabled shared-attention consent. Use it as visual context for this response. {vision.CoordinateInstruction}" },
                new { type = "image_url", image_url = new { url = vision.DataUrl } },
            },
        };
    }

    private async Task<string?> CompleteGroundedTurnAsync(
        List<object> messages,
        JsonElement message,
        string userText,
        VisionAttachment vision,
        Func<string, VisionRequest, CancellationToken, Task<VisionAttachment?>>? visionResolver,
        Func<AshaVisualToolCall, VisionAttachment?, CancellationToken, Task<string>>? visualToolExecutor,
        bool allowDesktopAction,
        ActivePerceptionPlan perceptionPlan,
        IReadOnlyList<object>? tools,
        string baseUrl,
        string model,
        CancellationToken cancellationToken,
        ISet<string>? executedToolNames,
        int remainingDesktopSteps = 8,
        string? desktopTaskId = null)
    {
        var toolCalls = ReadToolCalls(message);
        var captureCall = toolCalls.FirstOrDefault(call =>
            string.Equals(call.Name, "asha_request_detail", StringComparison.Ordinal) ||
            string.Equals(call.Name, "asha_request_view", StringComparison.Ordinal));
        if (captureCall is null || visionResolver is null)
            return await CompleteVisualToolCallsAsync(
                messages, message, vision, visualToolExecutor, tools, baseUrl, model,
                cancellationToken, executedToolNames, visionResolver, userText, allowDesktopAction,
                remainingDesktopSteps, desktopTaskId).ConfigureAwait(false);

        AppendAssistantToolCalls(messages, message, [captureCall]);
        var captureRequest = string.Equals(captureCall.Name, "asha_request_detail", StringComparison.Ordinal)
            ? ReadDetailVisionRequest(captureCall, vision)
            : ReadVisionRequest(captureCall);
        VisionAttachment? refreshedView = null;
        if (captureRequest is not null)
            refreshedView = await visionResolver(userText, captureRequest, cancellationToken).ConfigureAwait(false);

        object captureResult = refreshedView is null
            ? new { ok = false, error = "The requested desktop view was not available." }
            : new
            {
                ok = true,
                detail = string.Equals(captureCall.Name, "asha_request_detail", StringComparison.Ordinal)
                    ? "A fresh higher-detail region is attached in the next message."
                    : "A fresh independently selected desktop view is attached in the next message.",
            };
        messages.Add(new
        {
            role = "tool",
            tool_call_id = captureCall.Id,
            content = JsonSerializer.Serialize(captureResult),
        });

        if (refreshedView is null)
        {
            using var unavailable = await SendChatCompletionAsync(baseUrl, model, messages, null, cancellationToken).ConfigureAwait(false);
            return ReadMessageContent(unavailable.RootElement.GetProperty("choices")[0].GetProperty("message"));
        }

        RemovePriorVisionMessages(messages);
        messages.Add(CreateUserMessage(
            $"Use this fresh model-selected view to complete the person's original request: {userText}",
            refreshedView));
        var detailTools = SelectGroundedToolsForPlan(
            perceptionPlan with { AllowCloserLook = false },
            allowDesktopAction) ?? SelectGroundedToolsAfterModelView(
                perceptionPlan with { AllowCloserLook = false },
                allowDesktopAction);
        var detailChoice = ToolChoiceForPlan(perceptionPlan, refreshedView, allowDesktopAction, detailTools);
        ReportToolPhase("detail_view_grounded", null, detailTools, detailChoice);
        using var refined = await SendChatCompletionAsync(
            baseUrl, model, messages, detailTools, cancellationToken,
            toolChoice: detailChoice).ConfigureAwait(false);
        var refinedMessage = refined.RootElement.GetProperty("choices")[0].GetProperty("message");
        return await CompleteVisualToolCallsAsync(
            messages, refinedMessage, refreshedView, visualToolExecutor, detailTools, baseUrl, model,
            cancellationToken, executedToolNames, visionResolver, userText, allowDesktopAction,
            remainingDesktopSteps, desktopTaskId).ConfigureAwait(false);
    }

    private static VisionRequest? ReadDetailVisionRequest(AshaVisualToolCall call, VisionAttachment source)
    {
        if (!source.HasDesktopMapping ||
            !TryReadImageInteger(call.Arguments, "x", 0, source.ImageWidth, out var imageX) ||
            !TryReadImageInteger(call.Arguments, "y", 0, source.ImageHeight, out var imageY) ||
            !TryReadImageInteger(call.Arguments, "w", 12, source.ImageWidth, out var imageWidth) ||
            !TryReadImageInteger(call.Arguments, "h", 12, source.ImageHeight, out var imageHeight) ||
            imageX + imageWidth > source.ImageWidth ||
            imageY + imageHeight > source.ImageHeight ||
            !source.TryMapImagePoint(imageX, imageY, out var left, out var top) ||
            !source.TryMapImagePoint(imageX + imageWidth, imageY + imageHeight, out var right, out var bottom))
            return null;

        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var horizontalPadding = Math.Max(24, (int)Math.Round(width * 0.12));
        var verticalPadding = Math.Max(20, (int)Math.Round(height * 0.12));
        var region = new DesktopCaptureRegion(
            left - horizontalPadding,
            top - verticalPadding,
            width + (horizontalPadding * 2),
            height + (verticalPadding * 2),
            PreferTextDetail: true);
        return new VisionRequest(
            VisionRequestKind.ModelRequested,
            VisionRequestScope.Region,
            Region: region,
            PreferTextDetail: true);
    }

    private static bool TryReadImageInteger(JsonElement arguments, string name, int minimum, int maximum, out int value)
    {
        value = 0;
        if (!arguments.TryGetProperty(name, out var raw) || raw.ValueKind != JsonValueKind.Number || !raw.TryGetDouble(out var number) ||
            double.IsNaN(number) || double.IsInfinity(number))
            return false;
        value = (int)Math.Round(number);
        return value >= minimum && value <= maximum;
    }

    private static IReadOnlyList<object>? SelectGroundedToolsForPlan(
        ActivePerceptionPlan plan,
        bool allowDesktopAction) => plan.Goal switch
    {
        ActivePerceptionGoal.Annotate => plan.AllowCloserLook ? VisualWithDetailToolDefinitions : VisualToolDefinitions,
        ActivePerceptionGoal.Act when allowDesktopAction => plan.AllowCloserLook
            ? DesktopActionWithDetailToolDefinitions
            : DesktopActionToolDefinitions,
        ActivePerceptionGoal.Observe or ActivePerceptionGoal.Locate or ActivePerceptionGoal.Verify
            when plan.AllowCloserLook => DetailAndViewToolDefinitions,
        _ => null,
    };

    private static IReadOnlyList<object>? SelectGroundedToolsAfterModelView(
        ActivePerceptionPlan plan,
        bool allowDesktopAction) =>
        SelectGroundedToolsForPlan(plan, allowDesktopAction) ??
        (allowDesktopAction ? DesktopActionWithDetailToolDefinitions : DetailAndViewToolDefinitions);

    private static string ToolChoiceForPlan(
        ActivePerceptionPlan plan,
        VisionAttachment? vision,
        bool allowDesktopAction,
        IReadOnlyList<object>? tools) =>
        vision is { HasDesktopMapping: true } &&
        ((plan.Goal == ActivePerceptionGoal.Act &&
          allowDesktopAction &&
          ToolNames(tools).Contains("asha_desktop_action", StringComparer.Ordinal)) ||
         (plan.Goal == ActivePerceptionGoal.Annotate &&
          ToolNames(tools).Contains("asha_mark", StringComparer.Ordinal)))
            ? "required"
            : "auto";

    private static object InitialToolChoice(
        ActivePerceptionPlan plan,
        VisionAttachment? vision,
        bool allowDesktopAction,
        IReadOnlyList<object>? tools)
    {
        var names = ToolNames(tools);
        return plan.Goal != ActivePerceptionGoal.None &&
               names.Count == 1 &&
               string.Equals(names[0], "asha_choose_capability", StringComparison.Ordinal)
            ? RequiredFunctionToolChoice("asha_choose_capability")
            : ToolChoiceForPlan(plan, vision, allowDesktopAction, tools);
    }

    private static object ToolChoiceForCapabilityPhase(
        string capability,
        VisionAttachment? vision,
        IReadOnlyList<object>? tools)
    {
        var names = ToolNames(tools);
        if (vision is null &&
            names.Count == 1 &&
            string.Equals(names[0], "asha_request_view", StringComparison.Ordinal))
            return RequiredFunctionToolChoice("asha_request_view");
        return vision is { HasDesktopMapping: true } &&
               capability is "desktop_interaction" or "visual_guidance"
            ? "required"
            : "auto";
    }

    private static object RequiredFunctionToolChoice(string name) => new
    {
        type = "function",
        function = new { name },
    };

    private static string DescribeToolChoice(object toolChoice)
    {
        if (toolChoice is string text) return text;
        try
        {
            var element = JsonSerializer.SerializeToElement(toolChoice);
            return element.TryGetProperty("function", out var function) &&
                   function.TryGetProperty("name", out var name)
                ? $"required:{name.GetString()}"
                : "structured";
        }
        catch
        {
            return "structured";
        }
    }

    private void ReportToolPhase(
        string phase,
        string? capability,
        IReadOnlyList<object>? tools,
        object toolChoice)
    {
        try
        {
            ModelToolPhaseMeasured?.Invoke(new ModelToolPhaseMeasurement(
                DateTime.UtcNow,
                phase,
                capability,
                ToolNames(tools),
                DescribeToolChoice(toolChoice)));
        }
        catch
        {
            // Diagnostics must never interrupt a conversation.
        }
    }

    private static string FreshEvidenceInstruction(ActivePerceptionGoal goal) => goal switch
    {
        ActivePerceptionGoal.Annotate =>
            "The runtime acquired fresh visual evidence for visible guidance. Use only a currently supplied guidance tool and only for a clearly established target; otherwise explain the uncertainty.",
        ActivePerceptionGoal.Act =>
            "The runtime acquired fresh visual evidence because the requested action needs a visible top-layer target. Use only a currently supplied action or capability tool.",
        ActivePerceptionGoal.Verify =>
            "The runtime acquired fresh evidence for verification. State only what this current evidence establishes.",
        _ =>
            "The runtime acquired fresh visual evidence because the person's meaning required current desktop context. Answer from it without inventing unseen details.",
    };

    internal static string ToolChoiceForTesting(string text, bool hasGroundedVision, bool allowComputerControl)
    {
        var plan = ActivePerceptionPlanner.Infer(text);
        var vision = hasGroundedVision
            ? new VisionAttachment("test", [], 0, 0, 100, 100, 100, 100)
            : null;
        var tools = hasGroundedVision ? SelectGroundedToolsForPlan(plan, allowComputerControl) : null;
        return ToolChoiceForPlan(plan, vision, allowComputerControl, tools);
    }

    internal static bool StaticPromptContainsToolNameForTesting() =>
        Regex.IsMatch(SystemPrompt, @"\basha_[a-z_]+\b", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(FreshEvidenceInstruction(ActivePerceptionGoal.Act), @"\basha_[a-z_]+\b", RegexOptions.CultureInvariant);

    internal static string InitialToolChoiceForTesting(string text)
    {
        var plan = ActivePerceptionPlanner.Infer(text);
        var tools = SelectInitialTools(
            plan,
            hasGroundedVision: false,
            allowApplicationControl: true,
            allowDesktopAction: true,
            canRequestVision: true,
            hasToolExecutor: true);
        return DescribeToolChoice(InitialToolChoice(plan, null, true, tools));
    }

    internal static string CapabilityPhaseToolChoiceForTesting(string capability, bool hasGroundedVision)
    {
        var tools = SelectToolsForCapability(
            capability,
            hasGroundedVision,
            allowApplicationControl: true,
            allowDesktopAction: true,
            canRequestVision: true);
        var vision = hasGroundedVision
            ? new VisionAttachment("test.png", [], 0, 0, 100, 100, 100, 100)
            : null;
        return DescribeToolChoice(ToolChoiceForCapabilityPhase(capability, vision, tools));
    }

    private static IReadOnlyList<object>? SelectInitialTools(
        ActivePerceptionPlan plan,
        bool hasGroundedVision,
        bool allowApplicationControl,
        bool allowDesktopAction,
        bool canRequestVision,
        bool hasToolExecutor)
    {
        var canNegotiateCapability = hasToolExecutor &&
            (canRequestVision || allowApplicationControl || allowDesktopAction);
        if (canNegotiateCapability &&
            (plan.Goal == ActivePerceptionGoal.None ||
             (plan.Goal == ActivePerceptionGoal.Act && allowApplicationControl)))
            return CapabilitySelectionToolDefinitions;

        if (hasGroundedVision && hasToolExecutor)
        {
            return SelectGroundedToolsForPlan(plan, allowDesktopAction);
        }

        var selected = new List<object>();
        if (canRequestVision && plan.RequiresFreshEvidence)
            selected.AddRange(ViewRequestToolDefinitions);
        if (hasToolExecutor && plan.Goal == ActivePerceptionGoal.Annotate)
            selected.AddRange(GuidanceManagementToolDefinitions);
        return selected.Count > 0 ? selected : null;
    }

    private static IReadOnlyList<object>? SelectToolsForCapability(
        string capability,
        bool hasGroundedVision,
        bool allowApplicationControl,
        bool allowDesktopAction,
        bool canRequestVision)
    {
        return capability switch
        {
            "desktop_observation" when hasGroundedVision => DetailAndViewToolDefinitions,
            "desktop_observation" when canRequestVision => ViewRequestToolDefinitions,
            "visual_guidance" when hasGroundedVision => VisualWithDetailToolDefinitions,
            "visual_guidance" when canRequestVision => ViewRequestToolDefinitions,
            "application_control" when allowApplicationControl => ApplicationControlToolDefinitions,
            "desktop_interaction" when hasGroundedVision && allowDesktopAction => DesktopActionWithDetailToolDefinitions,
            "desktop_interaction" when canRequestVision && allowDesktopAction => ViewRequestToolDefinitions,
            "guidance_management" => GuidanceManagementToolDefinitions,
            _ => null,
        };
    }

    internal static IReadOnlyList<string> GroundedToolNamesForTesting(string text, bool allowComputerControl)
    {
        var selected = SelectGroundedToolsForPlan(ActivePerceptionPlanner.Infer(text), allowComputerControl);
        return ToolNames(selected);
    }

    internal static IReadOnlyList<string> ModelRoutedGroundedToolNamesForTesting(string text, bool allowComputerControl) =>
        ToolNames(SelectGroundedToolsAfterModelView(ActivePerceptionPlanner.Infer(text), allowComputerControl));

    internal static IReadOnlyList<string> InitialToolNamesForTesting(string text, bool allowComputerControl)
    {
        var selected = SelectInitialTools(
            ActivePerceptionPlanner.Infer(text),
            hasGroundedVision: false,
            allowApplicationControl: allowComputerControl,
            allowDesktopAction: allowComputerControl,
            canRequestVision: true,
            hasToolExecutor: true);
        return ToolNames(selected);
    }

    internal static IReadOnlyList<string> InitialToolNamesForCapabilitiesForTesting(
        string text,
        bool allowApplicationControl,
        bool allowDesktopAction)
    {
        var selected = SelectInitialTools(
            ActivePerceptionPlanner.Infer(text),
            hasGroundedVision: false,
            allowApplicationControl,
            allowDesktopAction,
            canRequestVision: true,
            hasToolExecutor: true);
        return ToolNames(selected);
    }

    internal static IReadOnlyList<string> CapabilityToolNamesForTesting(
        string capability,
        bool hasGroundedVision,
        bool allowApplicationControl,
        bool allowDesktopAction)
    {
        var selected = SelectToolsForCapability(
            capability,
            hasGroundedVision,
            allowApplicationControl,
            allowDesktopAction,
            canRequestVision: true);
        return ToolNames(selected);
    }

    internal static int InitialToolSchemaCharactersForTesting(string text, bool allowComputerControl)
    {
        var tools = SelectInitialTools(
            ActivePerceptionPlanner.Infer(text),
            hasGroundedVision: false,
            allowApplicationControl: allowComputerControl,
            allowDesktopAction: allowComputerControl,
            canRequestVision: true,
            hasToolExecutor: true);
        return tools is null ? 0 : JsonSerializer.Serialize(tools).Length;
    }

    internal static int LegacyBroadToolSchemaCharactersForTesting() =>
        JsonSerializer.Serialize(ModelRoutedGroundedToolDefinitions).Length;

    internal static IReadOnlyList<string> GroundedInitialToolNamesForTesting(
        string text,
        bool allowApplicationControl,
        bool allowDesktopAction)
    {
        var selected = SelectInitialTools(
            ActivePerceptionPlanner.Infer(text),
            hasGroundedVision: true,
            allowApplicationControl,
            allowDesktopAction,
            canRequestVision: true,
            hasToolExecutor: true);
        return ToolNames(selected);
    }

    internal static bool IsDesktopTaskProgressToolForTesting(string name) =>
        IsDesktopTaskProgressTool(name);

    private static IReadOnlyList<string> ToolNames(IReadOnlyList<object>? selected)
    {
        if (selected is null) return [];
        return selected.Select(tool =>
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(tool));
            return document.RootElement.GetProperty("function").GetProperty("name").GetString() ?? string.Empty;
        }).ToArray();
    }

    private async Task<string?> CompleteVisualToolCallsAsync(
        List<object> messages,
        JsonElement message,
        VisionAttachment? vision,
        Func<AshaVisualToolCall, VisionAttachment?, CancellationToken, Task<string>>? visualToolExecutor,
        IReadOnlyList<object>? tools,
        string baseUrl,
        string model,
        CancellationToken cancellationToken,
        ISet<string>? executedToolNames = null,
        Func<string, VisionRequest, CancellationToken, Task<VisionAttachment?>>? visionResolver = null,
        string? userText = null,
        bool allowDesktopAction = false,
        int remainingDesktopSteps = 8,
        string? desktopTaskId = null)
    {
        desktopTaskId ??= $"desktop-task-{Guid.NewGuid():N}";
        var desktopTaskStep = Math.Max(1, 9 - remainingDesktopSteps);
        var toolCalls = ReadToolCalls(message);
        if (toolCalls.Count == 0 || visualToolExecutor is null) return ReadMessageContent(message);

        AppendAssistantToolCalls(messages, message, toolCalls);
        var outputs = new List<string>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            executedToolNames?.Add(toolCall.Name);
            string output;
            if (string.Equals(toolCall.Name, "asha_decline_action", StringComparison.Ordinal) ||
                string.Equals(toolCall.Name, "asha_decline_guidance", StringComparison.Ordinal))
            {
                var reason = toolCall.Arguments.TryGetProperty("reason", out var rawReason) && rawReason.ValueKind == JsonValueKind.String
                    ? rawReason.GetString() ?? "target_ambiguous"
                    : "target_ambiguous";
                output = JsonSerializer.Serialize(new { ok = true, declined = true, reason });
            }
            else
            {
                var executableCall = IsDesktopTaskProgressTool(toolCall.Name)
                    ? WithDesktopTaskMetadata(toolCall, desktopTaskId, desktopTaskStep)
                    : toolCall;
                try { output = await visualToolExecutor(executableCall, vision, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { output = JsonSerializer.Serialize(new { ok = false, error = error.Message }); }
            }
            outputs.Add(output);
            messages.Add(new { role = "tool", tool_call_id = toolCall.Id, content = output });
        }

        if (toolCalls.Count == 1 &&
            IsDesktopTaskProgressTool(toolCalls[0].Name) &&
            ToolResultSucceeded(outputs[0]) &&
            visionResolver is not null &&
            !string.IsNullOrWhiteSpace(userText))
        {
            var followUp = await visionResolver(
                userText,
                new VisionRequest(
                    VisionRequestKind.ModelRequested,
                    VisionRequestScope.ForegroundWindow,
                    PreferTextDetail: true),
                cancellationToken).ConfigureAwait(false);
            if (followUp is not null)
            {
                if (remainingDesktopSteps <= 0)
                    return string.Equals(toolCalls[0].Name, "asha_desktop_action", StringComparison.Ordinal)
                        ? RenderPostActionEvidenceResult(outputs[0], followUp)
                        : TryRenderToolResult(toolCalls[0], outputs[0], out var exhaustedResult)
                            ? exhaustedResult
                            : "I reached the desktop-action limit before I could verify the complete request.";

                messages.Add(new
                {
                    role = "system",
                    content = $"Continue the person's original desktop request as one bounded task. The runtime has completed one step and attached a fresh, versioned foreground state. Re-evaluate the remaining goal from this new state. If work remains, call exactly one appropriate tool. If the complete request is now established, answer only from current evidence. Do not narrate a click, open, selection, or other action unless a tool call in this task actually performed it. At most {remainingDesktopSteps} additional desktop step(s) remain.",
                });
                RemovePriorVisionMessages(messages);
                messages.Add(CreateUserMessage(
                    $"Continue and, when possible, complete the original request: {userText}",
                    followUp));

                var continuationPlan = ActivePerceptionPlanner.Infer(userText);
                var continuationTools = SelectGroundedToolsAfterModelView(
                    continuationPlan with { AllowCloserLook = true },
                    allowDesktopAction);
                ReportToolPhase("bounded_task_continuation", null, continuationTools, "auto");
                using var continuation = await SendChatCompletionAsync(
                    baseUrl,
                    model,
                    messages,
                    continuationTools,
                    cancellationToken,
                    toolChoice: "auto").ConfigureAwait(false);
                var continuationMessage = continuation.RootElement.GetProperty("choices")[0].GetProperty("message");
                var continuationCalls = ReadToolCalls(continuationMessage);
                if (continuationCalls.Count == 0)
                {
                    var finalText = ReadMessageContent(continuationMessage);
                    if (!ClaimsVisualActionSucceeded(finalText)) return finalText;
                    return string.Equals(toolCalls[0].Name, "asha_desktop_action", StringComparison.Ordinal)
                        ? RenderPostActionEvidenceResult(outputs[0], followUp)
                        : TryRenderToolResult(toolCalls[0], outputs[0], out var verifiedRuntimeResult)
                            ? verifiedRuntimeResult
                            : "I completed one verified desktop step, but I have not verified the entire requested outcome.";
                }

                var requestsAnotherView = continuationCalls.Any(call =>
                    string.Equals(call.Name, "asha_request_detail", StringComparison.Ordinal) ||
                    string.Equals(call.Name, "asha_request_view", StringComparison.Ordinal));
                return requestsAnotherView
                    ? await CompleteGroundedTurnAsync(
                        messages,
                        continuationMessage,
                        userText,
                        followUp,
                        visionResolver,
                        visualToolExecutor,
                        allowDesktopAction,
                        continuationPlan,
                        continuationTools,
                        baseUrl,
                        model,
                        cancellationToken,
                        executedToolNames,
                        remainingDesktopSteps - 1,
                        desktopTaskId).ConfigureAwait(false)
                    : await CompleteVisualToolCallsAsync(
                        messages,
                        continuationMessage,
                        followUp,
                        visualToolExecutor,
                        continuationTools,
                        baseUrl,
                        model,
                        cancellationToken,
                        executedToolNames,
                        visionResolver,
                        userText,
                        allowDesktopAction,
                        remainingDesktopSteps - 1,
                        desktopTaskId).ConfigureAwait(false);
            }
        }

        // A successful physical or visual action already has a definitive
        // runtime result. Rendering that result locally avoids spending a
        // second provider request merely to say that the action happened. It
        // also keeps ASHA responsive when the provider is near its rate limit.
        if (toolCalls.Count == 1 && TryRenderToolResult(toolCalls[0], outputs[0], out var rendered))
            return rendered;

        using var final = await SendChatCompletionAsync(baseUrl, model, messages, tools, cancellationToken).ConfigureAwait(false);
        return ReadMessageContent(final.RootElement.GetProperty("choices")[0].GetProperty("message"));
    }

    private static bool IsDesktopTaskProgressTool(string name) =>
        name is "asha_open_application" or "asha_open_folder" or "asha_desktop_action";

    private static AshaVisualToolCall WithDesktopTaskMetadata(
        AshaVisualToolCall call,
        string taskId,
        int taskStep)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in call.Arguments.EnumerateObject())
            values[property.Name] = property.Value.Clone();
        values["runtime_task_id"] = taskId;
        values["runtime_task_step"] = taskStep;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return call with { Arguments = document.RootElement.Clone() };
    }

    private static void RemovePriorVisionMessages(List<object> messages)
    {
        messages.RemoveAll(message =>
        {
            try
            {
                var serialized = JsonSerializer.SerializeToElement(message);
                if (!serialized.TryGetProperty("role", out var role) ||
                    !string.Equals(role.GetString(), "user", StringComparison.Ordinal) ||
                    !serialized.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                    return false;
                return content.EnumerateArray().Any(item =>
                    item.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "image_url", StringComparison.Ordinal));
            }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            {
                return false;
            }
        });
    }

    private static bool ToolResultSucceeded(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRenderToolResult(AshaVisualToolCall call, string output, out string rendered)
    {
        rendered = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(output);
            var result = document.RootElement;
            if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;

            if (!ok.GetBoolean())
            {
                var error = ReadResultString(result, "error");
                rendered = string.IsNullOrWhiteSpace(error)
                    ? "I couldn't complete that action."
                    : $"I couldn't complete that action. {error}";
                return true;
            }

            rendered = call.Name switch
            {
                "asha_mark" => RenderMarkConfirmation(result),
                "asha_clear_guidance" => RenderClearGuidanceConfirmation(result),
                "asha_open_application" => RenderApplicationConfirmation(result),
                "asha_open_folder" => "I've opened the folder for you.",
                "asha_desktop_action" => RenderDesktopActionConfirmation(result),
                "asha_decline_action" => RenderActionRefusal(result),
                "asha_decline_guidance" => RenderGuidanceRefusal(result),
                _ => string.Empty,
            };
            return rendered.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RenderMarkConfirmation(JsonElement result)
    {
        var label = ReadResultString(result, "label");
        return string.IsNullOrWhiteSpace(label)
            ? "I've highlighted it for you."
            : $"I've highlighted {label} for you.";
    }

    private static string RenderApplicationConfirmation(JsonElement result)
    {
        var application = ReadResultString(result, "application");
        return string.IsNullOrWhiteSpace(application)
            ? "I've opened the application and brought it to the front."
            : $"I've opened {application} and brought it to the front.";
    }

    private static string RenderClearGuidanceConfirmation(JsonElement result)
    {
        var removed = result.TryGetProperty("removed", out var rawRemoved) && rawRemoved.TryGetInt32(out var count) ? count : 0;
        if (removed == 0) return "I don't have any highlights to remove.";
        return string.Equals(ReadResultString(result, "scope"), "all", StringComparison.Ordinal)
            ? "I've removed my highlights."
            : "I've removed that highlight.";
    }

    private static string RenderDesktopActionConfirmation(JsonElement result) => ReadResultString(result, "action") switch
    {
        "move" => "I've moved the pointer there.",
        "click" => "I sent the click, but I haven't yet verified the intended screen result.",
        "double_click" => "I sent the double-click, but I haven't yet verified the intended screen result.",
        "right_click" => "I sent the right-click, but I haven't yet verified the intended screen result.",
        "drag" => "I sent the drag, but I haven't yet verified the intended screen result.",
        "scroll" => "I sent the scroll, but I haven't yet verified the intended screen result.",
        "type_text" => "I sent the text input, but I haven't yet verified the resulting field value.",
        "key" => "I sent the requested key, but I haven't yet verified the intended screen result.",
        _ => "I sent the requested desktop input, but I haven't yet verified the intended result.",
    };

    private static string RenderPostActionEvidenceResult(string output, VisionAttachment evidence)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var result = document.RootElement;
            var action = ReadResultString(result, "action");
            var targetName = ReadResultString(result, "target_name");
            var containerName = ReadResultString(result, "container_name");
            var targetDescription = string.IsNullOrWhiteSpace(targetName) ? "the grounded target" : targetName;

            if (string.Equals(action, "move", StringComparison.Ordinal))
                return $"I've moved the pointer to {targetDescription}.";

            var actionDescription = action switch
            {
                "click" => "click",
                "double_click" => "double-click",
                "right_click" => "right-click",
                "drag" => "drag",
                "scroll" => "scroll",
                "type_text" => "text input",
                "key" => "key input",
                _ => "desktop input",
            };
            var delivery = $"I sent the {actionDescription} to {targetDescription}.";
            if (EvidenceConfirmsTargetState(evidence.DesktopContext, targetName, containerName))
            {
                var relationship = string.IsNullOrWhiteSpace(containerName)
                    ? string.Empty
                    : $" under {containerName}";
                return $"{delivery} The fresh Windows state verifies that {targetDescription}{relationship} is now active.";
            }
            if (evidence.ChangedScore is >= 0.0015)
                return $"{delivery} The visible interface changed afterward, but I haven't independently established the whole requested outcome.";
            return $"{delivery} I couldn't verify a visible response, so I won't claim the requested outcome is complete.";
        }
        catch (JsonException)
        {
            return "I sent the desktop input, but I couldn't independently verify the intended result.";
        }
    }

    private static bool EvidenceConfirmsTargetState(
        string? desktopContext,
        string? targetName,
        string? containerName)
    {
        if (string.IsNullOrWhiteSpace(desktopContext) || string.IsNullOrWhiteSpace(targetName))
            return false;
        var target = NormalizeEvidenceTerm(targetName);
        var container = NormalizeEvidenceTerm(containerName);
        if (target.Length < 3) return false;

        foreach (var line in desktopContext.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeEvidenceTerm(line);
            if (!normalized.Contains(target, StringComparison.Ordinal)) continue;
            var stateEstablished =
                line.Contains(" selected", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("expand=expanded", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("window title", StringComparison.OrdinalIgnoreCase);
            if (!stateEstablished) continue;
            if (container.Length > 0 && !normalized.Contains(container, StringComparison.Ordinal))
                continue;
            return true;
        }
        return false;
    }

    private static string NormalizeEvidenceTerm(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string RenderActionRefusal(JsonElement result) => ReadResultString(result, "reason") switch
    {
        "target_not_visible" => "I can't see that target clearly enough, so I haven't acted on it.",
        "target_ambiguous" => "I can see more than one possible target, so I need you to clarify which one you mean.",
        "unsafe" => "I haven't done that because it would not be safe to perform without another confirmation.",
        "permission_required" => "I haven't done that because the required permission is not available.",
        "unsupported" => "I can't perform that particular desktop action yet.",
        _ => "I couldn't establish a safe, visible target, so I haven't acted on it.",
    };

    private static string RenderGuidanceRefusal(JsonElement result) => ReadResultString(result, "reason") switch
    {
        "target_not_visible" => "I can't see that target clearly enough in the current view, so I haven't placed a mark.",
        "target_ambiguous" => "I can see more than one possible target, so tell me which one you want me to mark.",
        "not_top_layer" => "That target is currently covered, so I haven't marked a misleading location.",
        _ => "I couldn't verify the requested target, so I haven't placed a mark.",
    };

    private static string? ReadResultString(JsonElement result, string propertyName) =>
        result.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    internal static bool ClaimsVisualActionSucceeded(string? reply) =>
        !string.IsNullOrWhiteSpace(reply) && Regex.IsMatch(
            reply,
            @"\b(?:i(?:'ve|\s+have)\s+(?:highlighted|marked|opened|launched|clicked|moved|removed|activated|focused|switched|brought|put)|i\s+(?:opened|launched|activated|focused|switched|brought|put)|i\s+sent\s+(?:the\s+|a\s+)?(?:click|double[- ]click|right[- ]click|drag|scroll|key|input)|ich\s+habe\s+(?:markiert|geöffnet|geklickt|verschoben|entfernt|aktiviert|fokussiert|nach\s+vorne\s+gebracht)|ich\s+habe\s+(?:den|die|das|einen|eine)?\s*(?:klick|doppelklick|rechtsklick|eingabe)\s+gesendet)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool TryExtractGuidanceClearRequest(string text, out string scope)
    {
        scope = "latest";
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Treat this as intent detection rather than a fixed command phrase:
        // removal language and a visual-guidance noun may occur in either
        // order, with ordinary conversational words between them.
        var asksToRemove = Regex.IsMatch(
            text,
            @"\b(?:remove|clear|delete|erase|dismiss|hide|take\b.{0,40}\baway|get\s+rid\s+of|entfern(?:e|en|t)?|lösch(?:e|en|t)?|loesch(?:e|en|t)?|wegmach(?:en|e|st|t)?|nimm\b.{0,40}\bweg)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!asksToRemove) return false;

        var namesGuidance = Regex.IsMatch(
            text,
            @"\b(?:mark|marks|marker|markers|highlight|highlights|cue|cues|annotation|annotations|box|boxes|circle|circles|dot|dots|arrow|arrows|label|labels|guidance|overlay|overlays|markierung|markierungen|hinweis|hinweise|rahmen|kästchen|kaestchen|kreis|kreise|punkt|punkte|pfeil|pfeile|beschriftung|beschriftungen)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!namesGuidance) return false;

        var asksForAll = Regex.IsMatch(
            text,
            @"\b(?:all|every|your|yours|asha(?:'s)?|alle|alles|sämtliche|saemtliche|deine|deiner|deinem|deinen|deines|euren|eure)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        scope = asksForAll ? "all" : "latest";
        return true;
    }

    private static void AppendAssistantToolCalls(List<object> messages, JsonElement message, IReadOnlyList<AshaVisualToolCall> toolCalls)
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
    }

    private async Task<JsonDocument> SendChatCompletionAsync(
        string baseUrl,
        string model,
        List<object> messages,
        IReadOnlyList<object>? tools,
        CancellationToken cancellationToken,
        int maxTokens = 420,
        object? toolChoice = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0.55,
            ["max_completion_tokens"] = maxTokens,
        };
        if (tools is not null)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = toolChoice ?? "auto";
        }

        // Qwen 3.6 otherwise exposes its internal thinking as spoken text. ASHA's
        // normal voice mode needs a fast, concise final answer instead.
        if (string.Equals(model, "qwen/qwen3.6-27b", StringComparison.OrdinalIgnoreCase))
            payload["reasoning_effort"] = "none";

        // Serialize once. Key rotation can replay the exact same safe payload
        // without re-encoding a large screenshot and tool schema per key.
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var measurement = MeasureRequest(model, payloadBytes);

        // A visual/tool turn often needs more than the old six-second window,
        // especially while Groq is warming a model. Bound the logical request
        // as well as each key attempt so rotation cannot leave the orb hanging
        // for a minute when the network is unavailable.
        var attemptTimeout = maxTokens > 420 ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(10);
        var totalBudget = maxTokens > 420 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(22);
        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestBudget.CancelAfter(totalBudget);
        HttpResponseMessage response;
        try
        {
            response = await _groqKeys.SendAsync(async (key, attemptCancellation) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
                {
                    Content = new ByteArrayContent(payloadBytes),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                return await _http.SendAsync(request, attemptCancellation).ConfigureAwait(false);
            }, attemptTimeout, requestBudget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The model did not return this ASHA turn within {totalBudget.TotalSeconds:0} seconds.", error);
        }
        using (response)
        {
            await EnsureSuccessAsync(response, "Groq", cancellationToken).ConfigureAwait(false);
            var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            try
            {
                ModelRequestMeasured?.Invoke(measurement with
                {
                    PromptTokens = ReadUsageToken(document.RootElement, "prompt_tokens"),
                    CompletionTokens = ReadUsageToken(document.RootElement, "completion_tokens"),
                });
            }
            catch
            {
                // Diagnostics must never break a model turn.
            }
            return document;
        }
    }

    private static ModelRequestMeasurement MeasureRequest(
        string model,
        byte[] serializedPayload)
    {
        var messageTextCharacters = 0;
        var imageCount = 0;
        var imagePayloadCharacters = 0;
        using var document = JsonDocument.Parse(serializedPayload);
        var root = document.RootElement;
        if (root.TryGetProperty("messages", out var messages)) Visit(messages);
        var toolSchemaCharacters = root.TryGetProperty("tools", out var tools)
            ? tools.GetRawText().Length
            : 0;
        return new ModelRequestMeasurement(
            DateTime.UtcNow,
            model,
            messageTextCharacters,
            toolSchemaCharacters,
            imageCount,
            imagePayloadCharacters,
            null,
            null);

        void Visit(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject()) Visit(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Visit(item);
                    break;
                case JsonValueKind.String:
                    var value = element.GetString() ?? string.Empty;
                    if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        imageCount++;
                        imagePayloadCharacters += value.Length;
                    }
                    else
                    {
                        messageTextCharacters += value.Length;
                    }
                    break;
            }
        }
    }

    private static int? ReadUsageToken(JsonElement root, string name)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var tokens))
            return null;
        return tokens;
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

    private static VisionRequest ReadVisionRequest(AshaVisualToolCall call)
    {
        if (!call.Arguments.TryGetProperty("scope", out var rawValue) || rawValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(rawValue.GetString()))
            return new VisionRequest(VisionRequestKind.ModelRequested, VisionRequestScope.PointerArea);
        var rawScope = rawValue.GetString()!.Trim();
        var scope = rawScope switch
        {
            "foreground_window" => VisionRequestScope.ForegroundWindow,
            "entire_desktop" => VisionRequestScope.EntireDesktop,
            "left_screen" => VisionRequestScope.LeftScreen,
            "right_screen" => VisionRequestScope.RightScreen,
            "upper_screen" => VisionRequestScope.UpperScreen,
            "lower_screen" => VisionRequestScope.LowerScreen,
            "upper_left_screen" => VisionRequestScope.UpperLeftScreen,
            "upper_right_screen" => VisionRequestScope.UpperRightScreen,
            "lower_left_screen" => VisionRequestScope.LowerLeftScreen,
            "lower_right_screen" => VisionRequestScope.LowerRightScreen,
            _ => VisionRequestScope.PointerArea,
        };
        return new VisionRequest(VisionRequestKind.ModelRequested, scope);
    }

    private static readonly IReadOnlyList<object> ViewRequestToolDefinitions =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "asha_request_view",
                description = "Request one fresh permitted view. Choose the smallest useful independent scope; use pointer_area only for an explicit pointer reference.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "reason" },
                    properties = new
                    {
                        reason = new { type = "string", description = "Short reason for this view." },
                        scope = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "pointer_area", "foreground_window", "entire_desktop", "left_screen", "right_screen",
                                "upper_screen", "lower_screen", "upper_left_screen", "upper_right_screen",
                                "lower_left_screen", "lower_right_screen",
                            },
                            description = "Smallest useful view; all scopes except pointer_area are independent of the mouse.",
                        },
                    },
                },
            },
        },
    ];

    private static readonly object DetailViewToolDefinition = new
    {
        type = "function",
        function = new
        {
            name = "asha_request_detail",
            description = "Request one higher-detail crop inside the supplied overview when a target is too small to verify.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "x", "y", "w", "h", "reason" },
                properties = new
                {
                    x = new { type = "number", description = "Left in supplied-image pixels." },
                    y = new { type = "number", description = "Top in supplied-image pixels." },
                    w = new { type = "number", description = "Width in supplied-image pixels." },
                    h = new { type = "number", description = "Height in supplied-image pixels." },
                    reason = new { type = "string", description = "Short reason for detail." },
                },
            },
        },
    };

    private static readonly object ClearGuidanceToolDefinition = new
    {
        type = "function",
        function = new
        {
            name = "asha_clear_guidance",
            description = "Remove only visual highlights or marks previously created by ASHA for human guidance. Never removes person-created teaching cues, session history, evidence, or the computer-control presence frame.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "scope" },
                properties = new
                {
                    scope = new
                    {
                        type = "string",
                        @enum = new[] { "latest", "all" },
                        description = "Use latest for a singular request such as remove that mark; use all for remove your marks or clear your highlights.",
                    },
                },
            },
        },
    };

    private static readonly IReadOnlyList<object> GuidanceManagementToolDefinitions =
        [ClearGuidanceToolDefinition];

    private static readonly object CapabilitySelectionToolDefinition = new
    {
        type = "function",
        function = new
        {
            name = "asha_choose_capability",
            description = "Select one ASHA capability only when the person's current request needs a desktop tool. This selects a tool contract; it does not perform or claim an action.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "capability", "reason" },
                properties = new
                {
                    capability = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "desktop_observation",
                            "visual_guidance",
                            "application_control",
                            "desktop_interaction",
                            "guidance_management",
                        },
                        description = "desktop_observation reads or locates; visual_guidance shows a mark; application_control only launches/foregrounds an installed app or opens a folder; desktop_interaction operates controls inside an app; guidance_management removes ASHA guidance.",
                    },
                    reason = new { type = "string", description = "One short sentence connecting the person's request to this capability." },
                },
            },
        },
    };

    private static readonly IReadOnlyList<object> CapabilitySelectionToolDefinitions =
        [CapabilitySelectionToolDefinition];

    private static readonly object VisualGuidanceRefusalToolDefinition = new
    {
        type = "function",
        function = new
        {
            name = "asha_decline_guidance",
            description = "Return a structured truthful result when an explicitly requested mark cannot be grounded to one visible top-layer target. Never use this after placing a mark.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "reason" },
                properties = new
                {
                    reason = new
                    {
                        type = "string",
                        @enum = new[] { "target_not_visible", "target_ambiguous", "not_top_layer" },
                    },
                },
            },
        },
    };

    private static readonly IReadOnlyList<object> VisualToolDefinitions =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "asha_mark",
                description = "Place one click-through overlay on a verified top-layer target in the supplied image. Prefer its interactive text-bearing control, not a nearby icon or incidental duplicate text.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "kind", "x" },
                    properties = new
                    {
                        kind = new { type = "string", @enum = new[] { "dot", "circle", "box", "arrow", "label" } },
                        x = DesktopCoordinateParameter("Target x in supplied-image pixels; left edge for a box."),
                        y = DesktopCoordinateParameter("Target y in supplied-image pixels; top edge for a box."),
                        w = new { type = "number", description = "Box width or signed arrow x distance." },
                        h = new { type = "number", description = "Box height or signed arrow y distance." },
                        label = new { type = "string", description = "Short human-facing label." },
                        visible_text = new { type = "string", description = "Exact words printed inside a text target." },
                        expected_app = new { type = "string", description = "Visible host app/window for top-layer verification." },
                        target_type = new { type = "string", @enum = new[] { "text", "visual" }, description = "text requires local OCR; visual is genuinely non-textual." },
                        color = new { type = "string", description = "Optional hex color." },
                    },
                },
            },
        },
        VisualGuidanceRefusalToolDefinition,
        ClearGuidanceToolDefinition,
    ];

    private static readonly IReadOnlyList<object> VisualWithDetailToolDefinitions =
        VisualToolDefinitions.Concat([DetailViewToolDefinition]).Concat(ViewRequestToolDefinitions).ToArray();

    private static readonly IReadOnlyList<object> DetailAndViewToolDefinitions =
        new[] { DetailViewToolDefinition }.Concat(ViewRequestToolDefinitions).ToArray();

    private static readonly IReadOnlyList<object> ApplicationControlToolDefinitions =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "asha_open_application",
                description = "Open or foreground one installed application by ordinary display name. No path, command, URL, or argument.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "application" },
                    properties = new
                    {
                        application = new { type = "string", description = "Ordinary installed display name only." },
                    },
                },
            },
        },
        new
        {
            type = "function",
            function = new
            {
                name = "asha_open_folder",
                description = "Open one existing non-system folder in Explorer. No command or URL.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "folder" },
                    properties = new
                    {
                        folder = new { type = "string", description = "Existing folder display name or normal absolute local path." },
                    },
                },
            },
        },
    ];

    private static readonly object DesktopActionToolDefinition = new
    {
        type = "function",
        function = new
        {
            name = "asha_desktop_action",
            description = "Perform one permitted desktop input on a target grounded in the current image and UI snapshot.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "action" },
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "move", "click", "double_click", "right_click", "drag", "scroll", "type_text", "key" } },
                    x = DesktopCoordinateParameter("Target x in supplied-image pixels."),
                    y = DesktopCoordinateParameter("Target y in supplied-image pixels."),
                    end_x = DesktopCoordinateParameter("Drag destination x in supplied-image pixels."),
                    end_y = DesktopCoordinateParameter("Drag destination y in supplied-image pixels."),
                    delta = new { type = "number", description = "Wheel delta, between minus 1200 and 1200." },
                    text = new { type = "string", description = "Short non-sensitive text for the currently focused field." },
                    key = new { type = "string", @enum = new[] { "enter", "escape", "tab", "space", "backspace", "up", "down", "left", "right" } },
                    expected_app = new { type = "string", description = "Expected visible host app/window." },
                    target_name = new
                    {
                        type = "string",
                        description = "Exact visible text or accessible name. Required for text-bearing pointer targets.",
                    },
                    target_role = new
                    {
                        type = "string",
                        description = "Semantic role such as button, tab, account, folder, list_item, text_field, or icon.",
                    },
                    container_name = new
                    {
                        type = "string",
                        description = "Exact parent/account/panel name when the target label can repeat.",
                    },
                    target_type = new
                    {
                        type = "string",
                        @enum = new[] { "text", "non_text_visual", "focused_control" },
                        description = "text for named targets; non_text_visual only if unlabeled; focused_control only for keyboard input.",
                    },
                    expected_change = new
                    {
                        type = "string",
                        description = "Short observable postcondition that would prove success.",
                    },
                    source_snapshot_id = new
                    {
                        type = "string",
                        description = "Current supplied UI snapshot ID; stale IDs are rejected.",
                    },
                    executor_preference = new
                    {
                        type = "string",
                        @enum = new[] { "automatic", "background", "physical" },
                        description = "background for non-interference, physical only when explicitly requested, otherwise automatic.",
                    },
                },
            },
        },
    };

    private static readonly object DesktopActionRefusalToolDefinition = new
    {
        type = "function",
        function = new
        {
            name = "asha_decline_action",
            description = "Decline when one safe current target cannot be grounded.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "reason" },
                properties = new
                {
                    reason = new
                    {
                        type = "string",
                        @enum = new[] { "target_not_visible", "target_ambiguous", "unsafe", "permission_required", "unsupported" },
                    },
                },
            },
        },
    };

    private static readonly IReadOnlyList<object> DesktopActionToolDefinitions =
        [DesktopActionToolDefinition, DesktopActionRefusalToolDefinition];

    private static readonly IReadOnlyList<object> DesktopActionWithDetailToolDefinitions =
        [DesktopActionToolDefinition, DesktopActionRefusalToolDefinition, DetailViewToolDefinition, .. ViewRequestToolDefinitions];

    private static readonly IReadOnlyList<object> ModelRoutedGroundedToolDefinitions =
        [DesktopActionToolDefinition, DesktopActionRefusalToolDefinition, DetailViewToolDefinition, .. ViewRequestToolDefinitions, .. VisualToolDefinitions];

    private static object DesktopCoordinateParameter(string description) => new
    {
        oneOf = new object[]
        {
            new { type = "number" },
            new { type = "array", minItems = 1, maxItems = 4, items = new { type = "number" } },
        },
        description = $"{description} Prefer a scalar; a point or box array is accepted and normalized to its centre.",
    };

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
        if (string.Equals(service, "Groq", StringComparison.Ordinal))
            throw new GroqRequestException((int)response.StatusCode, detail);
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

internal sealed class GroqRequestException(int statusCode, string detail)
    : InvalidOperationException($"Groq failed ({statusCode}): {detail}")
{
    public int StatusCode { get; } = statusCode;
}

public sealed record VoiceTurnResult(string Transcript, string Reply);
public enum VisionRequestKind { PersonSelected, ModelRequested }
public enum VisionRequestScope
{
    PointerArea,
    ForegroundWindow,
    EntireDesktop,
    LeftScreen,
    RightScreen,
    UpperScreen,
    LowerScreen,
    UpperLeftScreen,
    UpperRightScreen,
    LowerLeftScreen,
    LowerRightScreen,
    Region,
}
public sealed record VisionRequest(
    VisionRequestKind Kind,
    VisionRequestScope Scope,
    DesktopCaptureRegion? Region = null,
    bool PreferTextDetail = false);
public sealed record DesktopAwarenessContext(DateTime ObservedAtUtc, string Summary, double ChangedScore);
public sealed record VisionAttachment(
    string Name,
    byte[] Bytes,
    int? ContextX,
    int? ContextY,
    int? ContextWidth,
    int? ContextHeight,
    int? PixelWidth = null,
    int? PixelHeight = null,
    string? DesktopContext = null,
    double? ChangedScore = null,
    string? DesktopSnapshotId = null,
    string? DesktopSnapshotSignature = null)
{
    public string DataUrl =>
        $"data:{(Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png")};base64,{Convert.ToBase64String(Bytes)}";
    public bool HasDesktopMapping => ContextX.HasValue && ContextY.HasValue && ContextWidth.GetValueOrDefault() > 0 && ContextHeight.GetValueOrDefault() > 0;
    public int ImageWidth => PixelWidth.GetValueOrDefault(ContextWidth.GetValueOrDefault());
    public int ImageHeight => PixelHeight.GetValueOrDefault(ContextHeight.GetValueOrDefault());
    public string CoordinateInstruction => HasDesktopMapping
        ? $"The supplied image is {ImageWidth} by {ImageHeight} pixels. For any coordinate fields in a currently available tool, use only supplied-image pixels. Never copy desktop coordinates into an image-coordinate field; ASHA maps image coordinates internally. {DesktopContext}".Trim()
        : $"The image does not include a reliable desktop coordinate map, so do not use a visual marking tool. {DesktopContext}".Trim();

    public bool TryMapImagePoint(int imageX, int imageY, out int desktopX, out int desktopY)
    {
        desktopX = 0;
        desktopY = 0;
        if (!HasDesktopMapping || ImageWidth <= 0 || ImageHeight <= 0 ||
            imageX < 0 || imageX > ImageWidth || imageY < 0 || imageY > ImageHeight)
            return false;
        desktopX = ContextX!.Value + (int)Math.Round(imageX * ContextWidth!.Value / (double)ImageWidth);
        desktopY = ContextY!.Value + (int)Math.Round(imageY * ContextHeight!.Value / (double)ImageHeight);
        return true;
    }

    public bool TryNormalizeToolPoint(
        int suppliedX,
        int suppliedY,
        out int imageX,
        out int imageY,
        out int desktopX,
        out int desktopY,
        out string coordinateSource)
    {
        imageX = suppliedX;
        imageY = suppliedY;
        coordinateSource = "supplied_image_pixels";
        if (TryMapImagePoint(imageX, imageY, out desktopX, out desktopY))
            return true;

        desktopX = desktopY = 0;
        if (!HasDesktopMapping ||
            suppliedX < ContextX!.Value ||
            suppliedX > ContextX.Value + ContextWidth!.Value ||
            suppliedY < ContextY!.Value ||
            suppliedY > ContextY.Value + ContextHeight!.Value)
            return false;

        imageX = Math.Clamp(
            (int)Math.Round((suppliedX - ContextX.Value) * ImageWidth / (double)ContextWidth.Value),
            0,
            ImageWidth);
        imageY = Math.Clamp(
            (int)Math.Round((suppliedY - ContextY.Value) * ImageHeight / (double)ContextHeight.Value),
            0,
            ImageHeight);
        coordinateSource = "desktop_pixels_normalized_for_compatibility";
        return TryMapImagePoint(imageX, imageY, out desktopX, out desktopY);
    }

    public int MapImageWidth(int imageWidth) =>
        (int)Math.Round(imageWidth * ContextWidth!.Value / (double)ImageWidth);

    public int MapImageHeight(int imageHeight) =>
        (int)Math.Round(imageHeight * ContextHeight!.Value / (double)ImageHeight);
}

public sealed record AshaVisualToolCall(string Id, string Name, JsonElement Arguments);
