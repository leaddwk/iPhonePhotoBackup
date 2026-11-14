using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace iPhonePhotoBackup
{
    internal sealed class GalleryForm : Form
    {
        private readonly List<GalleryItem> items;
        private readonly PhoneLibrary library;
        private readonly HashSet<GalleryItem> selected = new HashSet<GalleryItem>();
        private List<GalleryItem> visible = new List<GalleryItem>();
        private readonly DataGridView grid = new DataGridView();
        private readonly TextBox search = new TextBox();
        private readonly ComboBox month = SelectBox();
        private readonly ComboBox kind = SelectBox();
        private readonly ComboBox heart = SelectBox();
        private readonly ComboBox source = SelectBox();
        private readonly ComboBox order = SelectBox();
        private readonly DateTimePicker fromDate = DatePicker();
        private readonly DateTimePicker toDate = DatePicker();
        private readonly Label counts = new Label();
        private readonly Label previewStatus = new Label();
        private readonly PictureBox preview = new PictureBox();
        private readonly Button confirm = new Button();
        private readonly CheckBox selectedOnly = new CheckBox { Text = "선택한 항목만", AutoSize = true };
        private readonly System.Windows.Forms.Timer previewDelay = new System.Windows.Forms.Timer { Interval = 220 };
        private readonly CancellationTokenSource previewCancel = new CancellationTokenSource();
        private GalleryItem requestedPreview;
        private bool previewPending;
        private bool readingPreview;
        private bool closeRequested;
        private DialogResult closeResult;
        private bool updating;
        private bool originalPreview;
        private long selectedBytes;
        private int unknownSizes;
        private int selectedFiles;

        public List<MediaFile> Selection { get; private set; }

        public GalleryForm(List<GalleryItem> items, PhoneLibrary library, string metadataStatus, List<MediaFile> previousSelection)
        {
            this.items = items;
            this.library = library;
            Text = "iPhone 갤러리";
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Malgun Gothic", 9.5f);
            BackColor = Color.FromArgb(246, 248, 252);
            ForeColor = Color.FromArgb(23, 39, 58);
            ClientSize = new Size(1140, 675);
            MinimumSize = new Size(850, 570);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);

            var info = new Label { Text = metadataStatus, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            var tips = new ToolTip();
            tips.SetToolTip(info, metadataStatus);
            Disposed += delegate { tips.Dispose(); };
            layout.Controls.Add(info, 0, 0);

            month.Items.Add("모든 날짜");
            foreach (string value in items.Where(item => item.Date.HasValue).Select(Month).Distinct().OrderByDescending(value => value))
                month.Items.Add(value);
            if (items.Any(item => !item.Date.HasValue)) month.Items.Add("날짜 미확인");
            kind.Items.AddRange(new object[] { "모든 종류", "사진", "Live Photo", "동영상", "편집 정보" });
            heart.Items.AddRange(new object[] { "하트 전체", "♥ 즐겨찾기", "하트 없음", "하트 미확인" });
            source.Items.AddRange(new object[] { "모든 저장 앱", "X (Twitter)", "출처 미확인" });
            foreach (string value in items.Select(item => item.Source).Where(value => !String.IsNullOrEmpty(value) && value != "X (Twitter)")
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value))
                source.Items.Add(value);
            order.Items.AddRange(new object[] { "최신순", "오래된 순", "용량 큰 순", "파일 이름순" });
            var filters = Row();
            AddFilter(filters, "날짜", month, 105);
            AddFilter(filters, "종류", kind, 110);
            AddFilter(filters, "하트", heart, 113);
            AddFilter(filters, "저장 앱", source, 150);
            AddFilter(filters, "파일 이름", search, 160);
            layout.Controls.Add(filters, 0, 1);

            var dates = Row();
            AddFilter(dates, "시작일", fromDate, 135);
            AddFilter(dates, "종료일", toDate, 135);
            dates.Controls.Add(new Label { Text = "날짜에 체크하면 기간을 적용합니다.", AutoSize = true, Margin = new Padding(7, 7, 0, 0) });
            layout.Controls.Add(dates, 0, 2);

            var selection = Row();
            AddFilter(selection, "정렬", order, 108);
            selection.Controls.Add(MakeButton("검색 결과 모두 선택", () => SelectVisible(true), 146));
            selection.Controls.Add(MakeButton("검색 결과 선택 해제", () => SelectVisible(false), 146));
            selectedOnly.Margin = new Padding(9, 6, 0, 0);
            selection.Controls.Add(selectedOnly);
            layout.Controls.Add(selection, 0, 3);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            layout.Controls.Add(body, 0, 4);
            SetupGrid();
            body.Controls.Add(grid, 0, 0);

            var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(9, 0, 0, 0) };
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 57));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            preview.BackColor = Color.FromArgb(232, 237, 245);
            preview.Dock = DockStyle.Fill;
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            previewStatus.Dock = DockStyle.Fill;
            previewStatus.AutoEllipsis = true;
            previewStatus.TextAlign = ContentAlignment.MiddleLeft;
            var loadOriginal = MakeButton("사진 미리보기 열기", () => QueuePreview(true), 160);
            previewPanel.Controls.Add(preview, 0, 0);
            previewPanel.Controls.Add(previewStatus, 0, 1);
            previewPanel.Controls.Add(loadOriginal, 0, 2);
            body.Controls.Add(previewPanel, 1, 0);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            counts.Dock = DockStyle.Fill;
            counts.TextAlign = ContentAlignment.MiddleLeft;
            counts.AutoEllipsis = true;
            confirm.Text = "선택 항목 확인";
            confirm.Size = new Size(148, 34);
            confirm.Click += delegate
            {
                Selection = items.Where(selected.Contains).SelectMany(item => item.Files()).ToList();
                if (Selection.Count > 0) { DialogResult = DialogResult.OK; Close(); }
            };
            footer.Controls.Add(counts, 0, 0);
            footer.Controls.Add(confirm, 1, 0);
            footer.Controls.Add(MakeButton("닫기", () => { DialogResult = DialogResult.Cancel; Close(); }, 72), 2, 0);
            layout.Controls.Add(footer, 0, 5);

            foreach (var box in new[] { month, kind, heart, source, order })
            {
                box.SelectedIndex = 0;
                box.SelectedIndexChanged += delegate { Filter(); };
            }
            search.TextChanged += delegate { Filter(); };
            foreach (var date in new[] { fromDate, toDate })
            {
                date.ValueChanged += delegate { Filter(); };
                date.KeyUp += delegate { Filter(); };
                date.MouseUp += delegate { Filter(); };
            }
            selectedOnly.CheckedChanged += delegate { Filter(); };
            previewDelay.Tick += async delegate { previewDelay.Stop(); await ReadPreview(); };
            if (previousSelection != null)
            {
                var keys = new HashSet<string>(previousSelection.Select(SelectionKey), StringComparer.OrdinalIgnoreCase);
                foreach (GalleryItem item in items)
                    if (keys.Contains(SelectionKey(item.File))) SetSelection(item, true);
            }
            Shown += delegate { Filter(); };
            FormClosing += OnClosing;
            FormClosed += delegate
            {
                previewDelay.Dispose();
                previewCancel.Dispose();
                if (preview.Image != null) preview.Image.Dispose();
            };
        }

        private static string SelectionKey(MediaFile file)
        {
            return file.PersistentId + "\n" + file.RelativePath + "\n" + file.Size + "\n" + file.Created.GetValueOrDefault().Ticks;
        }

        private static ComboBox SelectBox()
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, DropDownHeight = 270 };
        }

        private static DateTimePicker DatePicker()
        {
            return new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", ShowCheckBox = true, Checked = false };
        }

        private static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
        }

        private static void AddFilter(FlowLayoutPanel panel, string name, Control control, int width)
        {
            panel.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(3, 6, 2, 0) });
            control.AccessibleName = name;
            control.Width = width;
            panel.Controls.Add(control);
        }

        private static Button MakeButton(string text, Action action, int width)
        {
            var button = new Button { Text = text, Size = new Size(width, 29) };
            button.Click += delegate { action(); };
            return button;
        }

        private void SetupGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.VirtualMode = true;
            grid.ReadOnly = true;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowTemplate.Height = 28;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 253);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "백업", Width = 53, SortMode = DataGridViewColumnSortMode.NotSortable });
            AddColumn("파일 이름", 150, true);
            AddColumn("날짜", 143);
            AddColumn("종류", 87);
            AddColumn("하트", 53);
            AddColumn("저장 앱", 113);
            AddColumn("용량", 83);
            grid.CellValueNeeded += ProvideCell;
            grid.CurrentCellChanged += delegate { if (!updating) QueuePreview(false); };
            grid.CellClick += delegate(object sender, DataGridViewCellEventArgs args)
            {
                if (args.RowIndex >= 0 && args.ColumnIndex == 0) Toggle(args.RowIndex);
            };
            grid.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode != Keys.Space || grid.CurrentCell == null) return;
                Toggle(grid.CurrentCell.RowIndex);
                args.Handled = true;
                args.SuppressKeyPress = true;
            };
        }

        private void AddColumn(string title, int width, bool fill = false)
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = title, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (fill) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns.Add(column);
        }

        private void ProvideCell(object sender, DataGridViewCellValueEventArgs args)
        {
            if (args.RowIndex < 0 || args.RowIndex >= visible.Count) return;
            GalleryItem item = visible[args.RowIndex];
            switch (args.ColumnIndex)
            {
                case 0: args.Value = selected.Contains(item); break;
                case 1: args.Value = Path.GetFileName(item.File.RelativePath); break;
                case 2: args.Value = item.Date.HasValue ? item.Date.Value.ToString("yyyy-MM-dd HH:mm") : "미확인"; break;
                case 3: args.Value = item.Kind; break;
                case 4: args.Value = item.Favorite == true ? "♥" : item.Favorite == false ? "" : "—"; break;
                case 5: args.Value = item.Source ?? "미확인"; break;
                case 6: args.Value = item.File.Size.HasValue ? FormatSize(item.Files().Sum(file => file.Size ?? 0)) : "미확인"; break;
            }
        }

        internal static string Month(GalleryItem item)
        {
            return item.Date.HasValue ? item.Date.Value.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) : "날짜 미확인";
        }

        internal static IEnumerable<GalleryItem> Find(IEnumerable<GalleryItem> items, int monthIndex, string month, int kindIndex, string kind,
            int heart, int sourceIndex, string source, string query, DateTime? fromDate = null, DateTime? toDate = null)
        {
            return items.Where(item =>
                (monthIndex == 0 || Month(item) == month) &&
                (!fromDate.HasValue || item.Date.HasValue && item.Date.Value.Date >= fromDate.Value.Date) &&
                (!toDate.HasValue || item.Date.HasValue && item.Date.Value.Date <= toDate.Value.Date) &&
                (kindIndex == 0 || item.Kind == kind || (kind == "사진" && item.Kind == "Live Photo")) &&
                (heart == 0 || (heart == 1 && item.Favorite == true) || (heart == 2 && item.Favorite == false) ||
                    (heart == 3 && !item.Favorite.HasValue)) &&
                (sourceIndex == 0 || (sourceIndex == 2 ? String.IsNullOrEmpty(item.Source) : String.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase))) &&
                (query.Length == 0 || Path.GetFileName(item.File.RelativePath).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void Filter()
        {
            IEnumerable<GalleryItem> filtered = Find(items, month.SelectedIndex, month.Text, kind.SelectedIndex, kind.Text,
                heart.SelectedIndex, source.SelectedIndex, source.Text, search.Text.Trim(),
                fromDate.Checked ? (DateTime?)fromDate.Value : null, toDate.Checked ? (DateTime?)toDate.Value : null);
            if (selectedOnly.Checked) filtered = filtered.Where(selected.Contains);
            switch (order.SelectedIndex)
            {
                case 1: filtered = filtered.OrderByDescending(item => item.Date.HasValue).ThenBy(item => item.Date); break;
                case 2: filtered = filtered.OrderByDescending(item => item.Files().Sum(file => file.Size ?? 0)); break;
                case 3: filtered = filtered.OrderBy(item => Path.GetFileName(item.File.RelativePath), StringComparer.OrdinalIgnoreCase); break;
                default: filtered = filtered.OrderByDescending(item => item.Date); break;
            }
            updating = true;
            try
            {
                grid.CurrentCell = null;
                visible = filtered.ToList();
                grid.RowCount = visible.Count;
                if (visible.Count > 0) grid.CurrentCell = grid[1, 0];
                grid.Invalidate();
            }
            finally { updating = false; }
            UpdateCounts();
            QueuePreview(false);
        }

        private void Toggle(int index)
        {
            if (index < 0 || index >= visible.Count) return;
            GalleryItem item = visible[index];
            SetSelection(item, !selected.Contains(item));
            if (selectedOnly.Checked) Filter();
            else
            {
                grid.InvalidateRow(index);
                UpdateCounts();
            }
        }

        private void SelectVisible(bool value)
        {
            foreach (GalleryItem item in visible) SetSelection(item, value);
            if (selectedOnly.Checked) Filter();
            else
            {
                grid.Invalidate();
                UpdateCounts();
            }
        }

        private void SetSelection(GalleryItem item, bool value)
        {
            if (!(value ? selected.Add(item) : selected.Remove(item))) return;
            int increment = value ? 1 : -1;
            foreach (MediaFile file in item.Files())
            {
                selectedFiles += increment;
                if (file.Size.HasValue) selectedBytes += increment * file.Size.Value;
                else unknownSizes += increment;
            }
        }

        private void UpdateCounts()
        {
            counts.Text = "표시 " + visible.Count.ToString("N0") + "개  ·  선택 " + selected.Count.ToString("N0") + "개  (" +
                selectedFiles.ToString("N0") + "개 파일, " + FormatSize(selectedBytes) +
                (unknownSizes == 0 ? "" : " + 용량 미확인 " + unknownSizes.ToString("N0") + "개") + ")";
            confirm.Enabled = selected.Count > 0;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("N2") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("N1") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("N1") + " KB";
            return bytes + " B";
        }

        private void QueuePreview(bool original)
        {
            if (closeRequested) return;
            var item = grid.CurrentCell == null || grid.CurrentCell.RowIndex >= visible.Count ? null : visible[grid.CurrentCell.RowIndex];
            if (!original && item == requestedPreview && (previewPending || readingPreview || preview.Image != null)) return;
            requestedPreview = item;
            originalPreview = original;
            previewPending = item != null && item.Kind != "편집 정보";
            previewStatus.Text = item == null ? "항목을 선택하면 미리 보기가 표시됩니다." :
                item.Kind == "편집 정보" ? "사진 편집 정보 파일" : "미리 보기를 불러오는 중…";
            SetImage(null);
            previewDelay.Stop();
            if (previewPending && !readingPreview) previewDelay.Start();
        }

        private async Task ReadPreview()
        {
            if (!previewPending || closeRequested) return;
            GalleryItem item = requestedPreview;
            bool original = originalPreview;
            previewPending = false;
            readingPreview = true;
            Bitmap image = null;
            try
            {
                image = await Task.Run(() =>
                {
                    // Try the device-provided thumbnail before requesting an image file.
                    byte[] data = original ? library.ReadPhotoPreview(item.File, previewCancel.Token) : library.ReadThumbnail(item.File, previewCancel.Token);
                    return DecodePreview(data);
                });
                if (!closeRequested && requestedPreview == item && !previewPending)
                {
                    SetImage(image);
                    image = null;
                    previewStatus.Text = preview.Image != null ? Path.GetFileName(item.File.RelativePath) :
                        item.File.IsVideo ? "이 동영상은 USB 미리 보기를 제공하지 않습니다." :
                        original ? "이 사진은 미리 볼 수 없습니다. 12MB 이하의 사진을 지원합니다." :
                        "썸네일 없음 · 아래 버튼으로 사진 열기";
                }
            }
            catch (OperationCanceledException) { }
            catch (COMException)
            {
                if (!closeRequested && requestedPreview == item) previewStatus.Text = "아이폰의 USB 연결을 확인해 주세요.";
            }
            finally
            {
                if (image != null) image.Dispose();
                readingPreview = false;
                if (closeRequested) { DialogResult = closeResult; Close(); }
                else if (previewPending) previewDelay.Start();
            }
        }

        internal static Bitmap DecodePreview(byte[] bytes)
        {
            if (bytes == null) return null;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                {
                    var decoded = new BitmapImage();
                    decoded.BeginInit();
                    decoded.StreamSource = stream;
                    decoded.CacheOption = BitmapCacheOption.OnLoad;
                    decoded.DecodePixelWidth = 560;
                    decoded.EndInit();
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(decoded));
                    using (var encoded = new MemoryStream())
                    {
                        png.Save(encoded);
                        encoded.Position = 0;
                        using (var bitmap = new Bitmap(encoded)) return new Bitmap(bitmap);
                    }
                }
            }
            catch (NotSupportedException) { return null; }
            catch (ArgumentException) { return null; }
            catch (FileFormatException) { return null; }
            catch (IOException) { return null; }
            catch (COMException) { return null; }
        }

        private void SetImage(Bitmap image)
        {
            var previous = preview.Image;
            preview.Image = image;
            if (previous != null) previous.Dispose();
        }

        private void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (!readingPreview) return;
            args.Cancel = true;
            if (closeRequested) { DialogResult = DialogResult.None; return; }
            closeRequested = true;
            closeResult = DialogResult == DialogResult.OK ? DialogResult.OK : DialogResult.Cancel;
            DialogResult = DialogResult.None;
            confirm.Enabled = false;
            previewDelay.Stop();
            previewCancel.Cancel();
            previewStatus.Text = "미리 보기를 닫는 중…";
        }
    }
}
