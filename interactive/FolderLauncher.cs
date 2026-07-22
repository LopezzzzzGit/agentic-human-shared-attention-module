using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AshaLive;

/// <summary>
/// Opens an existing folder in Explorer without modifying its contents. A
/// display name is resolved from known user folders and drive roots; absolute
/// paths are accepted only when they are outside protected system locations.
/// </summary>
internal static partial class FolderLauncher
{
    public static async Task<FolderLaunchResult> OpenAsync(string requestedFolder, CancellationToken cancellationToken)
    {
        var request = ValidateRequest(requestedFolder);
        var path = ResolveFolder(request);
        EnsureNotProtected(path);

        var start = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        start.ArgumentList.Add(path);
        using var launched = Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not accept the folder-open request.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var expected = Normalize(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var foreground = ForegroundWindow();
            if (foreground is not null &&
                string.Equals(foreground.Value.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(expected) || Normalize(foreground.Value.Title).Contains(expected, StringComparison.Ordinal)))
                return new FolderLaunchResult(request, path, foreground.Value.Title);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Windows accepted the request for '{request}', but ASHA could not verify the Explorer window.");
    }

    internal static string ValidateRequest(string requestedFolder)
    {
        var request = requestedFolder.Trim().Trim('"');
        if (request.Length is < 1 or > 260 || request.IndexOfAny(['\r', '\n', '\0', '|', '>', '<', '*', '?']) >= 0)
            throw new InvalidOperationException("Choose an existing folder by name or by a normal local folder path.");
        return request;
    }

    private static string ResolveFolder(string request)
    {
        if (Path.IsPathRooted(request))
        {
            var fullPath = Path.GetFullPath(request);
            if (!Directory.Exists(fullPath)) throw new InvalidOperationException($"The folder '{request}' does not exist.");
            return fullPath;
        }

        var normalized = Normalize(request);
        var candidates = CandidateFolders()
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new
            {
                Path = path,
                Name = Normalize(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
            })
            .Where(candidate => candidate.Name == normalized || candidate.Name.Contains(normalized, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Name == normalized ? 0 : 1)
            .ThenBy(candidate => candidate.Path.Length)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException($"ASHA could not find an accessible folder named '{request}'. Use its full local path if you want that exact folder.");
        if (candidates.Length > 1 && candidates[0].Name != normalized && candidates[1].Name == candidates[0].Name)
            throw new InvalidOperationException($"Several folders match '{request}'. Please say or type the full local path.");
        return candidates[0].Path;
    }

    private static IEnumerable<string> CandidateFolders()
    {
        var userFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        foreach (var folder in userFolders) yield return folder;

        var scanRoots = userFolders.Append(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .Concat(DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.RootDirectory.FullName))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scanRoots)
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var child in children)
            {
                if (!IsProtected(child)) yield return child;
            }
        }
    }

    private static void EnsureNotProtected(string path)
    {
        if (IsProtected(path))
            throw new InvalidOperationException("ASHA will not open protected Windows, program, or application-data folders through the voice control tool.");
    }

    private static bool IsProtected(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return ProtectedRoots().Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ProtectedRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
            yield return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static (string ProcessName, string Title)? ForegroundWindow()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return null;
        var title = new StringBuilder(512);
        _ = GetWindowText(window, title, title.Capacity);
        try { return (Process.GetProcessById((int)processId).ProcessName, title.ToString().Trim()); }
        catch { return null; }
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
}

internal sealed record FolderLaunchResult(string RequestedFolder, string Path, string WindowTitle);
