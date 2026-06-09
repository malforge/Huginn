using System;
using System.Linq;
using System.Reflection;

namespace Huginn.Services;

/// <summary>
/// Reads the build timestamp embedded by MSBuild at compile time.
/// Used for self-update detection (compare against latest repo commit).
/// </summary>
public static class BuildInfo
{
    public static DateTimeOffset BuildDate { get; } = ReadBuildTimestamp();

    private static DateTimeOffset ReadBuildTimestamp()
    {
        var attr = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestamp");

        if (attr != null && DateTimeOffset.TryParse(attr.Value, out var dt))
            return dt;

        return DateTimeOffset.MinValue;
    }
}
