using System.Reflection;

namespace KeorMon;

/// <summary>Application version, read from the assembly (set via &lt;Version&gt; in the csproj).</summary>
public static class AppVersion
{
    public static string Short { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";
}
