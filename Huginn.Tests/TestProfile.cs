using System;
using System.Runtime.CompilerServices;

namespace Huginn.Tests;

internal static class TestProfile
{
    /// <summary>
    /// Runs before any test touches Huginn, so its settings, log and port belong to a profile of
    /// their own rather than to the copy installed on the machine running the tests.
    /// </summary>
    [ModuleInitializer]
    internal static void Isolate() => Environment.SetEnvironmentVariable("HUGINN_PROFILE", "tests");
}
