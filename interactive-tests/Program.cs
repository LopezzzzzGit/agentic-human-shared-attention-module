using AshaLive;
using NAudio.Wave;

if (args is ["--activate", var requestedApplication])
{
    var result = await ApplicationLauncher.OpenAsync(requestedApplication, CancellationToken.None);
    Console.WriteLine($"Activated {result.ResolvedName}; process={result.ProcessName}; window={result.WindowTitle}; existing={result.ActivatedExisting}");
    return 0;
}

var cases = new[]
{
    new IntentCase("Can you remove that last mark please?", true, "latest"),
    new IntentCase("Take that highlight away again.", true, "latest"),
    new IntentCase("Please clear your highlights.", true, "all"),
    new IntentCase("Clear all visual cues.", true, "all"),
    new IntentCase("Entferne bitte diese Markierung.", true, "latest"),
    new IntentCase("Lösche alle deine Hinweise.", true, "all"),
    new IntentCase("Can you move the mouse to that box?", false, "latest"),
    new IntentCase("Please remove that file.", false, "latest"),
};

var failed = 0;

byte[] sourceSpeechWave;
using (var sourceStream = new MemoryStream())
{
    using (var sourceWriter = new WaveFileWriter(sourceStream, new WaveFormat(16_000, 16, 1)))
        sourceWriter.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
    sourceSpeechWave = sourceStream.ToArray();
}

var speechWithLeadIn = AshaVoiceSession.AddPlaybackLeadIn(sourceSpeechWave, TimeSpan.FromMilliseconds(120));
using (var preparedStream = new MemoryStream(speechWithLeadIn, writable: false))
using (var preparedReader = new WaveFileReader(preparedStream))
{
    var audio = new byte[preparedReader.Length];
    var audioLength = preparedReader.Read(audio, 0, audio.Length);
    var expectedSilence = preparedReader.WaveFormat.AverageBytesPerSecond * 120 / 1000;
    expectedSilence -= expectedSilence % preparedReader.WaveFormat.BlockAlign;
    var passed = audioLength == expectedSilence + 4 &&
                 audio.AsSpan(0, expectedSilence).IndexOfAnyExcept((byte)0) < 0 &&
                 audio.AsSpan(expectedSilence, 4).SequenceEqual(new byte[] { 1, 2, 3, 4 });
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | audio  | speech playback begins with a device-wakeup lead-in");
    if (!passed) failed++;
}

foreach (var test in cases)
{
    var matched = AshaVoiceSession.TryExtractGuidanceClearRequest(test.Text, out var scope);
    var passed = matched == test.ShouldMatch && (!matched || scope == test.Scope);
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | {scope,-6} | {test.Text}");
    if (!passed) failed++;
}

var semanticApplicationRoutingPassed =
    AshaVoiceSession.InitialToolNamesForTesting("Open my inbox.", allowComputerControl: true).Contains("asha_request_view") &&
    AshaVoiceSession.InitialToolNamesForTesting("Open my inbox.", allowComputerControl: true).Contains("asha_open_application") &&
    AshaVoiceSession.ModelRoutedGroundedToolNamesForTesting("Open my inbox.", allowComputerControl: true).Contains("asha_desktop_action") &&
    AshaVoiceSession.ModelRoutedGroundedToolNamesForTesting("Open my inbox.", allowComputerControl: true).Contains("asha_request_detail") &&
    ApplicationLauncher.ValidateName("Inbox") == "Inbox";
Console.WriteLine($"{(semanticApplicationRoutingPassed ? "PASS" : "FAIL")} | app    | natural open requests reach the model without runtime noun extraction");
if (!semanticApplicationRoutingPassed) failed++;

var identityCases = new[]
{
    new IdentityCase("LM Studio", "ai.elementlabs.lmstudio", @"C:\Program Files\LM Studio\LM Studio.exe", "firefox", "Videodetails - YouTube Studio — Mozilla Firefox", false),
    new IdentityCase("LM Studio", "ai.elementlabs.lmstudio", @"C:\Program Files\LM Studio\LM Studio.exe", "LM Studio", "", true),
    new IdentityCase("DaVinci Resolve", "com.blackmagicdesign.resolve", "", "Resolve", "Project One - DaVinci Resolve", true),
    new IdentityCase("Outlook", "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows", "", "firefox", "Inbox help - Mozilla Firefox", false),
};

