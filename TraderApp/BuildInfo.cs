using System.Reflection;

namespace TraderApp;

/// <summary>
/// Version stamped into the assembly at build time by Directory.Build.props.
/// Reads assembly metadata rather than AssemblyVersion because the build number
/// is a timestamp, which does not fit AssemblyVersion's 16-bit parts.
/// </summary>
public static class BuildInfo
{
    static BuildInfo()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;

        var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                      .ToDictionary(a => a.Key, a => a.Value ?? "", StringComparer.OrdinalIgnoreCase);

        Version = asm.GetName().Version?.ToString(3) ?? "0.0.0";
        BuildNumber = Get(meta, "BuildNumber", "dev");
        BuiltUtc = Get(meta, "BuildTimeUtc", "unknown");

        var sha = Get(meta, "GitSha", "local");
        GitSha = sha.Length > 7 ? sha[..7] : sha;
    }

    public static string Version { get; }
    public static string BuildNumber { get; }
    public static string GitSha { get; }
    public static string BuiltUtc { get; }

    /// <summary>e.g. "v1.0.0 build 260918.1642 (a1b2c3d)"</summary>
    public static string Display => $"v{Version} build {BuildNumber} ({GitSha})";

    private static string Get(IReadOnlyDictionary<string, string> meta, string key, string fallback) =>
        meta.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}
