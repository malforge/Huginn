using System;
using System.IO;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

public sealed class LogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"huginn-log-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void Two_writers_sharing_the_file_keep_every_line()
    {
        // Two processes, as the app and an MCP server started over stdio are.
        using (StreamWriter app = Log.OpenShared(_path)!)
        using (StreamWriter other = Log.OpenShared(_path)!)
        {
            Log.AppendLine(app, "app one");
            Log.AppendLine(other, "other one");
            Log.AppendLine(app, "app two");
            Log.AppendLine(other, "other two");
        }

        Assert.Equal(["app one", "other one", "app two", "other two"], File.ReadAllLines(_path));
    }
}