foreach (var test in identityCases)
{
    var matched = ApplicationLauncher.WindowIdentityMatches(test.Application, test.AppId, test.TargetPath, test.ProcessName, test.WindowTitle);
    var passed = matched == test.ShouldMatch;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | window | {test.Application} versus {test.ProcessName} / {test.WindowTitle}");
    if (!passed) failed++;
}

var claimCases = new[]
{
    new ClaimCase("I've brought LM Studio to the front.", true),
    new ClaimCase("I have activated Outlook.", true),
    new ClaimCase("I can bring LM Studio to the front if you enable control.", false),
};

foreach (var test in claimCases)
{
    var matched = AshaVoiceSession.ClaimsVisualActionSucceeded(test.Text);
    var passed = matched == test.ShouldMatch;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | claim  | {test.Text}");
    if (!passed) failed++;
}

var perceptionCases = new[]
{
    new PerceptionCase("Can you highlight the documentation link for me?", true, ActivePerceptionGoal.Annotate, VisionRequestScope.ForegroundWindow, true),
    new PerceptionCase("Look in the upper left corner.", true, ActivePerceptionGoal.Observe, VisionRequestScope.UpperLeftScreen, false),
    new PerceptionCase("Could you read the error on the right side?", true, ActivePerceptionGoal.Observe, VisionRequestScope.RightScreen, true),
    new PerceptionCase("Can you click the settings button?", true, ActivePerceptionGoal.Act, VisionRequestScope.ForegroundWindow, true),
    new PerceptionCase("Look right here.", true, ActivePerceptionGoal.Observe, VisionRequestScope.PointerArea, false),
    new PerceptionCase("Where is the application window?", true, ActivePerceptionGoal.Locate, VisionRequestScope.EntireDesktop, false),
    new PerceptionCase("Did that window open?", true, ActivePerceptionGoal.Verify, VisionRequestScope.ForegroundWindow, false),
    new PerceptionCase("Schau bitte unten rechts auf den Bildschirm.", true, ActivePerceptionGoal.Observe, VisionRequestScope.LowerRightScreen, false),
    new PerceptionCase("Open LM Studio for me.", false, ActivePerceptionGoal.None, VisionRequestScope.ForegroundWindow, false),
    new PerceptionCase("Tell me a short joke.", false, ActivePerceptionGoal.None, VisionRequestScope.ForegroundWindow, false),
};

foreach (var test in perceptionCases)
{
    var plan = ActivePerceptionPlanner.Infer(test.Text);
    var passed = plan.RequiresFreshEvidence == test.ShouldObserve &&
                 plan.Goal == test.Goal &&
                 plan.Scope == test.Scope &&
                 (!test.PreferTextDetail || plan.PreferTextDetail || plan.AllowCloserLook);
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | vision | {test.Text} => {plan.Goal} / {plan.Scope}");
    if (!passed) failed++;
}

const int reliabilityCaseCount = 11;
var initialApplicationTools = AshaVoiceSession.InitialToolNamesForTesting(
    "Could you bring up my email program?",
    allowComputerControl: true);
var modelPrimaryApplicationPassed = initialApplicationTools.Contains("asha_open_application");
Console.WriteLine($"{(modelPrimaryApplicationPassed ? "PASS" : "FAIL")} | tools  | natural application requests reach the model with the application tool");
if (!modelPrimaryApplicationPassed) failed++;

var visualQuestionTools = AshaVoiceSession.GroundedToolNamesForTesting("Can you see Outlook open?", allowComputerControl: true);
var visualQuestionPassed = !visualQuestionTools.Contains("asha_desktop_action") &&
                           !visualQuestionTools.Contains("asha_open_application") &&
                           !visualQuestionTools.Contains("asha_open_folder") &&
                           visualQuestionTools.All(name => name is "asha_request_detail" or "asha_request_view");
Console.WriteLine($"{(visualQuestionPassed ? "PASS" : "FAIL")} | tools  | visual question receives vision tools only");
if (!visualQuestionPassed) failed++;

var clickTools = AshaVoiceSession.GroundedToolNamesForTesting("Can you click Nein?", allowComputerControl: true);
var clickPassed = clickTools.Contains("asha_desktop_action") && clickTools.Contains("asha_decline_action");
Console.WriteLine($"{(clickPassed ? "PASS" : "FAIL")} | tools  | grounded click receives action and truthful-refusal tools");
if (!clickPassed) failed++;

