using Xunit;

namespace Huginn.Tests;

/// <summary>
/// Tests that write the test profile's settings file. They share one file, so they run one at a
/// time rather than in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SettingsFile
{
    public const string Name = "Settings file";
}
