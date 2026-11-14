using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace iPhonePhotoBackup
{
    internal sealed class PhotoMetadata
    {
        public string Directory;
        public string FileName;
        public long? Size;
        public DateTime? Captured;
        public bool? Favorite;
        public bool Live;
        public string Source;
    }

    internal sealed class GalleryItem
    {
        public readonly MediaFile File;
        public readonly List<MediaFile> Related = new List<MediaFile>();
        public DateTime? Date;
        public bool? Favorite;
        public string Source;

        public GalleryItem(MediaFile file, PhotoMetadata metadata)
        {
            File = file;
            Date = (metadata == null ? null : metadata.Captured) ?? file.Authored ?? file.Created;
            Favorite = metadata == null ? null : metadata.Favorite;
            Source = metadata == null ? null : metadata.Source;
        }

        public string Kind
        {
            get
            {
                if (File.IsVideo) return "동영상";
                if (String.Equals(Path.GetExtension(File.RelativePath), ".aae", StringComparison.OrdinalIgnoreCase)) return "편집 정보";
                return Related.Any(file => file.IsVideo) ? "Live Photo" : "사진";
            }
        }

        public IEnumerable<MediaFile> Files()
        {
            yield return File;
            foreach (MediaFile file in Related) yield return file;
        }
    }

    internal static class PhotoCatalog
    {
        private static readonly Regex AppleName = new Regex(@"^IMG_(E)?([0-9]+)$", RegexOptions.IgnoreCase);
        private static readonly Regex MonthFolder = new Regex(@"^([12][0-9]{3})(0[1-9]|1[0-2])[_0-9]*$");
        private static readonly Regex LegacyFolder = new Regex(@"^[0-9]{3}(APPLE|CLOUD)$", RegexOptions.IgnoreCase);
        private static readonly Dictionary<string, string> AppNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "com.atebits.Tweetie2", "X (Twitter)" }, { "com.atebits.Tweetie2.ShareExtension", "X (Twitter)" },
            { "com.burbn.instagram", "Instagram" }, { "com.apple.mobilesafari", "Safari" },
            { "com.iwilab.KakaoTalk", "카카오톡" }, { "com.apple.MobileSMS", "메시지" }, { "com.apple.camera", "카메라" },
            { "com.hammerandchisel.discord", "Discord" }, { "com.google.chrome.ios", "Chrome" }
        };

        public static List<PhotoMetadata> ReadDevice(Phone phone, string identity, CancellationToken cancel)
        {
            string temp = Path.Combine(Path.GetTempPath(), "iPhonePhotoCatalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string db = Path.Combine(temp, "Photos.sqlite");
                using (var connection = new ApplePhotoMetadata(phone, identity))
                    connection.CopyDatabase(db, cancel);
                return ReadDatabase(db, cancel);
            }
            finally
            {
                try { Directory.Delete(temp, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static List<PhotoMetadata> ReadDatabase(string path, CancellationToken cancel)
        {
            using (var db = new SqliteReader(path))
            {
                var assets = db.Columns("ZASSET");
                string table = "ZASSET";
                if (!assets.Contains("ZFILENAME"))
                {
                    table = "ZGENERICASSET";
                    assets = db.Columns(table);
                }
                if (!assets.Contains("ZFILENAME")) throw new IOException("현재 iOS의 사진 보관함 형식을 읽을 수 없습니다.");

                var extra = db.Columns("ZADDITIONALASSETATTRIBUTES");
                string join = "";
                if (assets.Contains("ZADDITIONALATTRIBUTES") && extra.Contains("Z_PK"))
                    join = " LEFT JOIN ZADDITIONALASSETATTRIBUTES e ON e.Z_PK = a.ZADDITIONALATTRIBUTES";
                else if (assets.Contains("Z_PK") && extra.Contains("ZASSET"))
                    join = " LEFT JOIN ZADDITIONALASSETATTRIBUTES e ON e.ZASSET = a.Z_PK";
                if (join.Length == 0) extra.Clear();

                string query = "SELECT a.ZFILENAME," + Column(assets, "a", "ZDIRECTORY") + "," +
                    Column(assets, "a", "ZDATECREATED") + "," + Column(assets, "a", "ZFAVORITE") + "," +
                    Column(extra, "e", "ZORIGINALFILESIZE") + "," + Column(assets, "a", "ZKINDSUBTYPE") +
                    ",COALESCE(NULLIF(" + Column(extra, "e", "ZIMPORTEDBYBUNDLEIDENTIFIER") + ",''),NULLIF(" +
                    Column(extra, "e", "ZCREATORBUNDLEID") + ",''),NULLIF(" +
                    Column(extra, "e", "ZIMPORTEDBYDISPLAYNAME") + ",'')) FROM " + table + " a" + join;
                if (assets.Contains("ZTRASHEDSTATE")) query += " WHERE COALESCE(a.ZTRASHEDSTATE,0)=0";

                var metadata = new List<PhotoMetadata>();
                db.Query(query, row =>
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = row.Text(0);
                    if (String.IsNullOrWhiteSpace(name)) return;
                    string directory = row.Text(1);
                    if (!String.IsNullOrEmpty(directory) && !directory.Replace('\\', '/').TrimStart('/').StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase))
                        return;
                    string source = row.Text(6);
                    string display;
                    if (!String.IsNullOrEmpty(source) && AppNames.TryGetValue(source, out display)) source = display;
                    long? favorite = row.Integer(3);
                    metadata.Add(new PhotoMetadata
                    {
                        FileName = name, Directory = directory, Captured = AppleDate(row.Text(2)),
                        Favorite = favorite.HasValue ? (bool?)(favorite.Value == 1) : null,
                        Size = row.Integer(4), Live = row.Integer(5) == 2, Source = source
                    });
                });
                return metadata;
            }
        }

        private static string Column(HashSet<string> columns, string alias, string name)
        {
            return columns.Contains(name) ? alias + "." + name : "NULL";
        }

        private static DateTime? AppleDate(string text)
        {
            double seconds;
            if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                Double.IsNaN(seconds) || Double.IsInfinity(seconds)) return null;
            try { return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        public static List<GalleryItem> Build(List<MediaFile> files, List<PhotoMetadata> metadata)
        {
            var byName = (metadata ?? new List<PhotoMetadata>())
                .ToLookup(record => record.FileName, StringComparer.OrdinalIgnoreCase);
            var names = files.SelectMany(file => Names(file)).ToLookup(name => name, StringComparer.OrdinalIgnoreCase);
            var matches = new Dictionary<MediaFile, PhotoMetadata>();
            foreach (MediaFile file in files)
            {
                var candidates = Names(file).SelectMany(name => byName[name]).Distinct().ToList();
                if (candidates.Count == 0) continue;
                string folder = LastDirectory(file.RelativePath);
                var direct = candidates.Where(record => String.Equals(folder, LastDirectory(record.Directory, true), StringComparison.OrdinalIgnoreCase)).ToList();
                if (direct.Count > 0) candidates = direct;
                else if (LegacyFolder.IsMatch(folder)) continue;

                if (candidates.Count > 1 && file.Size.HasValue)
                {
                    var sameSize = candidates.Where(record => record.Size == file.Size).ToList();
                    if (sameSize.Count > 0) candidates = sameSize;
                }
                if (candidates.Count != 1) continue;
                var candidate = candidates[0];
                if (direct.Count == 0)
                {
                    bool uniqueAndSized = names[candidate.FileName].Count() == 1 && file.Size.HasValue && candidate.Size == file.Size;
                    var month = MonthFolder.Match(folder);
                    var deviceDate = file.Authored ?? file.Created;
                    bool confirmedDate = candidate.Captured.HasValue && deviceDate.HasValue &&
                        Math.Abs((candidate.Captured.Value - deviceDate.Value).TotalSeconds) <= 5 &&
                        (!month.Success || candidate.Captured.Value.ToString("yyyyMM", CultureInfo.InvariantCulture) == month.Value.Substring(0, 6));
                    if (!uniqueAndSized && !confirmedDate) continue;
                }
                matches[file] = candidate;
            }

            var items = new List<GalleryItem>();
            foreach (var group in files.GroupBy(SiblingKey, StringComparer.OrdinalIgnoreCase))
            {
                var photos = group.Where(file => !file.IsVideo && !IsEditInfo(file) &&
                    !AppleName.Match(Path.GetFileNameWithoutExtension(file.RelativePath)).Groups[1].Success).ToList();
                var movies = group.Where(file => file.IsVideo && String.Equals(Path.GetExtension(file.RelativePath), ".mov", StringComparison.OrdinalIgnoreCase)).ToList();
                MediaFile primary = photos.Count == 1 ? photos[0] : null;
                PhotoMetadata primaryMetadata = primary == null ? null : Lookup(matches, primary);
                var related = new HashSet<MediaFile>();
                if (primary != null && AppleName.IsMatch(Path.GetFileNameWithoutExtension(primary.RelativePath)))
                {
                    foreach (MediaFile edit in group.Where(IsEditInfo)) related.Add(edit);
                    if (movies.Count == 1 && Lookup(matches, movies[0]) == null &&
                        (primaryMetadata != null ? primaryMetadata.Live : CloseInTime(primary, movies[0])))
                        related.Add(movies[0]);
                }
                foreach (MediaFile file in group)
                {
                    if (related.Contains(file)) continue;
                    var item = new GalleryItem(file, Lookup(matches, file) ?? (IsEditInfo(file) ? primaryMetadata : null));
                    if (file == primary) item.Related.AddRange(related);
                    items.Add(item);
                }
            }
            return items;
        }

        private static bool CloseInTime(MediaFile first, MediaFile second)
        {
            DateTime? a = first.Authored ?? first.Created;
            DateTime? b = second.Authored ?? second.Created;
            return a.HasValue && b.HasValue && Math.Abs((a.Value - b.Value).TotalSeconds) <= 3;
        }

        private static PhotoMetadata Lookup(Dictionary<MediaFile, PhotoMetadata> matches, MediaFile file)
        {
            PhotoMetadata value;
            return matches.TryGetValue(file, out value) ? value : null;
        }

        private static IEnumerable<string> Names(MediaFile file)
        {
            string name = Path.GetFileName(file.RelativePath);
            yield return name;
            if (!String.IsNullOrWhiteSpace(file.DeviceName) && Path.HasExtension(file.DeviceName) &&
                !String.Equals(name, file.DeviceName, StringComparison.OrdinalIgnoreCase))
                yield return file.DeviceName;
        }

        private static bool IsEditInfo(MediaFile file)
        {
            return String.Equals(Path.GetExtension(file.RelativePath), ".aae", StringComparison.OrdinalIgnoreCase);
        }

        private static string SiblingKey(MediaFile file)
        {
            string stem = Path.GetFileNameWithoutExtension(file.RelativePath);
            var match = AppleName.Match(stem);
            if (match.Success) stem = "IMG_" + match.Groups[2].Value;
            return Path.Combine(Path.GetDirectoryName(file.RelativePath), stem);
        }

        private static string LastDirectory(string path, bool alreadyDirectory = false)
        {
            if (String.IsNullOrEmpty(path)) return "";
            string windowsPath = path.Replace('/', '\\').TrimEnd('\\');
            return Path.GetFileName(alreadyDirectory ? windowsPath : Path.GetDirectoryName(windowsPath)) ?? "";
        }
    }

    internal sealed class SqliteReader : IDisposable
    {
        private const string Library = "winsqlite3.dll";
        private readonly IntPtr db;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_open_v2(byte[] name, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_close(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr sqlite3_column_text16(IntPtr statement, int index);

        public SqliteReader(string path)
        {
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, 1, IntPtr.Zero) != 0)
            {
                var error = Error();
                Dispose();
                throw error;
            }
        }

        public HashSet<string> Columns(string table)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Query("PRAGMA table_info(" + table + ")", row => columns.Add(row.Text(1)));
            return columns;
        }

        public void Query(string sql, Action<Row> read)
        {
            IntPtr statement;
            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out statement, IntPtr.Zero) != 0) throw Error();
            try
            {
                int status;
                var row = new Row(statement);
                while ((status = sqlite3_step(statement)) == 100) read(row);
                if (status != 101) throw Error();
            }
            finally { sqlite3_finalize(statement); }
        }

        private IOException Error()
        {
            return new IOException("사진 보관함 정보를 열 수 없습니다. " +
                (db == IntPtr.Zero ? "" : Marshal.PtrToStringUni(sqlite3_errmsg16(db))));
        }

        public void Dispose()
        {
            if (db != IntPtr.Zero) sqlite3_close(db);
        }

        internal sealed class Row
        {
            private readonly IntPtr statement;
            public Row(IntPtr statement) { this.statement = statement; }
            public string Text(int column) { return Marshal.PtrToStringUni(sqlite3_column_text16(statement, column)); }
            public long? Integer(int column)
            {
                long value;
                return Int64.TryParse(Text(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? (long?)value : null;
            }
        }
    }
}
