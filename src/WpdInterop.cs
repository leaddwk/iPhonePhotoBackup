using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace iPhonePhotoBackup
{
    // The method order and property keys follow PortableDeviceApi.h, PortableDeviceTypes.h
    // and PortableDevice.h. Only the vtable prefix used by the reader is declared.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Format;
        public uint Id;

        public PropertyKey(string format, uint id)
        {
            Format = new Guid(format);
            Id = id;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariantVector
    {
        public uint Count;
        public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public double Date;
        [FieldOffset(8)] private PropVariantVector vector;
    }

    [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceManager
    {
        [PreserveSig] int GetDevices(IntPtr ids, ref uint count);
        void RefreshDeviceList();
        [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, ref uint length);
        void GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr name, ref uint length);
        [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string id,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, ref uint length);
    }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDevice
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceValues client);
        void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
        void Content(out IPortableDeviceContent content);
        void Capabilities(out IntPtr capabilities);
        void Cancel();
        void Close();
    }

    [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceContent
    {
        void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parent, IPortableDeviceValues filter,
            out IEnumPortableDeviceObjectIds objects);
        void Properties(out IPortableDeviceProperties properties);
        void Transfer(out IPortableDeviceResources resources);
    }

    [ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumPortableDeviceObjectIds
    {
        [PreserveSig] int Next(uint count, IntPtr ids, out uint fetched);
    }

    [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceProperties
    {
        void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string id, out IPortableDeviceKeyCollection keys);
        void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key,
            out IPortableDeviceValues attributes);
        void GetValues([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceKeyCollection keys,
            out IPortableDeviceValues values);
    }

    [ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceResources
    {
        void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string id, out IPortableDeviceKeyCollection keys);
        void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key,
            out IPortableDeviceValues attributes);
        void GetStream([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, uint mode,
            ref uint bufferSize, out IStream stream);
    }

    [ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceKeyCollection
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void Add(ref PropertyKey key);
    }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceValues
    {
        void GetCount(out uint count);
        void GetAt(uint index, IntPtr key, IntPtr value);
        void SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
        void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref PropertyKey key, out IntPtr value);
        void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        void GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
        void SetSignedIntegerValue(ref PropertyKey key, int value);
        void GetSignedIntegerValue(ref PropertyKey key, out int value);
        void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
        [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
        void SetSignedLargeIntegerValue(ref PropertyKey key, long value);
        void GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
        void SetFloatValue(ref PropertyKey key, float value);
        void GetFloatValue(ref PropertyKey key, out float value);
        void SetErrorValue(ref PropertyKey key, int value);
        void GetErrorValue(ref PropertyKey key, out int value);
        void SetKeyValue(ref PropertyKey key, ref PropertyKey value);
        void GetKeyValue(ref PropertyKey key, out PropertyKey value);
        void SetBoolValue(ref PropertyKey key, int value);
        void GetBoolValue(ref PropertyKey key, out int value);
        void SetIUnknownValue(ref PropertyKey key, IntPtr value);
        void GetIUnknownValue(ref PropertyKey key, out IntPtr value);
        void SetGuidValue(ref PropertyKey key, ref Guid value);
        [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
    }
}
