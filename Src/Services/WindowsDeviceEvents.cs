using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RetroGameCard.Services;

/// <summary>Device notification actions (CM_NOTIFY_ACTION).</summary>
public enum DeviceAction : uint
{
    InterfaceArrival = 0,
    InterfaceRemoval = 1,
    QueryRemove = 2,
    QueryRemoveFailed = 3,
    RemovePending = 4,
    RemoveComplete = 5,
    CustomEvent = 6
}

/// <summary>
/// Windows device notifications (cfgmgr32 <c>CM_Register_Notification</c>, Windows 8+), the ones
/// Explorer reacts to. No window needed: callbacks run on a system thread pool thread, so handlers
/// must be quick and must not dispose their own registration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceEvents : IDisposable
{
    /// <summary>GUID_DEVINTERFACE_VOLUME</summary>
    public static readonly Guid VolumeInterface = new("53F5630D-B6BF-11D0-94F2-00A0C91EFB8B");

    /// <summary>GUID_DEVINTERFACE_DISK</summary>
    public static readonly Guid DiskInterface = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    private const uint CrSuccess = 0;
    private const uint FilterTypeDeviceInterface = 0, FilterTypeDeviceHandle = 1;
    private const uint ShareReadWrite = 0x3, OpenExisting = 3;

    // CM_NOTIFY_FILTER: cbSize, Flags, FilterType, Reserved, then a union whose largest member is
    // WCHAR InstanceId[200] (400 bytes): 416 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;  // DeviceInterface
        [FieldOffset(16)] public IntPtr Target;   // DeviceHandle
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint NotifyCallback(IntPtr notify, IntPtr context, DeviceAction action, IntPtr eventData,
        uint eventDataSize);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Register_Notification(ref NotifyFilter filter, IntPtr context,
        NotifyCallback callback, out IntPtr notifyContext);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(IntPtr notifyContext);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    private readonly NotifyCallback _callback; // kept alive as long as the registration
    private IntPtr _registration;

    private WindowsDeviceEvents(Action<DeviceAction> handler)
    {
        _callback = (_, _, action, _, _) =>
        {
            try
            {
                handler(action);
            }
            catch (Exception exception)
            {
                Log.Error("Device notification handler failed", exception);
            }

            return CrSuccess;
        };
    }

    /// <summary>Arrival / removal of any device exposing <paramref name="interfaceClass"/>.</summary>
    public static WindowsDeviceEvents RegisterInterface(Guid interfaceClass, Action<DeviceAction> handler)
    {
        var filter = new NotifyFilter
        {
            Size = (uint)Marshal.SizeOf<NotifyFilter>(),
            FilterType = FilterTypeDeviceInterface,
            ClassGuid = interfaceClass
        };
        return Register(filter, handler);
    }

    /// <summary>
    /// Query remove / removal of the device behind <paramref name="handle"/>. On
    /// <see cref="DeviceAction.QueryRemove"/> the handle must be closed, or the eject is refused.
    /// </summary>
    public static WindowsDeviceEvents RegisterHandle(SafeFileHandle handle, Action<DeviceAction> handler)
    {
        var filter = new NotifyFilter
        {
            Size = (uint)Marshal.SizeOf<NotifyFilter>(),
            FilterType = FilterTypeDeviceHandle,
            Target = handle.DangerousGetHandle()
        };
        return Register(filter, handler);
    }

    private static WindowsDeviceEvents Register(NotifyFilter filter, Action<DeviceAction> handler)
    {
        var events = new WindowsDeviceEvents(handler);
        var result = CM_Register_Notification(ref filter, IntPtr.Zero, events._callback, out events._registration);
        if (result != CrSuccess)
            throw new InvalidOperationException($"CM_Register_Notification failed (CONFIGRET 0x{result:X}).");
        return events;
    }

    /// <summary>
    /// Opens a volume (<c>E:\</c> → <c>\\.\E:</c>) without any access right, only to receive its
    /// notifications: nothing is read. Throws when it can't be opened.
    /// </summary>
    public static SafeFileHandle OpenVolume(string mountPoint)
    {
        if (mountPoint.Length is < 2 or > 3 || !char.IsAsciiLetter(mountPoint[0]) || mountPoint[1] != ':')
            throw new ArgumentException($"Not a drive letter: {mountPoint}", nameof(mountPoint));

        var handle = CreateFile(@"\\.\" + mountPoint[..2], 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }

        return handle;
    }

    /// <summary>Stops the notifications. Never call it from the handler.</summary>
    public void Dispose()
    {
        var registration = _registration;
        _registration = IntPtr.Zero;
        if (registration != IntPtr.Zero)
            CM_Unregister_Notification(registration);
        GC.KeepAlive(_callback);
    }
}
