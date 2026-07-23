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
        you that happened. A timestamped live-awareness summary supplied by
        the runtime is genuine recent visual context, but it may be less exact
        than a fresh image; speak with appropriate confidence.

        If the runtime supplies an image, it is one deliberately selected,
        recent piece of local desktop evidence — not a live feed and not proof
        of anything beyond what is visible in that image. Treat it as your
        current shared visual context: use it to answer the person directly,
        mention the visible application or target when helpful, and say when
        you are uncertain. Do not say that you cannot see the screen when an
        image is supplied.

        When the runtime offers asha_request_view but has not supplied an
        image, use that tool whenever the person's request genuinely depends
        on what is currently visible. This includes naturally phrased requests
        to look, identify, read, compare, locate, demonstrate, or act on the
        desktop. Do not guess and do not claim sight before the runtime returns
        a view. Do not request a view for ordinary conversation that does not
        need the desktop.

        ASHA uses active perception rather than treating screenshots as a
        command phrase. A current view may be supplied because the meaning of
        the person's request requires visual evidence, even when they never
        said screenshot. Start with the supplied overview. If an exact target
        is too small to read or locate reliably and asha_request_detail is
        available, request one closer view of the relevant region before
        marking or acting. Never approximate a tiny target from an overview.

        Choose the smallest useful view scope. Use pointer area only when the
        person explicitly refers to their mouse, pointer, cursor, or the area
        immediately around it as a location. A request to use the mouse for an
        action does not mean the target is currently near the pointer. Treat
        the pointer as one useful salience hint, never as a camera constraint.
        When they ask you to locate, highlight, or act on a named control in
        the active application, use foreground window so its interface text
        remains readable even if the pointer is elsewhere.
        Half-screen and quadrant scopes look independently at the requested
        part of the current monitor. Use entire desktop only for a broad
        overview or for locating an application or object whose region is not
        yet known. Do not ask the person to move the mouse merely so you can
        look somewhere else. When a supplied view does not contain the target,
        use asha_request_view to relocate once to an independent screen scope.
        From a broad overview, use asha_request_detail to place one arbitrary
        higher-detail capture box around the promising region.

        When the runtime gives you the safe asha_mark tool together with a
        coordinate-mapped image, you may use it once to point out a visible
        target. It creates a visual overlay only; it never moves the person's
        mouse, clicks, types, or changes their computer. Use it only when the
        target is genuinely visible and you can identify it with care. When a
        person explicitly asks where something is, asks you to show them, or
        asks which visible button to use, prefer one helpful asha_mark before
        you answer in words. If the person names visible text, the exact
        text-bearing label, row, or button outranks any nearby icon. Before
        placing the mark, verify that the requested words themselves are
        inside the proposed mark. If they are not, correct the target; if the
        target remains uncertain, ask rather than marking a plausible-looking
        neighbour. When the requested target is an interface control, choose
        the interactive button, navigation row, menu item, field, or tab — not
        the same words appearing incidentally in a log, terminal, chat,
        document body, tooltip, or an existing ASHA annotation. Never mark an
        application or window that is hidden, covered, or absent from the
        supplied image. If it is not visibly exposed at the top layer, say so
        or offer to bring it forward instead of inventing a location. Supply
        expected_app whenever the target belongs to a visible application. The
        label is a short human-facing description and need not be literal UI
        text. Set visible_text separately to the exact words printed inside the
        proposed target box. Set target_type to text when those visible words
        identify the target and must be confirmed by local OCR. Use visual only
        for a genuinely non-textual target. If asha_decline_guidance is
        available and the target cannot be verified, use it rather than merely
        describing a location or pretending that a mark was placed.

        When the runtime supplies asha_clear_guidance, use it when the person
        asks you to remove a highlight or mark that you created. Use latest for
        a singular or demonstrative request such as remove that mark, and all
        when they ask to remove your marks or clear your highlights. This tool
        never removes the person's teaching cues, session history, evidence,
        or computer-control presence.

        If the runtime supplies asha_open_application, the person has enabled
        computer control. Interpret the person's unrestricted natural wording
        and conversation context, then use the tool when they directly ask you
        to open or bring forward an installed application. Resolve references
        such as "it" from the conversation. Supply only the application's
        ordinary installed display name, without request words or politeness;
        never supply a path, command, argument, or guessed executable. A word
        following open is not automatically an application. Mailboxes,
        messages, documents, tabs, settings, projects, and other destinations
        inside an application must not be passed to asha_open_application;
        request a current view and ground the visible interaction instead. The
        runtime will verify that a visible application window appeared. Never
        say that you opened, launched, activated, or brought forward an
        application unless this tool returned a successful result in the
        current turn. A similar sentence in conversation history is not proof.

        If the runtime supplies asha_open_folder, use it only when the person
        directly asks to open an existing ordinary folder. Supply its spoken
        name or local path, never a URL or command. This is a read-only open;
        do not imply that files were changed.

        If the runtime additionally supplies asha_desktop_action, the person
        has explicitly enabled a visible control session. Use it only for one
        concrete action the person directly requested, only on a target you
        can see in the supplied image, and only when its effect is appropriate
        and low-risk. Never type passwords, secrets, payment details, recovery
        codes, or other credentials. If asha_decline_action is available and
        the target is absent, ambiguous, unsafe, or unsupported, use that
        structured refusal instead of narrating that the action happened or
        guessing a coordinate. Supply expected_app for a visible application
        target so the runtime can reject a stale or covered surface. The
        runtime will visibly move the physical mouse or type; do not claim
        success beyond the input that the runtime confirms.
        """;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly GroqKeyRotator _groqKeys = GroqKeyRotator.LoadDefault();
    private readonly List<ChatTurn> _history = [];
    private string _sessionSummary = string.Empty;
    private bool _disposed;

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
        var perceptionPlan = ActivePerceptionPlanner.Infer(transcript);
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
        var allowDesktopAction = controlAccess.CanUseKeyboard || controlAccess.CanUsePhysicalCursor;
        var perceptionPlan = ActivePerceptionPlanner.Infer(userText);
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
                content = controlAccess.DescribeForModel(virtualInteractionConnected: false),
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
                content = perceptionPlan.Goal switch
                {
                    ActivePerceptionGoal.Annotate => "The runtime acquired fresh visual evidence because the person asked for visible guidance. If the target is clearly present, use asha_mark. If it is not established by the image, say that naturally instead of claiming a mark was shown.",
                    ActivePerceptionGoal.Act => "The runtime acquired fresh visual evidence because the requested physical action needs a visible target. Use asha_desktop_action only when that target is clearly exposed at the top layer.",
                    ActivePerceptionGoal.Verify => "The runtime acquired fresh evidence for verification. State only what this current evidence establishes.",
                    _ => "The runtime acquired fresh visual evidence because the person's meaning required current desktop context. Answer from it without inventing unseen details.",
                },
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
        string? reply;
        using (var first = await SendChatCompletionAsync(
                   baseUrl, model, messages, tools, cancellationToken,
                   toolChoice: ToolChoiceForPlan(perceptionPlan, vision, allowDesktopAction, tools)).ConfigureAwait(false))
        {
            var message = first.RootElement.GetProperty("choices")[0].GetProperty("message");
            var toolCalls = ReadToolCalls(message);
            var viewRequests = toolCalls.Where(call => string.Equals(call.Name, "asha_request_view", StringComparison.Ordinal)).ToArray();
            if (vision is null && viewRequests.Length > 0 && visionResolver is not null)
            {
                vision = await visionResolver(userText, ReadVisionRequest(viewRequests[0]), cancellationToken).ConfigureAwait(false);
                if (vision is not null)
                {
                    messages[^1] = CreateUserMessage(userText, vision);
                    tools = vision.HasDesktopMapping && visualToolExecutor is not null
                        ? SelectGroundedToolsAfterModelView(perceptionPlan, allowDesktopAction)
                        : null;
                    using var grounded = await SendChatCompletionAsync(
                        baseUrl, model, messages, tools, cancellationToken,
                        toolChoice: ToolChoiceForPlan(perceptionPlan, vision, allowDesktopAction, tools)).ConfigureAwait(false);
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
                        cancellationToken, executedToolNames).ConfigureAwait(false);
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
        ISet<string>? executedToolNames)
    {
        var toolCalls = ReadToolCalls(message);
        var captureCall = toolCalls.FirstOrDefault(call =>
            string.Equals(call.Name, "asha_request_detail", StringComparison.Ordinal) ||
            string.Equals(call.Name, "asha_request_view", StringComparison.Ordinal));
        if (captureCall is null || visionResolver is null)
            return await CompleteVisualToolCallsAsync(
                messages, message, vision, visualToolExecutor, tools, baseUrl, model,
                cancellationToken, executedToolNames, visionResolver, userText).ConfigureAwait(false);

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

        messages.Add(CreateUserMessage(
            $"Use this fresh model-selected view to complete the person's original request: {userText}",
            refreshedView));
        var detailTools = SelectGroundedToolsForPlan(
            perceptionPlan with { AllowCloserLook = false },
            allowDesktopAction) ?? SelectGroundedToolsAfterModelView(
                perceptionPlan with { AllowCloserLook = false },
                allowDesktopAction);
        using var refined = await SendChatCompletionAsync(
            baseUrl, model, messages, detailTools, cancellationToken,
            toolChoice: ToolChoiceForPlan(perceptionPlan, refreshedView, allowDesktopAction, detailTools)).ConfigureAwait(false);
        var refinedMessage = refined.RootElement.GetProperty("choices")[0].GetProperty("message");
        return await CompleteVisualToolCallsAsync(
            messages, refinedMessage, refreshedView, visualToolExecutor, detailTools, baseUrl, model,
            cancellationToken, executedToolNames, visionResolver, userText).ConfigureAwait(false);
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
        (allowDesktopAction ? ModelRoutedGroundedToolDefinitions : VisualWithDetailToolDefinitions);

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

    internal static string ToolChoiceForTesting(string text, bool hasGroundedVision, bool allowComputerControl)
    {
        var plan = ActivePerceptionPlanner.Infer(text);
        var vision = hasGroundedVision
            ? new VisionAttachment("test", [], 0, 0, 100, 100, 100, 100)
            : null;
        var tools = hasGroundedVision ? SelectGroundedToolsForPlan(plan, allowComputerControl) : null;
        return ToolChoiceForPlan(plan, vision, allowComputerControl, tools);
    }

    private static IReadOnlyList<object>? SelectInitialTools(
        ActivePerceptionPlan plan,
        bool hasGroundedVision,
        bool allowApplicationControl,
        bool allowDesktopAction,
        bool canRequestVision,
        bool hasToolExecutor)
    {
        if (hasGroundedVision && hasToolExecutor)
            return SelectGroundedToolsForPlan(plan, allowDesktopAction);

        var selected = new List<object>();
        if (canRequestVision) selected.AddRange(ViewRequestToolDefinitions);
        if (allowApplicationControl && hasToolExecutor) selected.AddRange(ApplicationControlToolDefinitions);
        if (hasToolExecutor) selected.AddRange(GuidanceManagementToolDefinitions);
        return selected.Count > 0 ? selected : null;
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
        string? userText = null)
    {
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
                try { output = await visualToolExecutor(toolCall, vision, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { output = JsonSerializer.Serialize(new { ok = false, error = error.Message }); }
            }
            outputs.Add(output);
            messages.Add(new { role = "tool", tool_call_id = toolCall.Id, content = output });
        }

        if (toolCalls.Count == 1 &&
            string.Equals(toolCalls[0].Name, "asha_desktop_action", StringComparison.Ordinal) &&
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
                messages.Add(new
                {
                    role = "system",
                    content = "The runtime has now sent the requested physical input and attached fresh post-action evidence. Verify only what visibly changed or what this evidence establishes. If the intended result is unclear, say so naturally; do not infer success merely from the input event.",
                });
                messages.Add(CreateUserMessage(
                    $"Check the result of the person's original request: {userText}",
                    followUp));
                using var verified = await SendChatCompletionAsync(baseUrl, model, messages, null, cancellationToken).ConfigureAwait(false);
                return ReadMessageContent(verified.RootElement.GetProperty("choices")[0].GetProperty("message"));
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
        "click" => "I've clicked there.",
        "double_click" => "I've double-clicked there.",
        "right_click" => "I've right-clicked there.",
        "drag" => "I've dragged it there.",
        "scroll" => "I've scrolled there.",
        "type_text" => "I've entered that text.",
        "key" => "I've pressed the requested key.",
        _ => "I've sent the requested desktop input.",
    };

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
            @"\b(?:i(?:'ve|\s+have)\s+(?:highlighted|marked|opened|launched|clicked|moved|removed|activated|focused|switched|brought|put)|i\s+(?:opened|launched|activated|focused|switched|brought|put)|ich\s+habe\s+(?:markiert|geöffnet|geklickt|verschoben|entfernt|aktiviert|fokussiert|nach\s+vorne\s+gebracht))\b",
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
        string toolChoice = "auto")
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
            payload["tool_choice"] = toolChoice;
        }

        // Qwen 3.6 otherwise exposes its internal thinking as spoken text. ASHA's
        // normal voice mode needs a fast, concise final answer instead.
        if (string.Equals(model, "qwen/qwen3.6-27b", StringComparison.OrdinalIgnoreCase))
            payload["reasoning_effort"] = "none";

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
                    Content = JsonContent.Create(payload),
                };
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
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
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
                description = "Request one fresh, permission-gated desktop view when the current evidence does not contain the target. This may relocate independently of the pointer to the foreground, full desktop, a screen half, or a quadrant. Use pointer_area only when the person explicitly identifies their pointer location. This never clicks, types, or controls the computer.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "reason" },
                    properties = new
                    {
                        reason = new { type = "string", description = "A short human-facing reason the current view is needed." },
                        scope = new
                        {
                            type = "string",
                            @enum = new[]
                            {
                                "pointer_area", "foreground_window", "entire_desktop", "left_screen", "right_screen",
                                "upper_screen", "lower_screen", "upper_left_screen", "upper_right_screen",
                                "lower_left_screen", "lower_right_screen",
                            },
                            description = "The smallest useful view. Foreground, half-screen, and quadrant scopes look independently of the person's mouse.",
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
            description = "Request one fresh, higher-detail crop of a region inside the currently supplied overview. Use this before marking, reading, or acting when the exact target is too small to establish reliably. This only obtains evidence and never controls the computer.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "x", "y", "w", "h", "reason" },
                properties = new
                {
                    x = new { type = "number", description = "Left edge of the region in current supplied-image pixels." },
                    y = new { type = "number", description = "Top edge of the region in current supplied-image pixels." },
                    w = new { type = "number", description = "Width of the region in current supplied-image pixels." },
                    h = new { type = "number", description = "Height of the region in current supplied-image pixels." },
                    reason = new { type = "string", description = "Short reason a closer view is necessary." },
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
                description = "Place one safe, click-through ASHA overlay on a target visible in the supplied coordinate-mapped desktop image. If the person names a UI target, mark its interactive text-bearing button, navigation row, menu item, field, or tab rather than an adjacent icon or the same words in logs, chats, documents, tooltips, or annotations. This is visual guidance only and never controls the mouse or keyboard.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "kind", "x" },
                    properties = new
                    {
                        kind = new { type = "string", @enum = new[] { "dot", "circle", "box", "arrow", "label" } },
                        x = DesktopCoordinateParameter("Horizontal pixel position in the supplied image. For a box, this is its left edge. ASHA maps it to the desktop."),
                        y = DesktopCoordinateParameter("Vertical pixel position in the supplied image. For a box, this is its top edge. ASHA maps it to the desktop."),
                        w = new { type = "number", description = "Box width or signed arrow horizontal distance in supplied-image pixels." },
                        h = new { type = "number", description = "Box height or signed arrow vertical distance in supplied-image pixels." },
                        label = new { type = "string", description = "A short human-facing description of the marked target. It may include context not literally printed in the interface." },
                        visible_text = new { type = "string", description = "For a text target, the exact short words visibly printed inside the proposed target box, without explanatory words such as folder, account, button, or in the application." },
                        expected_app = new { type = "string", description = "The visible host application or window expected at the target, for top-layer verification. Use desktop only for the Windows desktop itself." },
                        target_type = new { type = "string", @enum = new[] { "text", "visual" }, description = "Use text when visible words identify the target and must be confirmed by local OCR; use visual for a genuinely non-textual object." },
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
                description = "Open or bring forward one installed application by its ordinary display name during an explicitly enabled ASHA computer-control session. This tool accepts no executable path, command, URL, or argument.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "application" },
                    properties = new
                    {
                        application = new { type = "string", description = "Only the application's ordinary installed display name. Infer it from the person's natural request and omit politeness, pronouns, commands, paths, and arguments." },
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
                description = "Open one existing non-system folder in Windows Explorer during an explicitly enabled ASHA computer-control session. This opens the folder read-only and accepts no command or URL.",
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
            description = "Perform one visible, permission-gated physical desktop input during an explicitly enabled ASHA control session. Use only for an action the person directly requested on a target visible in the supplied image.",
            parameters = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "action" },
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "move", "click", "double_click", "right_click", "drag", "scroll", "type_text", "key" } },
                    x = DesktopCoordinateParameter("Visible target x coordinate in the supplied image. Required for move, click, double click, right click, and drag. ASHA maps it to the desktop."),
                    y = DesktopCoordinateParameter("Visible target y coordinate in the supplied image. Required for move, click, double click, right click, and drag. ASHA maps it to the desktop."),
                    end_x = DesktopCoordinateParameter("Drag destination x coordinate in the supplied image."),
                    end_y = DesktopCoordinateParameter("Drag destination y coordinate in the supplied image."),
                    delta = new { type = "number", description = "Wheel delta, between minus 1200 and 1200." },
                    text = new { type = "string", description = "Short non-sensitive text for the currently focused field." },
                    key = new { type = "string", @enum = new[] { "enter", "escape", "tab", "space", "backspace", "up", "down", "left", "right" } },
                    expected_app = new { type = "string", description = "Visible host application expected at the target, so ASHA can verify the top layer immediately before input." },
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
            description = "Return a structured, truthful refusal when a directly requested desktop action cannot be grounded to one safe visible target. Never use this after performing the action.",
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
        description = $"{description} Prefer one scalar number. For vision-model compatibility, ASHA also accepts a two-number point or a four-number target box in x when the paired y value is omitted, and uses the target centre.",
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
    string? DesktopContext = null)
{
    public string DataUrl => $"data:image/png;base64,{Convert.ToBase64String(Bytes)}";
    public bool HasDesktopMapping => ContextX.HasValue && ContextY.HasValue && ContextWidth.GetValueOrDefault() > 0 && ContextHeight.GetValueOrDefault() > 0;
    public int ImageWidth => PixelWidth.GetValueOrDefault(ContextWidth.GetValueOrDefault());
    public int ImageHeight => PixelHeight.GetValueOrDefault(ContextHeight.GetValueOrDefault());
    public string CoordinateInstruction => HasDesktopMapping
        ? $"The supplied image is {ImageWidth} by {ImageHeight} pixels. If you use asha_mark, asha_request_detail, or asha_desktop_action, give coordinates and dimensions in those supplied-image pixels. Do not convert them to desktop coordinates; ASHA's normalization layer performs that mapping. {DesktopContext}".Trim()
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

    public int MapImageWidth(int imageWidth) =>
        (int)Math.Round(imageWidth * ContextWidth!.Value / (double)ImageWidth);

    public int MapImageHeight(int imageHeight) =>
        (int)Math.Round(imageHeight * ContextHeight!.Value / (double)ImageHeight);
}

public sealed record AshaVisualToolCall(string Id, string Name, JsonElement Arguments);
