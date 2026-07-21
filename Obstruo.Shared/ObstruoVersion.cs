namespace Obstruo.Shared;

/// <summary>
/// Single source of truth for product version strings. Previously the version
/// lived independently in the installer, the DB config seed, and the EULA
/// dialog — three copies that could silently drift. Bump versions HERE only.
/// </summary>
public static class ObstruoVersion
{
    /// <summary>Product version — drives upgrade detection and the DB seed.</summary>
    public const string Current = "1.0.4";

    /// <summary>EULA revision recorded on acceptance.</summary>
    public const string EulaVersion = "1.0";

    public const string ProductName = "Obstruo Security";
    public const string Publisher = "DarkNem4377";

    /// <summary>
    /// Short git commit stamped into AssemblyInformationalVersion at build time
    /// (see Directory.Build.props), or "unknown" when the build carried no
    /// revision id. Lets a running install identify exactly which build it is —
    /// the concrete answer to finding L3's "show the build commit in the dashboard".
    /// </summary>
    public static string CommitHash { get; } = ResolveCommitHash();

    /// <summary>e.g. "1.0.1 (a1b2c3d)" — for the About box / dashboard footer.</summary>
    public static string DisplayVersion =>
        CommitHash == "unknown" ? Current : $"{Current} ({CommitHash})";

    private static string ResolveCommitHash()
        => ParseCommit(typeof(ObstruoVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion);

    /// <summary>
    /// Extracts the git commit from an AssemblyInformationalVersion of the form
    /// "&lt;version&gt;+&lt;sourceRevisionId&gt;", or "unknown" when unstamped.
    /// Pure — unit-tested independently of the build environment.
    /// </summary>
    public static string ParseCommit(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion)) return "unknown";

        var plus = informationalVersion.IndexOf('+');
        if (plus < 0 || plus >= informationalVersion.Length - 1) return "unknown";

        // Trim any trailing whitespace the build may append after the id.
        return informationalVersion[(plus + 1)..].Trim();
    }
}
