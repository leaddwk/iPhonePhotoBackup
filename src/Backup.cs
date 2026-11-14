using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace iPhonePhotoBackup
{
    internal static class BackupPaths
    {
        private static readonly Regex ReservedName = new Regex(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase);

        public static string SafeName(string name, string fallback)
        {
            var clean = new StringBuilder(name ?? "");
            foreach (char invalid in Path.GetInvalidFileNameChars()) clean.Replace(invalid, '_');
            string result = clean.ToString().TrimStart().TrimEnd(' ', '.');
            if (String.IsNullOrEmpty(result)) return fallback;
            if (ReservedName.IsMatch(result.Split('.')[0].TrimEnd())) result = "_" + result;
            return result;
        }

        public static string HashText(string text)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        public static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        public static string Under(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("백업 폴더 밖에 파일을 저장할 수 없습니다.");
            return full;
        }

        public static void CheckLink(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("바로 가기 또는 연결 폴더에는 백업할 수 없습니다. " + path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        public static void MakeDirectory(string path)
        {
            if (String.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)) return;
            MakeDirectory(Path.GetDirectoryName(path));
            CheckLink(path);
            Directory.CreateDirectory(path);
        }
    }

    internal sealed class BackupResult
    {
        public string Folder;
        public string Report;
        public int Total;
        public int Copied;
        public int Existing;
        public int Failed;
        public int Checked;
        public bool Checking;
        public string Phase;
        public bool Cancelled;
        public string Stopped;

        public int Remaining { get { return Total - Copied - Existing; } }
    }

    internal delegate void ReadMedia(MediaFile file, Action<byte[], int> write, CancellationToken cancel);

    internal static class Backup
    {
        private const long Fat32Max = UInt32.MaxValue;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private sealed class SavedFile
        {
            public string RelativePath;
            public long Length;
            public string Hash;
        }

        public static BackupResult Copy(string destination, string deviceIdentity, List<MediaFile> files, ReadMedia read,
            CancellationToken cancel, Action<BackupResult, MediaFile, long> progress, bool checkFirst = false)
        {
            var result = new BackupResult { Total = files.Count };
            result.Folder = Path.Combine(destination, "iPhone 사진 백업", "iPhone - " + BackupPaths.HashText(deviceIdentity).Substring(0, 12));
            string mediaFolder = Path.Combine(result.Folder, "사진 및 동영상");
            string workFolder = Path.Combine(result.Folder, "백업 기록");
            BackupPaths.MakeDirectory(mediaFolder);
            BackupPaths.MakeDirectory(workFolder);
            var drive = new DriveInfo(Path.GetPathRoot(result.Folder));
            bool fat32 = String.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase);

            string lockPath = Path.Combine(workFolder, "backup.lock");
            string indexPath = Path.Combine(workFolder, "index.tsv");
            string tempPath = Path.Combine(workFolder, "copy.part");
            BackupPaths.CheckLink(lockPath);
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                BackupPaths.CheckLink(indexPath);
                BackupPaths.CheckLink(tempPath);
                File.Delete(tempPath);
                var saved = LoadIndex(indexPath);
                var previousPaths = saved.Values.ToLookup(entry => Path.GetFileName(entry.RelativePath), StringComparer.OrdinalIgnoreCase);
                result.Report = Path.Combine(workFolder, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");

                using (var indexFile = new FileStream(indexPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var index = new StreamWriter(indexFile, Utf8) { AutoFlush = true })
                using (var report = new StreamWriter(new FileStream(result.Report, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Utf8) { AutoFlush = true })
                {
                    report.WriteLine("백업 시작 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    report.WriteLine("아이폰이 USB로 공개한 백업 대상 파일 " + files.Count + "개");
                    report.WriteLine("확인 범위: 현재 USB 목록. 아이폰 사진 앱의 전체 보관함과 별도로 비교해야 합니다.");
                    var pending = files;
                    if (checkFirst)
                    {
                        pending = new List<MediaFile>();
                        result.Checking = true;
                        foreach (var file in files)
                        {
                            try
                            {
                                cancel.ThrowIfCancellationRequested();
                                progress(result, file, 0);
                                cancel.ThrowIfCancellationRequested();
                                if (IsSaved(mediaFolder, saved, previousPaths, file, SourceKey(file), cancel, verifyContents: false)) result.Existing++;
                                else pending.Add(file);
                                result.Checked++;
                            }
                            catch (OperationCanceledException) { break; }
                        }
                        result.Cancelled = cancel.IsCancellationRequested;
                        result.Checking = false;
                        report.WriteLine("사전 확인 " + result.Checked + "개, 완료 기록과 파일 정보 일치 " + result.Existing + "개");
                    }

                    foreach (var file in pending)
                    {
                        if (result.Cancelled) break;
                        try
                        {
                            cancel.ThrowIfCancellationRequested();
                            result.Phase = "파일 준비 중";
                            progress(result, file, 0);
                            string original = BackupPaths.Under(mediaFolder, file.RelativePath);
                            BackupPaths.MakeDirectory(Path.GetDirectoryName(original));

                            string key = SourceKey(file);
                            if (!checkFirst && IsSaved(mediaFolder, saved, previousPaths, file, key, cancel))
                            {
                                result.Existing++;
                                continue;
                            }
                            if (fat32 && file.Size > Fat32Max) throw Fat32Error();

                            report.WriteLine("복사 시작\t" + file.RelativePath + "\t" + (file.Size.HasValue ? file.Size.Value.ToString(CultureInfo.InvariantCulture) : "크기 미확인"));
                            bool existing;
                            SavedFile verified = CopyWithRetry(mediaFolder, original, tempPath, file, read, fat32, cancel,
                                (phase, bytes) =>
                                {
                                    result.Phase = phase;
                                    progress(result, file, bytes);
                                }, out existing);
                            if (key != null)
                            {
                                // Start on a fresh line even when the previous run left a partially written record.
                                index.WriteLine("\n" + key + "\t" + Convert.ToBase64String(Utf8.GetBytes(verified.RelativePath)) + "\t" +
                                    verified.Length.ToString(CultureInfo.InvariantCulture) + "\t" + verified.Hash);
                                saved[key] = verified;
                            }
                            if (existing) result.Existing++;
                            else result.Copied++;
                        }
                        catch (OperationCanceledException)
                        {
                            result.Cancelled = true;
                            report.WriteLine("복사 중지\t" + file.RelativePath);
                            break;
                        }
                        catch (Exception error)
                        {
                            report.WriteLine("실패\t" + file.RelativePath + "\t" + error.Message + " (0x" + error.HResult.ToString("X8") + ")");
                            result.Failed++;
                            if (IsConnectionOrDiskError(error))
                            {
                                result.Stopped = error.Message + " (0x" + error.HResult.ToString("X8") + ")";
                                break;
                            }
                        }
                        finally { progress(result, file, 0); }
                    }
                    report.WriteLine("새로 복사 " + result.Copied + "개, 기존 확인 " + result.Existing + "개, 남은 파일 " + result.Remaining +
                        "개(실패 " + result.Failed + "개, 미시도 또는 미확인 " + (result.Remaining - result.Failed) + "개)");
                    if (result.Cancelled) report.WriteLine("사용자가 중지했습니다.");
                    if (result.Stopped != null) report.WriteLine("중단 사유 " + result.Stopped);
                }
            }
            return result;
        }

        private static string SourceKey(MediaFile file, string originalPath = null)
        {
            if (!file.Size.HasValue || String.IsNullOrEmpty(file.PersistentId))
                return null;
            return BackupPaths.HashText((originalPath ?? file.RelativePath) + "\n" + file.PersistentId + "\n" + file.Size + "\n" +
                file.Created.GetValueOrDefault().Ticks + "\n" + file.Modified.GetValueOrDefault().Ticks);
        }

        private static bool IsSaved(string root, Dictionary<string, SavedFile> saved, ILookup<string, SavedFile> previousPaths,
            MediaFile file, string key, CancellationToken cancel, bool verifyContents = true)
        {
            if (key == null) return false;
            SavedFile previous;
            if (saved.TryGetValue(key, out previous) && VerifySaved(root, previous, file, cancel, verifyContents)) return true;

            // The device can change the parent folders it publishes over USB. Match the previous
            // path only if it reproduces the recorded key for this source and its metadata.
            foreach (var candidate in previousPaths[Path.GetFileName(file.RelativePath)])
            {
                cancel.ThrowIfCancellationRequested();
                if (candidate.Length != file.Size) continue;
                string oldKey = SourceKey(file, candidate.RelativePath);
                if (oldKey != key && saved.TryGetValue(oldKey, out previous) && VerifySaved(root, previous, file, cancel, verifyContents)) return true;
            }
            return false;
        }

        private static Dictionary<string, SavedFile> LoadIndex(string path)
        {
            var entries = new Dictionary<string, SavedFile>(StringComparer.Ordinal);
            if (!File.Exists(path)) return entries;
            foreach (string line in File.ReadLines(path, Utf8))
            {
                string[] columns = line.Split('\t');
                long length;
                if (columns.Length != 4 || columns[0].Length != 64 || columns[3].Length != 64 ||
                    !Int64.TryParse(columns[2], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length <= 0) continue;
                try
                {
                    string relativePath = Utf8.GetString(Convert.FromBase64String(columns[1]));
                    if (String.IsNullOrWhiteSpace(relativePath) || relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                    entries[columns[0]] = new SavedFile
                    {
                        RelativePath = relativePath, Length = length, Hash = columns[3]
                    };
                }
                catch (FormatException) { }
            }
            return entries;
        }

        private static bool VerifySaved(string root, SavedFile saved, MediaFile source, CancellationToken cancel, bool verifyContents)
        {
            try
            {
                string path = BackupPaths.Under(root, saved.RelativePath);
                for (string parent = path; !String.Equals(parent, root, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                    BackupPaths.CheckLink(parent);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != saved.Length || saved.Length != source.Size) return false;
                if (verifyContents) return ReadHash(path, cancel) == saved.Hash;

                // Copies have already passed a checksum check. FAT timestamps have two-second precision.
                return !source.Modified.HasValue ||
                    Math.Abs((info.LastWriteTime - source.Modified.Value).TotalSeconds) <= 2;
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool Matches(string path, long length, string hash, CancellationToken cancel)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == length && ReadHash(path, cancel) == hash;
        }

        private static string ReadHash(string path, CancellationToken cancel, Action<long> progress = null)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan))
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[256 * 1024];
                long read = 0;
                int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                    read += count;
                    if (progress != null) progress(read);
                }
                hash.TransformFinalBlock(buffer, 0, 0);
                return BackupPaths.Hex(hash.Hash);
            }
        }

        private static SavedFile CopyWithRetry(string root, string original, string temp, MediaFile file, ReadMedia read,
            bool fat32, CancellationToken cancel, Action<string, long> progress, out bool existing)
        {
            try { return CopyOne(root, original, temp, file, read, fat32, cancel, progress, out existing); }
            catch (Exception error)
            {
                uint code = unchecked((uint)error.HResult);
                if (!(error is TransferSizeException) && code != 0x80042007 && code != 0x8007001E && code != 0x8007001F)
                    throw;
                progress("파일 전송 재시도 중", 0);
                cancel.WaitHandle.WaitOne(300);
                cancel.ThrowIfCancellationRequested();
                return CopyOne(root, original, temp, file, read, fat32, cancel, progress, out existing);
            }
        }

        private static SavedFile CopyOne(string root, string original, string temp, MediaFile file, ReadMedia read,
            bool fat32, CancellationToken cancel, Action<string, long> progress, out bool existing)
        {
            long length = 0;
            string digest;
            bool created = false;
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
                using (var hash = SHA256.Create())
                {
                    created = true;
                    progress("아이폰 전송 중", 0);
                    read(file, (buffer, count) =>
                    {
                        length += count;
                        if (fat32 && length > Fat32Max) throw Fat32Error();
                        if (file.Size.HasValue && length > file.Size.Value) throw new TransferSizeException(file.Size, length);
                        output.Write(buffer, 0, count);
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                        progress("아이폰 전송 중", length);
                    }, cancel);
                    cancel.ThrowIfCancellationRequested();
                    if (length == 0 || (file.Size.HasValue && length != file.Size.Value)) throw new TransferSizeException(file.Size, length);
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    digest = BackupPaths.Hex(hash.Hash);
                    progress("외장하드에 저장 중", length);
                    output.Flush(true);
                }
                progress("새 파일 검증 중", 0);
                if (ReadHash(temp, cancel, bytes => progress("새 파일 검증 중", bytes)) != digest)
                    throw new IOException("외장하드에 저장한 파일의 SHA-256 검증에 실패했습니다.");
                if (file.Modified.HasValue && (!fat32 || (file.Modified.Value.Year >= 1980 && file.Modified.Value.Year <= 2107)))
                    File.SetLastWriteTime(temp, file.Modified.Value);

                string directory = Path.GetDirectoryName(original);
                string stem = Path.GetFileNameWithoutExtension(original);
                string extension = Path.GetExtension(original);
                for (int suffix = 1; ; suffix++)
                {
                    cancel.ThrowIfCancellationRequested();
                    string candidate = suffix == 1 ? original : Path.Combine(directory, stem + " (" + suffix + ")" + extension);
                    BackupPaths.CheckLink(candidate);
                    existing = Matches(candidate, length, digest, cancel);
                    if (!existing)
                    {
                        if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                        File.Move(temp, candidate);
                    }
                    return new SavedFile { RelativePath = candidate.Substring(root.Length + 1), Length = length, Hash = digest };
                }
            }
            finally
            {
                if (created)
                {
                    try { File.Delete(temp); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static IOException Fat32Error()
        {
            return new IOException("FAT32에는 4GB 이상의 파일을 저장할 수 없습니다. exFAT 또는 NTFS 드라이브를 선택하세요.");
        }

        private sealed class TransferSizeException : IOException
        {
            public TransferSizeException(long? expected, long received)
                : base("아이폰 전송 크기 불일치 (예상 " + (expected.HasValue ? expected.Value.ToString("N0") + "바이트" : "미제공") +
                    ", 수신 " + received.ToString("N0") + "바이트). 사진 설정의 ‘원본 유지’와 USB 연결을 확인하세요.") { }
        }

        private static bool IsConnectionOrDiskError(Exception error)
        {
            switch (unchecked((uint)error.HResult))
            {
                case 0x80070015: // ERROR_NOT_READY
                case 0x80070027: // ERROR_HANDLE_DISK_FULL
                case 0x80070070: // ERROR_DISK_FULL
                case 0x8007048f: // ERROR_DEVICE_NOT_CONNECTED
                case 0x800706ba: // RPC_S_SERVER_UNAVAILABLE
                case 0x80010108: // RPC_E_DISCONNECTED
                case 0x802A0002: // E_WPD_DEVICE_NOT_OPEN
                case 0x802A0006: // E_WPD_DEVICE_IS_HUNG
                    return true;
                default: return false;
            }
        }
    }
}
