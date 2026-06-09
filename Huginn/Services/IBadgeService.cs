using System;

namespace Huginn.Services;

/// <summary>
/// Renders a numeric badge on the app's taskbar/dock icon.
/// </summary>
public interface IBadgeService : IDisposable
{
    /// <summary>
    /// Initialize with the platform-native window handle.
    /// Windows: IntPtr (HWND). macOS: not needed (pass IntPtr.Zero).
    /// </summary>
    bool Initialize(IntPtr nativeHandle);

    void SetBadge(int count);
}
