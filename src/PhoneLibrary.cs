using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace iPhonePhotoBackup
{
    internal sealed class Phone
    {
        public string Id;
        public string Name;

        public override string ToString() { return Name; }
    }

    internal sealed class MediaFile
    {
        public string Id;
        public string PersistentId;
        public string RelativePath;
        public string DeviceName;
        public bool IsVideo;
        public long? Size;
        public DateTime? Created;
        public DateTime? Modified;
        public DateTime? Authored;
    }

    internal sealed class PhoneLibrary : IDisposable
    {
        private const string ObjectFormat = "ef6b490d-5cd8-437a-affc-da8b60ee4a3c";
        private const string ClientFormat = "204d9f0c-2292-4080-9f42-40664e70f859";
        private static readonly PropertyKey Name = new PropertyKey(ObjectFormat, 4);
        private static readonly PropertyKey PersistentId = new PropertyKey(ObjectFormat, 5);
        private static readonly PropertyKey ContentType = new PropertyKey(ObjectFormat, 7);
        private static readonly PropertyKey Size = new PropertyKey(ObjectFormat, 11);
        private static readonly PropertyKey OriginalName = new PropertyKey(ObjectFormat, 12);
        private static readonly PropertyKey Created = new PropertyKey(ObjectFormat, 18);
        private static readonly PropertyKey Modified = new PropertyKey(ObjectFormat, 19);
        private static readonly PropertyKey Authored = new PropertyKey(ObjectFormat, 20);
        private static readonly PropertyKey DeviceSerial = new PropertyKey("26d4979a-e643-4626-9e2b-736dc0c92fdc", 9);
        private static readonly PropertyKey FunctionalCategory = new PropertyKey("8f052d93-abca-4fc5-a5ac-b01df4dbe598", 2);
        private static readonly PropertyKey DefaultResource = new PropertyKey("e81e79be-34f0-41bf-b53f-f1a06ae87842", 0);
        private static readonly PropertyKey Thumbnail = new PropertyKey("c7c407ba-98fa-46b5-9960-23fec124cfde", 0);
        private static readonly Guid Folder = new Guid("27e2e392-a111-48e0-ab0c-e17705a05f85");
        private static readonly Guid Image = new Guid("ef2107d5-a52a-4243-a26b-62d4176d7603");
        private static readonly Guid Video = new Guid("9261b03c-3d78-4519-85e3-02c5e1f50bb9");
        private static readonly Guid Functional = new Guid("99ed0160-17ff-4c44-9d98-1d7a6f941921");
        private static readonly Guid Storage = new Guid("23f05bbc-15de-4c2a-a55b-a9af5ce412ef");
        private static readonly Regex CameraFolder = new Regex(@"^(DCIM|Internal Storage|\d{6}[_\d]*|\d{3}(APPLE|CLOUD))$", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> ExcludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Download", "Downloads", "Documents", "Files", "iCloud Drive", "다운로드", "문서", "파일", "나의 iPhone"
        };
        private static readonly HashSet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".aae", ".avci", ".avif", ".bmp", ".dng", ".gif", ".heic", ".heif", ".heics", ".hif", ".ico",
            ".j2k", ".jfif", ".jp2", ".jpeg", ".jpg", ".jxl", ".png", ".psd", ".raw", ".svg", ".tif", ".tiff",
            ".webp"
        };
        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".3g2", ".3gp", ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts",
            ".qt", ".ts", ".webm", ".wmv"
        };

        private IPortableDevice device;
        private IPortableDeviceContent content;
        private IPortableDeviceProperties properties;
        private IPortableDeviceResources resources;
        private IPortableDeviceKeyCollection objectKeys;
        private bool opened;

        public string Identity { get; private set; }

        public static List<Phone> FindPhones()
        {
            var phones = new List<Phone>();
            var manager = Create<IPortableDeviceManager>("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
            try
            {
                manager.RefreshDeviceList();
                uint count = 0;
                Marshal.ThrowExceptionForHR(manager.GetDevices(IntPtr.Zero, ref count));
                foreach (var id in ReadIds(count, manager.GetDevices))
                {
                    string name = ReadDeviceLabel(id, manager.GetDeviceFriendlyName);
                    string maker = ReadDeviceLabel(id, manager.GetDeviceManufacturer);
                    bool appleUsb = id.IndexOf("vid_05ac", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool applePhone = name.IndexOf("iPhone", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        maker.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (appleUsb || applePhone)
                        phones.Add(new Phone { Id = id, Name = String.IsNullOrWhiteSpace(name) ? "Apple iPhone" : name });
                }
            }
            finally { Release(manager); }
            return phones;
        }

        public static int? AppleUsbProblemCode()
        {
            try
            {
                using (var query = new ManagementObjectSearcher("SELECT ConfigManagerErrorCode FROM Win32_PnPEntity " +
                    "WHERE Present = TRUE AND DeviceID LIKE 'USB%VID[_]05AC%' AND ConfigManagerErrorCode > 0"))
                using (var devices = query.Get())
                {
                    int? code = null;
                    foreach (ManagementObject device in devices)
                    {
                        using (device)
                        {
                            int value = Convert.ToInt32(device["ConfigManagerErrorCode"]);
                            if (value == 10) return value;
                            if (value > 0) code = value;
                        }
                    }
                    return code;
                }
            }
            catch (ManagementException) { return null; }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static bool IsDisconnectedError(Exception error)
        {
            switch (unchecked((uint)error.HResult))
            {
                case 0x80070002: // ERROR_FILE_NOT_FOUND
                case 0x80070003: // ERROR_PATH_NOT_FOUND
                case 0x80070015: // ERROR_NOT_READY
                case 0x8007048F: // ERROR_DEVICE_NOT_CONNECTED
                case 0x80010108: // RPC_E_DISCONNECTED
                    return true;
                default: return false;
            }
        }

        public PhoneLibrary(Phone phone)
        {
            var client = Create<IPortableDeviceValues>("0c15d503-d017-47ce-9016-7b3f978721cc");
            try
            {
                var clientName = new PropertyKey(ClientFormat, 2);
                var access = new PropertyKey(ClientFormat, 9);
                client.SetStringValue(ref clientName, "iPhone Photo Backup");
                client.SetUnsignedIntegerValue(ref access, 0x80000000); // GENERIC_READ
                device = Create<IPortableDevice>("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
                device.Open(phone.Id, client);
                opened = true;
                device.Content(out content);
                content.Properties(out properties);
                content.Transfer(out resources);
                objectKeys = Create<IPortableDeviceKeyCollection>("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
                foreach (var property in new[] { Name, PersistentId, ContentType, Size, OriginalName, Created, Modified, Authored, FunctionalCategory })
                {
                    var key = property;
                    objectKeys.Add(ref key);
                }

                Identity = phone.Id.ToUpperInvariant();
                var serialKeys = Create<IPortableDeviceKeyCollection>("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
                IPortableDeviceValues values = null;
                try
                {
                    var key = DeviceSerial;
                    serialKeys.Add(ref key);
                    properties.GetValues("DEVICE", serialKeys, out values);
                    string serial = ReadString(values, key);
                    if (!String.IsNullOrWhiteSpace(serial)) Identity = serial.Trim().ToUpperInvariant();
                }
                catch (COMException) { }
                finally
                {
                    Release(values);
                    Release(serialKeys);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally { Release(client); }
        }

        public List<MediaFile> Scan(CancellationToken cancel, Action<int> progress)
        {
            var files = new List<MediaFile>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            Walk("DEVICE", "", false, visited, files, cancel, progress);
            return files;
        }

        private void Walk(string parent, string parentPath, bool inCameraStorage, HashSet<string> visited,
            List<MediaFile> files, CancellationToken cancel, Action<int> progress)
        {
            IEnumPortableDeviceObjectIds objects;
            content.EnumObjects(0, parent, null, out objects);
            try
            {
                const uint batchSize = 64;
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var ids = ReadIds(batchSize, delegate(IntPtr buffer, ref uint count)
                    {
                        return objects.Next(batchSize, buffer, out count);
                    });
                    foreach (string id in ids)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (!visited.Add(id)) continue;
                        IPortableDeviceValues values;
                        properties.GetValues(id, objectKeys, out values);
                        try
                        {
                            string name = ReadString(values, Name);
                            Guid type = ReadGuid(values, ContentType);
                            Guid category = ReadGuid(values, FunctionalCategory);
                            bool isStorage = category == Storage || (type == Functional && category == Guid.Empty);
                            bool isFolder = type == Folder || (type == Guid.Empty && CameraFolder.IsMatch(name));
                            if (isStorage || isFolder)
                            {
                                if (ExcludedFolders.Contains(name)) continue;
                                if (!inCameraStorage && !isStorage && !CameraFolder.IsMatch(name)) continue;
                                string folderPath = isStorage && parent == "DEVICE" ? parentPath :
                                    Path.Combine(parentPath, BackupPaths.SafeName(name, "폴더-" + BackupPaths.HashText(id).Substring(0, 8)));
                                Walk(id, folderPath, true, visited, files, cancel, progress);
                                continue;
                            }
                            if (type == Functional) continue;

                            string originalName = ReadString(values, OriginalName);
                            if (String.IsNullOrWhiteSpace(originalName)) originalName = name;
                            string fallback = "미디어-" + BackupPaths.HashText(id).Substring(0, 12);
                            string fileName = BackupPaths.SafeName(originalName, fallback);
                            string extension = Path.GetExtension(fileName);
                            bool isVideo = type == Video || VideoExtensions.Contains(extension);
                            if (type != Image && !isVideo && !MediaExtensions.Contains(extension)) continue;
                            ulong size;
                            var sizeKey = Size;
                            files.Add(new MediaFile
                            {
                                Id = id,
                                PersistentId = ReadString(values, PersistentId),
                                RelativePath = Path.Combine(parentPath, fileName),
                                DeviceName = name,
                                IsVideo = isVideo,
                                Size = values.GetUnsignedLargeIntegerValue(ref sizeKey, out size) >= 0 && size > 0 && size <= Int64.MaxValue ? (long?)size : null,
                                Created = ReadDate(values, Created),
                                Modified = ReadDate(values, Modified),
                                Authored = ReadDate(values, Authored)
                            });
                            if (files.Count == 1 || files.Count % 64 == 0) progress(files.Count);
                        }
                        finally { Release(values); }
                    }
                    if (ids.Count < batchSize) break;
                }
            }
            finally { Release(objects); }
        }

        public void Read(MediaFile file, Action<byte[], int> write, CancellationToken cancel)
        {
            ReadResource(file, DefaultResource, write, cancel);
        }

        public byte[] ReadThumbnail(MediaFile file, CancellationToken cancel)
        {
            return ReadPreview(file, Thumbnail, 2 * 1024 * 1024, cancel);
        }

        public byte[] ReadPhotoPreview(MediaFile file, CancellationToken cancel)
        {
            return !file.IsVideo && file.Size > 0 && file.Size <= 12 * 1024 * 1024 ?
                ReadPreview(file, DefaultResource, 12 * 1024 * 1024, cancel) : null;
        }

        private byte[] ReadPreview(MediaFile file, PropertyKey key, int maxBytes, CancellationToken cancel)
        {
            try
            {
                using (var output = new MemoryStream())
                {
                    ReadResource(file, key, (buffer, length) =>
                    {
                        if (output.Length + length > maxBytes) throw new IOException();
                        output.Write(buffer, 0, length);
                    }, cancel);
                    return output.Length == 0 ? null : output.ToArray();
                }
            }
            catch (COMException) { return null; }
            catch (IOException) { return null; }
        }

        private void ReadResource(MediaFile file, PropertyKey key, Action<byte[], int> write, CancellationToken cancel)
        {
            uint optimal = 0;
            IStream stream;
            resources.GetStream(file.Id, ref key, 0, ref optimal, out stream);
            IntPtr count = IntPtr.Zero;
            try
            {
                count = Marshal.AllocCoTaskMem(sizeof(int));
                var buffer = new byte[optimal == 0 ? 256 * 1024 : Math.Min(optimal, 4 * 1024 * 1024)];
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    Marshal.WriteInt32(count, 0);
                    stream.Read(buffer, buffer.Length, count);
                    int length = Marshal.ReadInt32(count);
                    if (length < 0 || length > buffer.Length) throw new IOException("아이폰에서 잘못된 전송 길이를 받았습니다.");
                    if (length == 0) return;
                    write(buffer, length);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(count);
                Release(stream);
            }
        }

        private delegate int GetIds(IntPtr buffer, ref uint count);
        private delegate int GetDeviceLabel(string id, StringBuilder label, ref uint length);

        private static List<string> ReadIds(uint capacity, GetIds read)
        {
            var ids = new List<string>();
            if (capacity == 0) return ids;
            int bytes = checked((int)capacity * IntPtr.Size);
            IntPtr buffer = Marshal.AllocCoTaskMem(bytes);
            try
            {
                Marshal.Copy(new byte[bytes], 0, buffer, bytes);
                uint count = capacity;
                Marshal.ThrowExceptionForHR(read(buffer, ref count));
                for (int i = 0; i < Math.Min(count, capacity); i++)
                {
                    string id = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                    if (String.IsNullOrEmpty(id)) throw new IOException("Windows에서 기기 항목 이름을 읽지 못했습니다.");
                    ids.Add(id);
                }
                return ids;
            }
            finally
            {
                for (int offset = 0; offset < bytes; offset += IntPtr.Size)
                    Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(buffer, offset));
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        private static string ReadDeviceLabel(string id, GetDeviceLabel read)
        {
            uint length = 0;
            read(id, null, ref length);
            if (length == 0 || length > 4096) return "";
            var label = new StringBuilder((int)length);
            return read(id, label, ref length) >= 0 ? label.ToString() : "";
        }

        private static string ReadString(IPortableDeviceValues values, PropertyKey key)
        {
            IntPtr pointer;
            if (values.GetStringValue(ref key, out pointer) < 0 || pointer == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(pointer); }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }

        private static Guid ReadGuid(IPortableDeviceValues values, PropertyKey key)
        {
            Guid result;
            return values.GetGuidValue(ref key, out result) >= 0 ? result : Guid.Empty;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        private static DateTime? ReadDate(IPortableDeviceValues values, PropertyKey key)
        {
            var value = new PropVariant();
            try
            {
                if (values.GetValue(ref key, ref value) < 0 || value.Type != 7 || value.Date <= 0) return null;
                return DateTime.FromOADate(value.Date);
            }
            catch (ArgumentException) { return null; }
            finally { PropVariantClear(ref value); }
        }

        private static T Create<T>(string clsid) where T : class
        {
            return (T)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), true));
        }

        private static void Release(object instance)
        {
            if (instance != null) Marshal.ReleaseComObject(instance);
        }

        public void Dispose()
        {
            Release(objectKeys);
            Release(resources);
            Release(properties);
            Release(content);
            try
            {
                if (opened) device.Close();
            }
            catch (COMException) { }
            finally { Release(device); }
        }
    }
}
