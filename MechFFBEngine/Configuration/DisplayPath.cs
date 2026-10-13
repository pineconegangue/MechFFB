namespace MechFFBEngine.Configuration;

/// <summary>
/// Formats file paths for the debug log without exposing the Windows user name, e.g.
/// C:\Users\Name\AppData\Roaming\MechFFB\settings.json -> %AppData%\MechFFB\settings.json.
/// Use for anything written to the log; the real path is still used for file access.
/// </summary>
public static class DisplayPath
{
    public static string Of(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Most specific first
        foreach (var (folder, token) in new[]
        {
            (Environment.SpecialFolder.LocalApplicationData, "%LocalAppData%"),
            (Environment.SpecialFolder.ApplicationData, "%AppData%"),
            (Environment.SpecialFolder.UserProfile, "%UserProfile%")
        })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return token + path.Substring(root.Length);
        }

        // The app's own folder is shown relative, e.g. ".\weapon_profiles.json"
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (appDir.Length > 0 && path.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
            return "." + path.Substring(appDir.Length);

        return path;
    }
}
