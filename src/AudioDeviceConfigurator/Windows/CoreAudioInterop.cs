using System.Runtime.InteropServices;

namespace AudioDeviceConfigurator.Windows;

/// <summary>Core Audio COM declarations. Hand-written so the app takes no third-party dependency.</summary>
internal static class CoreAudio
{
    public const int SOk = 0;

    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public const int EDataFlowRender = 0;
    public const int ERoleConsole = 0;
    public const int DeviceStateActive = 0x00000001;
    public const uint StgmRead = 0;
    public const int AudclntSharemodeExclusive = 1;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);

        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);

        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);

        [PreserveSig] int GetAt(int index, out PropertyKey key);

        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr sessionGuid);

        [PreserveSig] int GetBufferSize(out uint frames);

        [PreserveSig] int GetStreamLatency(out long latency);

        [PreserveSig] int GetCurrentPadding(out uint padding);

        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);

        [PreserveSig] int GetMixFormat(out IntPtr format);

        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig] int Start();

        [PreserveSig] int Stop();

        [PreserveSig] int Reset();

        [PreserveSig] int SetEventHandle(IntPtr handle);

        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT is 24 bytes on x64: 8 bytes of type/reserved fields plus a 16-byte union.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        private const ushort VtLpwstr = 31;
        private const ushort VtClsid = 72;

        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;

        public string? AsString() =>
            VariantType == VtLpwstr ? Marshal.PtrToStringUni(PointerValue) : null;

        /// <summary>
        /// VT_CLSID stores a pointer to the GUID rather than the GUID itself, which is how
        /// PKEY_Device_ContainerId arrives. Returned in the braced form Windows displays.
        /// </summary>
        public string? AsGuid()
        {
            if (VariantType != VtClsid || PointerValue == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[16];
            Marshal.Copy(PointerValue, bytes, 0, 16);
            return new Guid(bytes).ToString("B").ToUpperInvariant();
        }
    }

    // PKEY_Device_FriendlyName, PKEY_Device_DeviceDesc, PKEY_DeviceClass_IconPath equivalents.
    public static PropertyKey PkeyDeviceFriendlyName =>
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static PropertyKey PkeyDeviceDeviceDesc =>
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    public static PropertyKey PkeyDeviceInstanceId =>
        new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    public static PropertyKey PkeyDeviceContainerId =>
        new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant variant);
}
