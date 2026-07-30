using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AshaLive;

internal sealed class ApplicationResolutionException : InvalidOperationException
{
    public ApplicationResolutionException(
        string requestedName,
        IReadOnlyList<string> candidates,
        bool noInstalledMatch)
        : base(noInstalledMatch
            ? $"No installed application matches {requestedName}."
            : $"More than one installed application matches {requestedName}.")
    {
        RequestedName = requestedName;
        Candidates = candidates;
        NoInstalledMatch = noInstalledMatch;
    }

    public string RequestedName { get; }
    public IReadOnlyList<string> Candidates { get; }
    public bool NoInstalledMatch { get; }
}

/// <summary>
/// Resolves human-facing names against Windows' own Start application catalog.
/// Model input is never treated as a path or command line; only an AppID that
/// Windows returned from Get-StartApps can be launched.
/// </summary>
internal static partial class ApplicationLauncher
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(18);
    private static readonly object CatalogGate = new();
    private static Task<IReadOnlyList<StartApplication>>? _catalog;

    public static async Task<ApplicationLaunchResult> OpenAsync(string requestedName, CancellationToken cancellationToken)
    {
        // The model supplies the semantic application display name. This
        // boundary validates it but never rewrites ordinary language into an
        // application guess (for example, "open my inbox" into "inbox").
        var name = ValidateName(requestedName);
        var applications = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        var match = Resolve(applications, name);
        if (IsProtectedApplicationIdentity(match, Environment.ProcessPath))
            throw new DesktopActionAuthorizationException(
                "I can't launch or activate my own protected interface through general computer control.");

        var existing = FindWindow(match);
        if (existing is not null)
        {
            if (existing.Id == Environment.ProcessId)
                throw new DesktopActionAuthorizationException(
                    "I can't launch or activate my own protected interface through general computer control.");
            var activation = await ActivateAsync(existing, match, cancellationToken).ConfigureAwait(false);
            return ToResult(name, match.Name, existing, activatedExisting: true, activation);
        }

        using var started = LaunchAppId(match.AppId);
        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = FindWindow(match, started);
            if (window is not null)
            {
                var activation = await ActivateAsync(window, match, cancellationToken).ConfigureAwait(false);
                return ToResult(name, match.Name, window, activatedExisting: false, activation);
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Windows accepted the request to open '{match.Name}', but I couldn't verify a visible application window.");
    }

    internal static string ValidateName(string requestedName)
    {
        var name = requestedName.Trim();
        if (name.Length is < 1 or > 80 || !SafeApplicationName().IsMatch(name))
            throw new InvalidOperationException("Choose an installed application by its display name, without a path or command-line characters.");
        return name;
    }

    internal static string ResolveDisplayNameForTesting(
        string requestedName,
        IReadOnlyList<string> installedDisplayNames) =>
        Resolve(
            installedDisplayNames
                .Select((name, index) => new StartApplication(
                    name,
                    $"asha-test-{index}.exe",
                    $@"C:\ASHA-Test\asha-test-{index}.exe"))
                .ToArray(),
            requestedName).Name;

    internal static bool IsProtectedApplicationIdentityForTesting(
        string name,
        string appId,
        string targetPath,
        string currentProcessPath) =>
        IsProtectedApplicationIdentity(
            new StartApplication(name, appId, targetPath),
            currentProcessPath);

    private static async Task<IReadOnlyList<StartApplication>> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<StartApplication>> task;
        lock (CatalogGate) task = _catalog ??= LoadCatalogAsync(CancellationToken.None);
        try { return await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            lock (CatalogGate)
            {
                if (ReferenceEquals(_catalog, task)) _catalog = null;
            }
            throw;
        }
    }

    private static async Task<IReadOnlyList<StartApplication>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        // This command is fixed. No model- or user-supplied text enters it.
        start.ArgumentList.Add("$roots=@([Environment]::GetFolderPath('StartMenu'),[Environment]::GetFolderPath('CommonStartMenu')); $targets=@{}; $wsh=New-Object -ComObject WScript.Shell; Get-ChildItem -LiteralPath $roots -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object { try { $shortcut=$wsh.CreateShortcut($_.FullName); if ($shortcut.TargetPath -and -not $targets.ContainsKey($_.BaseName.ToLowerInvariant())) { $targets[$_.BaseName.ToLowerInvariant()]=$shortcut.TargetPath } } catch {} }; Get-StartApps | ForEach-Object { $key=$_.Name.ToLowerInvariant(); $target=if ($targets.ContainsKey($key)) { $targets[$key] } else { '' }; [pscustomobject]@{Name=$_.Name;AppID=$_.AppID;TargetPath=$target} } | ConvertTo-Json -Compress");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows could not enumerate installed Start applications.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"Windows could not enumerate installed Start applications{(string.IsNullOrWhiteSpace(error) ? "." : ": " + error.Trim())}");

        using var document = JsonDocument.Parse(output);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        return elements
            .Select(element => new StartApplication(
                element.TryGetProperty("Name", out var rawName) ? rawName.GetString()?.Trim() ?? "" : "",
                element.TryGetProperty("AppID", out var rawId) ? rawId.GetString()?.Trim() ?? "" : "",
                element.TryGetProperty("TargetPath", out var rawTarget) ? rawTarget.GetString()?.Trim() ?? "" : ""))
            .Where(item => item.Name.Length > 0 && item.AppId.Length > 0)
            .GroupBy(item => (Normalize(item.Name), item.AppId), StringTupleComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static StartApplication Resolve(IReadOnlyList<StartApplication> applications, string requestedName)
    {
        var requested = Normalize(requestedName);
        var scored = applications
            .Where(IsPermittedApplication)
            .Select(item => new { Item = item, Score = MatchScore(item, requested) })
            .Where(candidate => candidate.Score < 100)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Item.Name.Contains("beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(candidate => candidate.Item.Name.Length)
            .ToArray();
        if (scored.Length == 0)
            throw new ApplicationResolutionException(
                requestedName,
                [],
                noInstalledMatch: true);

        var best = scored[0];
        var ambiguous = scored.Skip(1)
            .Where(candidate => candidate.Score == best.Score)
            .Select(candidate => candidate.Item.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .ToArray();
        if (ambiguous.Length > 0 && best.Score > 0)
        {
            throw new ApplicationResolutionException(
                requestedName,
                new[] { best.Item.Name }.Concat(ambiguous).ToArray(),
                noInstalledMatch: false);
        }
        return best.Item;
    }

    private static bool IsPermittedApplication(StartApplication application)
    {
        var appId = application.AppId;
        if (Uri.TryCreate(appId, UriKind.Absolute, out var uri) && uri.Scheme is not "file") return false;
        var identity = $"{application.Name} {appId}";
        return !Regex.IsMatch(
            identity,
            @"cmd\.exe|command prompt|eingabeaufforderung|powershell|pwsh|terminal|wsl|bash|regedit|registrierungs-editor|msconfig|run dialog|ausführen|\.msc(?:$|[.!\s])|RunDialog|ControlPanel",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsProtectedApplicationIdentity(
        StartApplication application,
        string? currentProcessPath)
    {
        if (string.IsNullOrWhiteSpace(currentProcessPath)) return false;
        var currentFullPath = Path.GetFullPath(currentProcessPath);
        if (!string.IsNullOrWhiteSpace(application.TargetPath))
        {
            try
            {
                if (string.Equals(
                        Path.GetFullPath(application.TargetPath),
                        currentFullPath,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed catalog path is not trusted as an identity.
            }
        }

        var currentProcessName = Normalize(Path.GetFileNameWithoutExtension(currentFullPath));
        var expectedProcessName = ExpectedProcessName(
            application.AppId,
            application.TargetPath);
        return currentProcessName.Length > 0 &&
               string.Equals(
                   expectedProcessName,
                   currentProcessName,
                   StringComparison.Ordinal);
    }

    private static int MatchScore(StartApplication application, string requested)
    {
        var name = Normalize(application.Name);
        if (name == requested) return 0;
        if (SemanticAliasMatches(application, requested)) return 1;
        if (name.StartsWith(requested + " ", StringComparison.Ordinal)) return 2;
        if (name.Contains(requested, StringComparison.Ordinal)) return 3;

        var words = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words.All(word => name.Contains(word, StringComparison.Ordinal))) return 4;
        return 100;
    }

    private static bool SemanticAliasMatches(StartApplication application, string requested)
    {
        if (requested is "notepad" or "notebook" &&
            application.AppId.Contains("WindowsNotepad", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Process LaunchAppId(string appId)
    {
        var start = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        start.ArgumentList.Add(@"shell:AppsFolder\" + appId);
        return Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not accept the installed-application launch request.");
    }

    private static Process? FindWindow(StartApplication application, Process? started = null)
    {
        var candidates = new List<Process>();
        if (started is not null) candidates.Add(started);
        candidates.AddRange(Process.GetProcesses());
        foreach (var process in candidates
                     .GroupBy(candidate => candidate.Id)
                     .Select(group => group.First()))
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                if (WindowIdentityMatches(application.Name, application.AppId, application.TargetPath, process.ProcessName, process.MainWindowTitle))
                    return process;
            }
            catch
            {
                // Protected and short-lived processes can disappear while the
                // desktop is being enumerated. They are not launch matches.
            }
        }
        return null;
    }

    internal static bool WindowIdentityMatches(
        string applicationName,
        string appId,
        string? targetPath,
        string processName,
        string? windowTitle)
    {
        var expectedProcess = ExpectedProcessName(appId, targetPath);
        var actualProcess = Normalize(processName);
        if (!string.IsNullOrWhiteSpace(expectedProcess))
            return actualProcess == expectedProcess;

        var normalizedName = Normalize(applicationName);
        if (actualProcess == normalizedName) return true;

        var title = Normalize(windowTitle ?? string.Empty);
        return normalizedName.Length > 0 &&
               (title == normalizedName || ContainsWholePhrase(title, normalizedName));
    }

    private static string ExpectedProcessName(string appId, string? targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath) &&
            targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Normalize(Path.GetFileNameWithoutExtension(targetPath));

        var last = appId.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Normalize(Path.GetFileNameWithoutExtension(last))
            : "";
    }

    private static bool ContainsWholePhrase(string text, string phrase)
    {
        var index = text.IndexOf(phrase, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || text[index - 1] == ' ';
            var afterIndex = index + phrase.Length;
            var after = afterIndex == text.Length || text[afterIndex] == ' ';
            if (before && after) return true;
            index = text.IndexOf(phrase, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static ApplicationLaunchResult ToResult(
        string requestedName,
        string resolvedName,
        Process window,
        bool activatedExisting,
        ForegroundActivationResult activation)
    {
        window.Refresh();
        return new ApplicationLaunchResult(
            requestedName,
            resolvedName,
            window.ProcessName,
            window.MainWindowTitle,
            activatedExisting,
            activation);
    }

    private static async Task<ForegroundActivationResult> ActivateAsync(
        Process process,
        StartApplication application,
        CancellationToken cancellationToken)
    {
        process.Refresh();
        var window = process.MainWindowHandle;
        if (window == IntPtr.Zero)
            throw new InvalidOperationException($"ASHA found {application.Name}, but it does not currently have a visible window.");
        return await ForegroundWindowActivator.ActivateAsync(
            window,
            process.Id,
            cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}\s.&()'_+\-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeApplicationName();

    private sealed record StartApplication(string Name, string AppId, string TargetPath);

    private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();
        public bool Equals((string, string) left, (string, string) right) =>
            string.Equals(left.Item1, right.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Item2, right.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item1),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item2));
    }
}

internal sealed record ApplicationLaunchResult(
    string RequestedName,
    string ResolvedName,
    string ProcessName,
    string WindowTitle,
    bool ActivatedExisting,
    ForegroundActivationResult Activation);
