using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Huginn.Services;

/// <summary>
/// Resolves the correct platform-specific service implementations at runtime.
/// Uses Activator.CreateInstance with type names to avoid compile-time references
/// to platform-specific types that may not be present in the build.
/// </summary>
public static class PlatformFactory
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.TaskbarBadgeService", "Huginn")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.DockBadgeService", "Huginn")]
    public static IBadgeService CreateBadgeService()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Create<IBadgeService>("Huginn.Services.TaskbarBadgeService");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Create<IBadgeService>("Huginn.Services.DockBadgeService");
        return new NullBadgeService();
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.WinToastService", "Huginn")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.MacNotificationService", "Huginn")]
    public static INotificationService CreateNotificationService()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Create<INotificationService>("Huginn.Services.WinToastService");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Create<INotificationService>("Huginn.Services.MacNotificationService");
        return new NullNotificationService();
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.WinCredentialStore", "Huginn")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.KeychainCredentialStore", "Huginn")]
    public static ICredentialStore CreateCredentialStore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Create<ICredentialStore>("Huginn.Services.WinCredentialStore");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Create<ICredentialStore>("Huginn.Services.KeychainCredentialStore");
        return new FileCredentialStore();
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.WinAutoStartService", "Huginn")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, "Huginn.Services.MacAutoStartService", "Huginn")]
    public static IAutoStartService CreateAutoStartService()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Create<IAutoStartService>("Huginn.Services.WinAutoStartService");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Create<IAutoStartService>("Huginn.Services.MacAutoStartService");
        return new NullAutoStartService();
    }

    private static T Create<T>(string typeName)
    {
        var type = Type.GetType(typeName)
                   ?? throw new PlatformNotSupportedException($"Could not find type {typeName}");
        return (T)Activator.CreateInstance(type)!;
    }

    // Fallback no-op implementations for unsupported platforms
    private sealed class NullBadgeService : IBadgeService
    {
        public bool Initialize(IntPtr nativeHandle) => false;
        public void SetBadge(int count) { }
        public void Dispose() { }
    }

    private sealed class NullNotificationService : INotificationService
    {
        public void RegisterActivation() { }
        public void ShowNewPullRequest(string title, string author, string repo, string url) { }
        public void ShowBuildFailed(string pipelineName, string branch, string url) { }
    }

    private sealed class FileCredentialStore : ICredentialStore
    {
        private static readonly bool _warned;

        static FileCredentialStore()
        {
            _warned = false;
        }

        private static string FilePath(string name) =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Huginn", $".cred_{name}");

        public string? Get(string targetName)
        {
            WarnOnce();
            var path = FilePath(targetName);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }

        public bool Set(string targetName, string secret, string? userName = null)
        {
            WarnOnce();
            try
            {
                var path = FilePath(targetName);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, secret);
                return true;
            }
            catch { return false; }
        }

        public bool Delete(string targetName)
        {
            try { System.IO.File.Delete(FilePath(targetName)); return true; }
            catch { return false; }
        }

        private static void WarnOnce()
        {
            if (!_warned)
                Log.Warn("Using plaintext FileCredentialStore fallback — credentials are NOT encrypted. Use Windows or macOS for secure storage.");
        }
    }

    private sealed class NullAutoStartService : IAutoStartService
    {
        public bool IsEnabled => false;
        public void Enable(string executablePath) { }
        public void Disable() { }
    }
}