var disabledClickTools = AshaVoiceSession.GroundedToolNamesForTesting("Can you click Nein?", allowComputerControl: false);
var disabledClickPassed = disabledClickTools.Count == 0;
Console.WriteLine($"{(disabledClickPassed ? "PASS" : "FAIL")} | tools  | disabled control exposes no physical-input tool");
if (!disabledClickPassed) failed++;

var annotationTools = AshaVoiceSession.GroundedToolNamesForTesting("Highlight Developer Docs for me.", allowComputerControl: true);
var annotationPassed = annotationTools.Contains("asha_mark") && !annotationTools.Contains("asha_desktop_action");
Console.WriteLine($"{(annotationPassed ? "PASS" : "FAIL")} | tools  | annotation receives visual guidance without physical input");
if (!annotationPassed) failed++;

var relocatableCapturePassed = annotationTools.Contains("asha_request_view") && annotationTools.Contains("asha_request_detail");
Console.WriteLine($"{(relocatableCapturePassed ? "PASS" : "FAIL")} | tools  | model can relocate a view and place a detail crop independently of the pointer");
if (!relocatableCapturePassed) failed++;

var ordinaryMouseActionScope = MainWindow.ResolveVisionScope(
    "Can you use the mouse to click my inbox?",
    VisionRequestScope.ForegroundWindow,
    scene: null);
var explicitPointerScope = MainWindow.ResolveVisionScope(
    "Click right here beside my pointer.",
    VisionRequestScope.ForegroundWindow,
    scene: null);
var pointerSemanticsPassed = ordinaryMouseActionScope == VisionRequestScope.ForegroundWindow &&
                             explicitPointerScope == VisionRequestScope.PointerArea;
Console.WriteLine($"{(pointerSemanticsPassed ? "PASS" : "FAIL")} | vision | mouse use is not mistaken for a pointer-area location");
if (!pointerSemanticsPassed) failed++;

var ocrGroundingPassed =
    LocalOcrGrounder.BestContiguousMatchLengthForTesting("Inbox for pete.albrecht@gmx.net", "Inbox") == 1 &&
    LocalOcrGrounder.BestContiguousMatchLengthForTesting("Junk Email folder", "Junk Email") == 2 &&
    LocalOcrGrounder.BestContiguousMatchLengthForTesting("Drafts folder in Outlook", "Drafts") == 1 &&
    LocalOcrGrounder.BestContiguousMatchLengthForTesting("pete.albrecht@gmx.net account", "pete.albrecht@gmx.net") == 1;
Console.WriteLine($"{(ocrGroundingPassed ? "PASS" : "FAIL")} | ocr    | descriptive labels retain a verifiable visible-text anchor");
if (!ocrGroundingPassed) failed++;

var intervalCoordinatePassed = MainWindow.TryReadToolCoordinateForTesting("{\"x\":[120,140]}", "x", out var intervalCoordinate) &&
                               intervalCoordinate == 130;
Console.WriteLine($"{(intervalCoordinatePassed ? "PASS" : "FAIL")} | tools  | coordinate intervals are safely normalized to their centre");
if (!intervalCoordinatePassed) failed++;

var bundledVisionCoordinatePassed =
    MainWindow.TryReadImagePointForTesting("{\"x\":[130,230]}", "x", "y", out var pointX, out var pointY) &&
    pointX == 130 && pointY == 230 &&
    MainWindow.TryReadImagePointForTesting("{\"x\":[120,381],\"y\":[120,381]}", "x", "y", out var duplicateX, out var duplicateY) &&
    duplicateX == 120 && duplicateY == 381 &&
    MainWindow.TryReadImagePointForTesting("{\"x\":[100,200,300,400]}", "x", "y", out var boxX, out var boxY) &&
    boxX == 200 && boxY == 300;
Console.WriteLine($"{(bundledVisionCoordinatePassed ? "PASS" : "FAIL")} | tools  | model point and bounding-box coordinates resolve to a safe centre");
if (!bundledVisionCoordinatePassed) failed++;

var directActionContractPassed =
    AshaVoiceSession.ToolChoiceForTesting("Click the visible Inbox folder.", hasGroundedVision: true, allowComputerControl: true) == "required" &&
    AshaVoiceSession.ToolChoiceForTesting("Click the visible Inbox folder.", hasGroundedVision: false, allowComputerControl: true) == "auto" &&
    AshaVoiceSession.ToolChoiceForTesting("Click the visible Inbox folder.", hasGroundedVision: true, allowComputerControl: false) == "auto" &&
    AshaVoiceSession.GroundedToolNamesForTesting("Click the visible Inbox folder.", allowComputerControl: true).Contains("asha_decline_action");
