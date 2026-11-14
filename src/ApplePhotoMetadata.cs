using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace iPhonePhotoBackup
{
    // Apple's paired USB File Conduit connection. Read the Photos database and its journal only.
    internal sealed class ApplePhotoMetadata : IDisposable
    {
        private const string Mobile = "MobileDevice.dll";
        private const string Core = "CoreFoundation.dll";
        private const string RemoteDatabase = "/PhotoData/Photos.sqlite";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CFA6LPAA");
        private static readonly object LoadLock = new object();
        private static bool librariesLoaded;
        private IntPtr devices;
        private IntPtr device;
        private IntPtr service;
        private bool connected;
        private bool session;
        private ulong packetNumber;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr AddDllDirectory(string path);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern bool RemoveDllDirectory(IntPtr cookie);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_Interface_PropertyW(string path, ref PropertyKey key, out uint type,
            [Out] byte[] value, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, uint size, uint flags);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CFArrayGetCount(IntPtr array);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, IntPtr index);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] value, uint encoding);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFStringGetCString(IntPtr value, [Out] byte[] buffer, IntPtr length, uint encoding);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        private static extern void CFRelease(IntPtr value);

        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr AMDCreateDeviceList();
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceGetInterfaceType(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr AMDeviceCopyDeviceIdentifier(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceConnect(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceDisconnect(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceIsPaired(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceValidatePairing(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceStartSession(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceStopSession(IntPtr device);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDeviceSecureStartService(IntPtr device, IntPtr serviceName, IntPtr options, out IntPtr service);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern void AMDServiceConnectionInvalidate(IntPtr service);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDServiceConnectionSend(IntPtr service, IntPtr buffer, uint length);
        [DllImport(Mobile, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AMDServiceConnectionReceive(IntPtr service, IntPtr buffer, uint length);

        internal static void LoadLibraries()
        {
            lock (LoadLock)
            {
                if (librariesLoaded) return;
                string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Apple");
                string core = Path.Combine(common, "Apple Application Support");
                string mobile = Path.Combine(common, "Mobile Device Support");
                if (TryLoad(core, mobile)) { librariesLoaded = true; return; }

                // The Microsoft Store package can provide both libraries together.
                using (var package = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications"))
                {
                    if (package != null)
                    {
                        foreach (string name in package.GetSubKeyNames())
                        {
                            if (!name.StartsWith("AppleInc.AppleDevices_", StringComparison.OrdinalIgnoreCase)) continue;
                            using (var entry = package.OpenSubKey(name))
                            {
                                string manifest = entry == null ? null : entry.GetValue("Path") as string;
                                if (String.IsNullOrEmpty(manifest) || !Path.IsPathRooted(manifest)) continue;
                                string folder = Directory.Exists(manifest) ? manifest : Path.GetDirectoryName(manifest);
                                if (TryLoad(folder, folder)) { librariesLoaded = true; return; }
                            }
                        }
                    }
                }
                throw new IOException("Windows에서 호환되는 Apple Mobile Device Support 모듈을 사용할 수 없습니다.");
            }
        }

        private static bool TryLoad(string coreDirectory, string mobileDirectory)
        {
            string corePath = Path.Combine(coreDirectory, Core);
            string mobilePath = Path.Combine(mobileDirectory, Mobile);
            if (!File.Exists(corePath) || !File.Exists(mobilePath)) return false;
            IntPtr cookie = AddDllDirectory(coreDirectory);
            const uint loadFromDllDirectoryAndDefault = 0x1100;
            try
            {
                return LoadLibraryEx(corePath, IntPtr.Zero, loadFromDllDirectoryAndDefault) != IntPtr.Zero &&
                    LoadLibraryEx(mobilePath, IntPtr.Zero, loadFromDllDirectoryAndDefault) != IntPtr.Zero;
            }
            finally { if (cookie != IntPtr.Zero) RemoveDllDirectory(cookie); }
        }

        public ApplePhotoMetadata(Phone phone, string identity)
        {
            LoadLibraries();
            try
            {
                string pnp = UsbHardwareId(phone.Id);
                devices = AMDCreateDeviceList();
                if (devices != IntPtr.Zero)
                {
                    long count = CFArrayGetCount(devices).ToInt64();
                    for (long i = 0; i < count; i++)
                    {
                        IntPtr candidate = CFArrayGetValueAtIndex(devices, new IntPtr(i));
                        if (AMDeviceGetInterfaceType(candidate) != 1) continue; // USB
                        string udid = ReadIdentifier(candidate);
                        if (!MatchesPhone(udid, pnp, identity) && !MatchesPhone(udid, phone.Id, identity)) continue;
                        device = candidate;
                        break;
                    }
                }

                if (device == IntPtr.Zero) throw new IOException("선택한 아이폰의 Apple USB 연결을 찾지 못했습니다.");
                Check(AMDeviceConnect(device));
                connected = true;
                if (AMDeviceIsPaired(device) == 0) throw new IOException("아이폰에서 이 PC를 신뢰해 주세요.");
                Check(AMDeviceValidatePairing(device));
                Check(AMDeviceStartSession(device));
                session = true;
                IntPtr name = CFStringCreateWithCString(IntPtr.Zero, Encoding.ASCII.GetBytes("com.apple.afc\0"), 0x08000100);
                if (name == IntPtr.Zero) throw new IOException("Apple USB 서비스 이름을 열 수 없습니다.");
                try { Check(AMDeviceSecureStartService(device, name, IntPtr.Zero, out service)); }
                finally { CFRelease(name); }
                if (service == IntPtr.Zero) throw new IOException("Apple USB 서비스를 열 수 없습니다.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static string ReadIdentifier(IntPtr candidate)
        {
            IntPtr value = AMDeviceCopyDeviceIdentifier(candidate);
            if (value == IntPtr.Zero) return "";
            try
            {
                var bytes = new byte[128];
                return CFStringGetCString(value, bytes, new IntPtr(bytes.Length), 0x08000100)
                    ? Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0)) : "";
            }
            finally { CFRelease(value); }
        }

        internal static bool MatchesPhone(string udid, string pnp, string identity)
        {
            string compact = udid.Replace("-", "");
            if (compact.Length < 24) return false;
            if (String.Equals(compact, identity.Replace("-", ""), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string segment in pnp.Split('\\', '#', '&'))
                if (String.Equals(compact, segment.Replace("-", ""), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string UsbHardwareId(string interfacePath)
        {
            var key = new PropertyKey("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
            var data = new byte[8192];
            uint size = (uint)data.Length;
            uint type;
            uint node;
            if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out type, data, ref size, 0) != 0 || type != 18 ||
                CM_Locate_DevNodeW(out node, Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0'), 0) != 0) return "";
            for (int level = 0; level < 12; level++)
            {
                var label = new StringBuilder(1024);
                if (CM_Get_Device_IDW(node, label, (uint)label.Capacity, 0) != 0) break;
                string id = label.ToString();
                if (id.StartsWith("USB\\VID_05AC", StringComparison.OrdinalIgnoreCase) &&
                    id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0) return id;
                uint parent;
                if (CM_Get_Parent(out parent, node, 0) != 0) break;
                node = parent;
            }
            return "";
        }

        public void CopyDatabase(string path, CancellationToken cancel)
        {
            CopyFile(RemoteDatabase, path, false, cancel);
            CopyFile(RemoteDatabase + "-wal", path + "-wal", true, cancel);
        }

        private void CopyFile(string remote, string local, bool optional, CancellationToken cancel)
        {
            byte[] name = Encoding.UTF8.GetBytes(remote + "\0");
            var payload = new byte[8 + name.Length];
            payload[0] = 1; // AFC_FOPEN_RDONLY
            Array.Copy(name, 0, payload, 8, name.Length);
            byte[] opened = Command(0x0d, payload, 0x0e, 8, optional ? 8UL : 0UL, cancel);
            if (opened == null) return;
            if (opened.Length != 8) throw new IOException("잘못된 Apple USB 파일 응답입니다.");
            try
            {
                const int chunkSize = 128 * 1024;
                payload = new byte[16];
                Array.Copy(opened, payload, 8);
                Array.Copy(BitConverter.GetBytes((ulong)chunkSize), 0, payload, 8, 8);
                using (var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    while (true)
                    {
                        byte[] data = Command(0x0f, payload, 2, chunkSize, 14, cancel);
                        if (data == null || data.Length == 0) break;
                        if (output.Length + data.Length > 1024L * 1024 * 1024)
                            throw new IOException("사진 보관함 정보 파일이 1GB를 초과합니다.");
                        output.Write(data, 0, data.Length);
                    }
                }
            }
            finally
            {
                if (!cancel.IsCancellationRequested)
                {
                    try { Command(0x14, opened, 1, 8, 0, cancel); }
                    catch (IOException) { }
                }
            }
        }

        private byte[] Command(ulong operation, byte[] payload, ulong response, int maxLength, ulong optionalStatus, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            byte[] request = new byte[40 + payload.Length];
            Array.Copy(Magic, request, 8);
            Array.Copy(BitConverter.GetBytes((ulong)request.Length), 0, request, 8, 8);
            Array.Copy(BitConverter.GetBytes((ulong)request.Length), 0, request, 16, 8);
            Array.Copy(BitConverter.GetBytes(++packetNumber), 0, request, 24, 8);
            Array.Copy(BitConverter.GetBytes(operation), 0, request, 32, 8);
            Array.Copy(payload, 0, request, 40, payload.Length);
            Transfer(request, AMDServiceConnectionSend, cancel);

            byte[] header = new byte[40];
            Transfer(header, AMDServiceConnectionReceive, cancel);
            ulong length = BitConverter.ToUInt64(header, 8);
            ulong firstLength = BitConverter.ToUInt64(header, 16);
            ulong type = BitConverter.ToUInt64(header, 32);
            if (Encoding.ASCII.GetString(header, 0, 8) != "CFA6LPAA" ||
                BitConverter.ToUInt64(header, 24) != packetNumber ||
                firstLength < 40 || firstLength > length || length < 40 || length > (ulong)(40 + Math.Max(8, maxLength)))
                throw new IOException("잘못된 Apple USB 응답을 받았습니다.");
            var data = new byte[(int)length - 40];
            Transfer(data, AMDServiceConnectionReceive, cancel);
            if (type == 1 && data.Length == 8)
            {
                ulong code = BitConverter.ToUInt64(data, 0);
                if (code == 0 && response == 1) return data;
                if (code == optionalStatus && optionalStatus != 0) return null;
                throw new IOException("Apple USB 서비스에서 보관함 파일을 읽을 수 없습니다. (AFC " + code.ToString(CultureInfo.InvariantCulture) + ")");
            }
            if (type != response || data.Length > maxLength) throw new IOException("예상하지 못한 Apple USB 응답입니다.");
            return data;
        }

        private delegate int ServiceTransfer(IntPtr service, IntPtr buffer, uint length);
        private void Transfer(byte[] bytes, ServiceTransfer transfer, CancellationToken cancel)
        {
            if (bytes.Length == 0) return;
            var buffer = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                for (int offset = 0; offset < bytes.Length;)
                {
                    cancel.ThrowIfCancellationRequested();
                    int n = transfer(service, IntPtr.Add(buffer.AddrOfPinnedObject(), offset), (uint)(bytes.Length - offset));
                    if (n <= 0 || n > bytes.Length - offset) throw new IOException("Apple USB 연결에서 데이터를 받지 못했습니다.");
                    offset += n;
                }
            }
            finally { buffer.Free(); }
        }

        private static void Check(int error)
        {
            if (error != 0) throw new IOException("Apple USB 보관함에 연결할 수 없습니다. (0x" + error.ToString("X8") + ")");
        }

        public void Dispose()
        {
            if (service != IntPtr.Zero)
            {
                AMDServiceConnectionInvalidate(service);
                CFRelease(service);
            }
            if (session) AMDeviceStopSession(device);
            if (connected) AMDeviceDisconnect(device);
            if (devices != IntPtr.Zero) CFRelease(devices);
        }
    }
}
