using AshaLive;

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
foreach (var test in cases)
{
    var matched = AshaVoiceSession.TryExtractGuidanceClearRequest(test.Text, out var scope);
    var passed = matched == test.ShouldMatch && (!matched || scope == test.Scope);
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | {scope,-6} | {test.Text}");
    if (!passed) failed++;
}

var applicationCases = new[]
{
    new ApplicationIntentCase("Can you open LM Studio for me?", true, "LM Studio"),
    new ApplicationIntentCase("Bring LM Studio to the front.", true, "LM Studio"),
    new ApplicationIntentCase("Could you bring LM Studio to the foreground, please?", true, "LM Studio"),
    new ApplicationIntentCase("Switch to Outlook.", true, "Outlook"),
    new ApplicationIntentCase("Focus on DaVinci Resolve now.", true, "DaVinci Resolve"),
    new ApplicationIntentCase("Bring LM Studio nach vorne.", true, "LM Studio"),
    new ApplicationIntentCase("Wechsle zu Outlook.", true, "Outlook"),
    new ApplicationIntentCase("Bring it to the front.", false, ""),
};

foreach (var test in applicationCases)
{
    var matched = AshaVoiceSession.TryExtractApplicationActivationRequest(test.Text, out var application);
    var passed = matched == test.ShouldMatch && (!matched || application == test.Application);
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} | app    | {test.Text} => {application}");
    if (!passed) failed++;
}

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
        allowComputerControl: false,
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

Console.WriteLine($"All {cases.Length + applicationCases.Length + identityCases.Length + claimCases.Length + perceptionCases.Length + 1} ASHA intent, perception, and application-control tests passed.");
return 0;

internal sealed record IntentCase(string Text, bool ShouldMatch, string Scope);
internal sealed record ApplicationIntentCase(string Text, bool ShouldMatch, string Application);
internal sealed record IdentityCase(string Application, string AppId, string TargetPath, string ProcessName, string WindowTitle, bool ShouldMatch);
internal sealed record ClaimCase(string Text, bool ShouldMatch);
internal sealed record PerceptionCase(
    string Text,
    bool ShouldObserve,
    ActivePerceptionGoal Goal,
    VisionRequestScope Scope,
    bool PreferTextDetail);
