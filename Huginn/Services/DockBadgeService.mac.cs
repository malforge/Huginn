using System;
using System.Runtime.InteropServices;

namespace Huginn.Services;

/// <summary>
/// Sets the dock badge on macOS using NSApplication via ObjC runtime interop.
/// </summary>
public sealed class DockBadgeService : IBadgeService
{
    public bool Initialize(IntPtr nativeHandle)
    {
        // No HWND needed on macOS — dock badge is app-global
        return true;
    }

    public void SetBadge(int count)
    {
        try
        {
            var nsApp = objc_getClass("NSApplication");
            var sharedApp = objc_msgSend_IntPtr(nsApp, sel_registerName("sharedApplication"));
            var dockTile = objc_msgSend_IntPtr(sharedApp, sel_registerName("dockTile"));

            var badgeText = count > 0 ? count.ToString() : "";
            var nsString = CreateNSString(badgeText);

            objc_msgSend_void(dockTile, sel_registerName("setBadgeLabel:"), nsString);
        }
        catch
        {
            // Non-critical — badge just won't update
        }
    }

    public void Dispose() => SetBadge(0);

    private static IntPtr CreateNSString(string str)
    {
        var nsStringClass = objc_getClass("NSString");
        var utf8 = System.Text.Encoding.UTF8.GetBytes(str + '\0');
        var handle = GCHandle.Alloc(utf8, GCHandleType.Pinned);
        try
        {
            var alloc = objc_msgSend_IntPtr(nsStringClass, sel_registerName("alloc"));
            return objc_msgSend_IntPtr_IntPtr(alloc,
                sel_registerName("initWithUTF8String:"), handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    // ObjC runtime P/Invoke
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector, IntPtr arg1);
}