Console.WriteLine($"{(directActionContractPassed ? "PASS" : "FAIL")} | tools  | grounded direct actions require execution or a structured refusal");
if (!directActionContractPassed) failed++;

var explicitGuidanceContractPassed =
    AshaVoiceSession.ToolChoiceForTesting("Highlight the visible Inbox folder.", hasGroundedVision: true, allowComputerControl: false) == "required" &&
    AshaVoiceSession.GroundedToolNamesForTesting("Highlight the visible Inbox folder.", allowComputerControl: false).Contains("asha_decline_guidance");
Console.WriteLine($"{(explicitGuidanceContractPassed ? "PASS" : "FAIL")} | tools  | explicit guidance requires a verified mark or structured refusal");
if (!explicitGuidanceContractPassed) failed++;

var typedComposerPassed = MainWindow.NormalizeTypedInput("  pete.albrecht@gmx.net  ") == "pete.albrecht@gmx.net";
Console.WriteLine($"{(typedComposerPassed ? "PASS" : "FAIL")} | chat   | typed technical identifiers bypass speech recognition unchanged");
if (!typedComposerPassed) failed++;

const string diagnosticSecret = "gsk_abcdefghijklmnopqrstuvwxyz123456";
var sanitizedDiagnostic = MainWindow.SanitizeTurnDiagnostic($"Bearer abc.def.ghi failed with {diagnosticSecret}. {new string('x', 600)}");
var diagnosticPassed = !sanitizedDiagnostic.Contains(diagnosticSecret, StringComparison.Ordinal) &&
                       !sanitizedDiagnostic.Contains("abc.def.ghi", StringComparison.Ordinal) &&
                       sanitizedDiagnostic.Length <= 501;
Console.WriteLine($"{(diagnosticPassed ? "PASS" : "FAIL")} | ledger | failed-turn diagnostics redact credentials and stay bounded");
if (!diagnosticPassed) failed++;

const int controlPolicyCaseCount = 7;
var defaultControlPolicy = new ComputerControlPolicy();
var defaultControlPassed =
    defaultControlPolicy.AllowedCapabilities == ComputerControlCapability.None &&
    defaultControlPolicy.VirtualCursorBehaviour == VirtualCursorBehaviour.Interact &&
    defaultControlPolicy.ShowVirtualCursor &&
    defaultControlPolicy.AskBeforePhysicalFallback;
Console.WriteLine($"{(defaultControlPassed ? "PASS" : "FAIL")} | policy | every control capability defaults off while safe child defaults are retained");
if (!defaultControlPassed) failed++;

var rejectedWithoutSession = !ComputerControlLease.TryStart(
    new ComputerControlPolicy { AllowPhysicalCursor = true },
    null,
    out _,
    out _);
var rejectedWithoutCapability = !ComputerControlLease.TryStart(
    new ComputerControlPolicy(),
    "session-one",
    out _,
    out _);
var rejectedLeasePassed = rejectedWithoutSession && rejectedWithoutCapability;
Console.WriteLine($"{(rejectedLeasePassed ? "PASS" : "FAIL")} | lease  | a lease requires both a retained session and an allowed capability");
if (!rejectedLeasePassed) failed++;

var activeControlPolicy = new ComputerControlPolicy
{
    AllowApplicationAndFolderOpening = true,
    EnableVirtualCursor = true,
    VirtualCursorBehaviour = VirtualCursorBehaviour.Interact,
    ShowVirtualCursor = false,
    AllowPhysicalCursor = true,
    AskBeforePhysicalFallback = true,
};
var started = ComputerControlLease.TryStart(activeControlPolicy, "session-one", out var activeLease, out _);
var activeAccess = new ComputerControlAccess(activeControlPolicy, activeLease, "session-one");
var leaseSnapshotPassed =
    started &&
    activeAccess.CanOpenApplicationsAndFolders &&
    activeAccess.CanInteractWithVirtualCursor &&
    !activeAccess.CanShowVirtualCursor &&
    activeAccess.CanUsePhysicalCursor &&
    activeAccess.MustAskBeforePhysicalFallback &&
    activeAccess.AllowsCurrentPhysicalExecutorAction("click") &&
    !activeAccess.AllowsCurrentPhysicalExecutorAction("key");
Console.WriteLine($"{(leaseSnapshotPassed ? "PASS" : "FAIL")} | lease  | effective capabilities are the explicit policy-and-lease intersection");
if (!leaseSnapshotPassed) failed++;

