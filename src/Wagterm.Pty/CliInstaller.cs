namespace Wagterm.Pty;

/// <summary>
/// Opt-in installer that puts <c>wagtermctl</c> on the user's PATH (HKCU\Environment) so it's
/// callable from any shell. Mirrors agterm's "Install Command Line Tool" Help-menu action — kept
/// OUT of the app installer on purpose (minimal, non-invasive setup; the user opts in from the app).
/// Uses the User-scoped environment API, which persists to HKCU and broadcasts WM_SETTINGCHANGE so
/// newly launched shells pick up the change.
/// </summary>
public static class CliInstaller
{
    /// <summary>The directory holding wagtermctl.exe (it sits next to wagterm.exe / the ctl exe).</summary>
    public static string CliDir() => AppContext.BaseDirectory.TrimEnd('\\', '/');

    private static string[] UserPathParts()
    {
        string cur = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        return cur.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Add the ctl directory to the user PATH (idempotent). Returns a human-readable status.</summary>
    public static string Install()
    {
        string dir = CliDir();
        try
        {
            var parts = UserPathParts();
            if (parts.Any(p => SameDir(p, dir)))
                return "wagtermctl is already on your PATH -> " + dir;
            Environment.SetEnvironmentVariable("Path", string.Join(";", parts.Append(dir)),
                EnvironmentVariableTarget.User);
            return "added wagtermctl to your PATH -> " + dir + " (open a new shell to use it)";
        }
        catch (Exception ex) { return "failed to add to PATH: " + ex.Message; }
    }

    /// <summary>Remove the ctl directory from the user PATH. Returns a human-readable status.</summary>
    public static string Uninstall()
    {
        string dir = CliDir();
        try
        {
            var parts = UserPathParts();
            var kept = parts.Where(p => !SameDir(p, dir)).ToArray();
            if (kept.Length == parts.Length) return "wagtermctl was not on your PATH";
            Environment.SetEnvironmentVariable("Path", string.Join(";", kept), EnvironmentVariableTarget.User);
            return "removed wagtermctl from your PATH";
        }
        catch (Exception ex) { return "failed to remove from PATH: " + ex.Message; }
    }
}
