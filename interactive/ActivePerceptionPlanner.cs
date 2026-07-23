using System.Text.RegularExpressions;

namespace AshaLive;

/// <summary>
/// Describes why ASHA needs a current desktop observation. The plan contains
/// no application names or target-specific recipes; it only selects an
/// evidence scope and an interaction goal from ordinary language.
/// </summary>
internal enum ActivePerceptionGoal
{
    None,
    Observe,
    Locate,
    Annotate,
    Act,
    Verify,
}

internal sealed record ActivePerceptionPlan(
    ActivePerceptionGoal Goal,
    VisionRequestScope Scope,
    bool RequiresFreshEvidence,
    bool PreferTextDetail,
    bool AllowCloserLook)
{
    public static ActivePerceptionPlan None { get; } = new(
        ActivePerceptionGoal.None,
        VisionRequestScope.ForegroundWindow,
        RequiresFreshEvidence: false,
        PreferTextDetail: false,
        AllowCloserLook: false);
}

/// <summary>
/// A conservative, model-independent fallback for active perception. The
/// configured model may still request a view for language this planner does
/// not recognize. This layer guarantees that common natural visual requests
/// do not fail merely because one provider omitted a tool call.
/// </summary>
internal static class ActivePerceptionPlanner
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static ActivePerceptionPlan Infer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ActivePerceptionPlan.None;
        var value = Regex.Replace(text, @"\s+", " ").Trim();

        var annotation = Matches(value,
            @"\b(highlight|mark|point\s+(?:out|to)|show\s+me\s+where|circle|draw\s+(?:a\s+)?(?:box|arrow)|annotate|hervorheben|markier\w*|zeig\w*\s+mir|einkreisen|umkreisen|rahmen|pfeil)\b");
        var physicalAction = Matches(value,
            @"\b(click|double[- ]click|right[- ]click|drag|scroll|move\s+(?:the\s+)?(?:mouse|pointer|cursor)|klick\w*|doppelklick\w*|rechtsklick\w*|zieh\w*|scroll\w*|beweg\w*\s+(?:die\s+)?(?:maus|zeiger))\b");
        var inApplicationOpenAction = Matches(value,
            @"\b(?:open|activate|select|expand|öffn\w*|oeffn\w*|aktivier\w*|wähl\w*|waehl\w*|klapp\w*\s+auf)\b.{0,90}\b(?:account|folder|inbox|email|message|item|document|link|menu|tab|konto|ordner|posteingang|e-?mail|nachricht|element|dokument|menü|registerkarte)\b");
        physicalAction |= inApplicationOpenAction;
        var verification = Matches(value,
            @"\b(did\s+(?:that|it|this).{0,35}(?:work|open|change|move|appear)|is\s+(?:it|that|this).{0,30}(?:open|visible|selected|active)|verify|confirm|hat\s+(?:das|es).{0,35}(?:geklappt|funktioniert|geöffnet|geaendert|geändert)|ist\s+(?:es|das).{0,30}(?:offen|sichtbar|ausgewählt|aktiv)|prüf\w*|verifizier\w*)\b");
        var observation = Matches(value,
            @"\b(look|see|read|inspect|check\s+(?:the|my|this|that|what|whether|if)|take\s+(?:a\s+)?screen\s*shot|screen\s*capture|what(?:'s|\s+is)\s+(?:on|in)|what\b.{0,55}\b(?:visible|shown|displayed)|which\b.{0,55}\b(?:visible|shown|displayed)|schau\w*|sieh\w*|sehen|lies|lesen|prüf\w*|ueberpruef\w*|überprüf\w*|bildschirmfoto|screenshot|was\b.{0,55}\b(?:sichtbar|angezeigt)|welch\w*\b.{0,55}\b(?:sichtbar|angezeigt))\b");
        var location = Matches(value,
            @"\b(where\s+(?:is|are|did)|find|locate|which\s+(?:button|control|item|window|app)|wo\s+(?:ist|sind|finde)|find\w*|lokalisier\w*|welch\w*\s+(?:knopf|schaltfläche|element|fenster|app))\b");
        var visualObject = Matches(value,
            @"\b(screen|desktop|monitor|display|window|app|application|program|button|tab|menu|field|label|link|icon|control|setting|option|panel|sidebar|section|text|document|image|dialog|mouse|cursor|pointer|error|message|email|inbox|sender|subject|list|bildschirm|desktop|monitor|anzeige|fenster|anwendung|programm|knopf|schaltfläche|registerkarte|menü|feld|beschriftung|symbol|einstellung|bereich|seitenleiste|text|dokument|bild|dialog|maus|zeiger|fehler|meldung|e-?mail|posteingang|absender|betreff|liste)\b");
        var spatialReference = Matches(value,
            @"\b(left|right|upper|lower|top|bottom|corner|side|here|there|links|rechts|oben|unten|ecke|seite|hier|dort)\b");

        var requiresEvidence = annotation || physicalAction || verification || location ||
                               (observation && (visualObject || spatialReference)) ||
                               Matches(value, @"\b(can|could|would|kannst|könntest|koenntest)\s+(?:you|du).{0,50}\b(?:see|look|read|show|find|sehen|schauen|lesen|zeigen|finden)\b");
        if (!requiresEvidence) return ActivePerceptionPlan.None;

        var goal = annotation
            ? ActivePerceptionGoal.Annotate
            : physicalAction
                ? ActivePerceptionGoal.Act
                : verification
                    ? ActivePerceptionGoal.Verify
                    : location
                        ? ActivePerceptionGoal.Locate
                        : ActivePerceptionGoal.Observe;

        var scope = InferScope(value, goal);
        var textDetail = Matches(value,
            @"\b(read|text|words?|letters?|label|button|menu|field|document|docs|documentation|error|message|email|inbox|sender|subject|list|lies|lesen|text|wörter|woerter|buchstaben|beschriftung|knopf|schaltfläche|menü|feld|dokument|dokumentation|fehler|meldung|e-?mail|posteingang|absender|betreff|liste)\b");
        var broadScope = scope is VisionRequestScope.EntireDesktop or
            VisionRequestScope.LeftScreen or VisionRequestScope.RightScreen or
            VisionRequestScope.UpperScreen or VisionRequestScope.LowerScreen or
            VisionRequestScope.UpperLeftScreen or VisionRequestScope.UpperRightScreen or
            VisionRequestScope.LowerLeftScreen or VisionRequestScope.LowerRightScreen;

        return new ActivePerceptionPlan(
            goal,
            scope,
            RequiresFreshEvidence: true,
            PreferTextDetail: textDetail && !broadScope,
            AllowCloserLook: broadScope || textDetail);
    }

    private static VisionRequestScope InferScope(string text, ActivePerceptionGoal goal)
    {
        if (Matches(text, @"\b(?:(?:under|near|around|at)\s+(?:my|the)\s+(?:mouse|cursor|pointer)|where\s+i(?:'m|\s+am)\s+pointing|mouse\s+pointer|mauszeiger|(?:unter|neben|um)\b.{0,25}\b(?:maus|zeiger|cursor))\b") ||
            Matches(text, @"\b(click|look|mark|point|klick\w*|schau\w*|markier\w*|zeig\w*)\s+(?:right\s+)?(?:here|hier)\b"))
            return VisionRequestScope.PointerArea;

        var upper = Matches(text, @"\b(upper|top|oben|ober\w*)\b");
        var lower = Matches(text, @"\b(lower|bottom|unten|unter\w*)\b");
        var left = Matches(text, @"\b(left|links|linke\w*)\b");
        var right = Matches(text, @"\b(right|rechts|rechte\w*)\b");
        if (upper && left) return VisionRequestScope.UpperLeftScreen;
        if (upper && right) return VisionRequestScope.UpperRightScreen;
        if (lower && left) return VisionRequestScope.LowerLeftScreen;
        if (lower && right) return VisionRequestScope.LowerRightScreen;
        if (upper) return VisionRequestScope.UpperScreen;
        if (lower) return VisionRequestScope.LowerScreen;
        if (left) return VisionRequestScope.LeftScreen;
        if (right) return VisionRequestScope.RightScreen;

        if (Matches(text,
                @"\b(entire|whole|full|all)\s+(?:desktop|screen|monitor|display)|desktop\s+overview|all\s+(?:open\s+)?(?:apps|applications|windows)|gesamte\w*|ganze\w*\s+(?:desktop|bildschirm|monitor|anzeige)|desktopübersicht|desktopuebersicht|alle\s+(?:offenen\s+)?(?:apps|anwendungen|fenster)\b"))
            return VisionRequestScope.EntireDesktop;

        if (Matches(text,
                @"\b(this|current|foreground|active)\s+(?:app|application|program|window)|in\s+(?:this|the\s+current|the\s+active)\s+(?:app|window)|diese\w*|aktuelle\w*|aktive\w*\s+(?:app|anwendung|programm|fenster)\b"))
            return VisionRequestScope.ForegroundWindow;

        if (Matches(text,
                @"\b(button|tab|menu|menu\s+item|field|label|link|icon|control|setting|option|panel|sidebar|section|docs|documentation|knopf|schaltfläche|registerkarte|menü|menüpunkt|feld|beschriftung|symbol|einstellung|bereich|seitenleiste|dokumentation)\b"))
            return VisionRequestScope.ForegroundWindow;

        // Unknown-location requests begin with a cheap overview. Verification
        // and direct interaction default to the active window where the user
        // is already working.
        if (goal == ActivePerceptionGoal.Locate)
            return VisionRequestScope.EntireDesktop;
        if (goal == ActivePerceptionGoal.Observe &&
            Matches(text, @"\b(screen|desktop|monitor|display|bildschirm|desktop|monitor|anzeige)\b"))
            return VisionRequestScope.EntireDesktop;
        return VisionRequestScope.ForegroundWindow;
    }

    private static bool Matches(string value, string pattern) => Regex.IsMatch(value, pattern, Options);
}