activeControlPolicy.AllowKeyboardInteraction = true;
var noSilentExpansion = !new ComputerControlAccess(activeControlPolicy, activeLease, "session-one").CanUseKeyboard;
Console.WriteLine($"{(noSilentExpansion ? "PASS" : "FAIL")} | lease  | enabling a global capability does not silently expand an active lease");
if (!noSilentExpansion) failed++;

activeControlPolicy.AllowPhysicalCursor = false;
var immediateRevocation =
    !new ComputerControlAccess(activeControlPolicy, activeLease, "session-one").CanUsePhysicalCursor &&
    !new ComputerControlAccess(activeControlPolicy, activeLease, "session-one").AllowsCurrentPhysicalExecutorAction("click") &&
    !new ComputerControlAccess(activeControlPolicy, activeLease, "another-session").IsLeaseActive;
Console.WriteLine($"{(immediateRevocation ? "PASS" : "FAIL")} | policy | revocation is immediate and leases cannot cross session boundaries");
if (!immediateRevocation) failed++;

var demonstratorPolicy = new ComputerControlPolicy
{
    EnableVirtualCursor = true,
    VirtualCursorBehaviour = VirtualCursorBehaviour.DemonstrateOnly,
    ShowVirtualCursor = false,
};
demonstratorPolicy.Normalize();
var demonstratorPassed = demonstratorPolicy.ShowVirtualCursor &&
                         !demonstratorPolicy.AllowedCapabilities.HasFlag(ComputerControlCapability.VirtualCursorInteraction);
Console.WriteLine($"{(demonstratorPassed ? "PASS" : "FAIL")} | policy | demonstration mode remains visible and non-interacting");
if (!demonstratorPassed) failed++;

var applicationOnlyTools = AshaVoiceSession.InitialToolNamesForCapabilitiesForTesting(
    "Open the calendar application.",
    allowApplicationControl: true,
    allowDesktopAction: false);
var pointerOnlyTools = AshaVoiceSession.InitialToolNamesForCapabilitiesForTesting(
    "Move the pointer to the calendar.",
    allowApplicationControl: false,
    allowDesktopAction: true);
var separatedToolPermissionsPassed =
    applicationOnlyTools.Contains("asha_open_application") &&
    !pointerOnlyTools.Contains("asha_open_application") &&
    !AshaVoiceSession.GroundedToolNamesForTesting("Click the calendar.", allowComputerControl: false).Contains("asha_desktop_action");
Console.WriteLine($"{(separatedToolPermissionsPassed ? "PASS" : "FAIL")} | tools  | application permission does not leak physical-input tools");
if (!separatedToolPermissionsPassed) failed++;

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} ASHA guidance-removal intent test(s) failed.");
    return 1;
}

var executedCalls = 0;
string? executedScope = null;
using (var voice = new AshaVoiceSession())
{
    var reply = await voice.RespondToTranscriptAsync(
        "Can you remove that last mark please?",
        visionResolver: null,
        visualToolExecutor: (call, _, _) =>
        {
            executedCalls++;
            executedScope = call.Arguments.GetProperty("scope").GetString();
            return Task.FromResult("{\"ok\":true,\"scope\":\"latest\",\"removed\":1}");
        },
        controlAccess: new ComputerControlAccess(new ComputerControlPolicy(), null, null),
        allowModelRequestedVision: false,
        awarenessContext: null,
        CancellationToken.None);

    var passed = executedCalls == 1 &&
                 executedScope == "latest" &&
                 reply == "I've removed that highlight.";
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | local  | guidance removal executes without a provider turn");
    if (!passed) failed++;
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} ASHA guidance-removal test(s) failed.");
    return 1;
}

Console.WriteLine($"All {cases.Length + identityCases.Length + claimCases.Length + perceptionCases.Length + reliabilityCaseCount + 3 + controlPolicyCaseCount} ASHA reliability, audio, intent, perception, policy, lease, and application-control tests passed.");
return 0;

internal sealed record IntentCase(string Text, bool ShouldMatch, string Scope);
internal sealed record IdentityCase(string Application, string AppId, string TargetPath, string ProcessName, string WindowTitle, bool ShouldMatch);
internal sealed record ClaimCase(string Text, bool ShouldMatch);
internal sealed record PerceptionCase(
    string Text,
    bool ShouldObserve,
    ActivePerceptionGoal Goal,
    VisionRequestScope Scope,
    bool PreferTextDetail);
