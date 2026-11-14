using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace iPhonePhotoBackup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly ComboBox phones = new ComboBox();
        private readonly TextBox destination = new TextBox();
        private readonly Button refresh = new Button();
        private readonly Button browse = new Button();
        private readonly Button gallery = new Button();
        private readonly Button backupAll = new Button();
        private readonly Button backupRemaining = new Button();
        private readonly Button stop = new Button();
        private readonly Button openFolder = new Button();
        private readonly Button openReport = new Button();
        private readonly Label status = new Label();
        private readonly Label current = new Label();
        private readonly ProgressBar bar = new ProgressBar();
        private CancellationTokenSource cancellation;
        private bool searching;
        private bool closing;
        private string outputFolder;
        private string reportPath;
        private string previousDevice;
        private List<MediaFile> previousSelection;

        public MainForm()
        {
            Text = "iPhone 사진 백업";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Malgun Gothic", 10);
            BackColor = Color.FromArgb(246, 248, 252);
            ForeColor = Color.FromArgb(23, 39, 58);
            ClientSize = new Size(670, 440);
            MinimumSize = new Size(610, 465);
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 18), ColumnCount = 3, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            foreach (int height in new[] { 50, 55, 42, 42, 54, 27, 39, 54, 42 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);

            var title = new Label
            {
                Text = Text, Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            AddWide(layout, title, 0);
            AddWide(layout, new Label
            {
                Text = "아이폰 잠금을 풀고 USB로 연결한 뒤 ‘신뢰’를 누르세요." + Environment.NewLine +
                    "백업 범위는 USB에 표시되는 파일입니다. 잠긴 ‘가려진 항목’은 확인할 수 없습니다.",
                Dock = DockStyle.Fill, ForeColor = Color.FromArgb(67, 83, 105)
            }, 1);

            layout.Controls.Add(RowLabel("아이폰"), 0, 2);
            phones.DropDownStyle = ComboBoxStyle.DropDownList;
            phones.Dock = DockStyle.Top;
            phones.AccessibleName = "백업할 아이폰";
            phones.SelectedIndexChanged += delegate { UpdateButtons(); };
            layout.Controls.Add(phones, 1, 2);
            SetupButton(refresh, "다시 검색", async delegate { await RefreshPhones(); });
            layout.Controls.Add(refresh, 2, 2);

            layout.Controls.Add(RowLabel("저장 위치"), 0, 3);
            destination.Dock = DockStyle.Top;
            destination.ReadOnly = true;
            destination.AccessibleName = "외장하드의 백업 폴더";
            layout.Controls.Add(destination, 1, 3);
            SetupButton(browse, "폴더 선택…", ChooseFolder);
            layout.Controls.Add(browse, 2, 3);

            SetupButton(gallery, "갤러리 열기", async delegate { await RunBackup(false, false); });
            SetupButton(backupAll, "전체 백업", async delegate { await RunBackup(true, false); });
            SetupButton(backupRemaining, "남은 파일만 백업", async delegate { await RunBackup(true, true); });
            backupAll.BackColor = Color.FromArgb(27, 77, 164);
            backupAll.ForeColor = Color.White;
            backupAll.FlatStyle = FlatStyle.Flat;
            backupAll.FlatAppearance.BorderSize = 0;
            SetupButton(stop, "중지", RequestStop);
            stop.Width = 76;
            AddWide(layout, ButtonRow(backupAll, backupRemaining, gallery, stop), 4);
            AcceptButton = gallery;

            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0, 5, 0, 4);
            AddWide(layout, bar, 5);
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "아이폰을 연결하고 백업할 방법을 선택하세요.";
            AddWide(layout, status, 6);
            current.Dock = DockStyle.Fill;
            current.ForeColor = Color.FromArgb(67, 83, 105);
            current.AutoEllipsis = true;
            AddWide(layout, current, 7);

            SetupButton(openFolder, "백업 폴더 열기", delegate { OpenOutput(outputFolder); });
            SetupButton(openReport, "백업 기록 열기", delegate { OpenOutput(reportPath); });
            AddWide(layout, ButtonRow(openFolder, openReport), 8);
            Shown += async delegate { await RefreshPhones(); };
            FormClosing += HandleClosing;
            UpdateButtons();
        }

        private static Label RowLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        }

        private static void SetupButton(Button button, string text, EventHandler click)
        {
            button.Text = text;
            button.Width = 145;
            button.Height = 33;
            button.Dock = DockStyle.Top;
            button.Click += click;
        }

        private static FlowLayoutPanel ButtonRow(params Button[] buttons)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            foreach (Button button in buttons)
            {
                button.Margin = new Padding(0, 4, 8, 0);
                row.Controls.Add(button);
            }
            return row;
        }

        private static void AddWide(TableLayoutPanel layout, Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 3);
        }

        private void UpdateButtons()
        {
            bool running = cancellation != null;
            phones.Enabled = !running && !searching && phones.Items.Count > 0;
            refresh.Enabled = !running && !searching;
            browse.Enabled = !running;
            gallery.Enabled = backupRemaining.Enabled = backupAll.Enabled = !running && !searching && phones.SelectedItem != null;
            stop.Enabled = running && !cancellation.IsCancellationRequested;
            openFolder.Enabled = !running && outputFolder != null;
            openReport.Enabled = !running && reportPath != null;
        }

        private async Task RefreshPhones()
        {
            var selected = phones.SelectedItem as Phone;
            searching = true;
            UpdateButtons();
            status.Text = "USB로 연결된 아이폰을 찾는 중입니다…";
            current.Text = "";
            try
            {
                List<Phone> devices = await Task.Run(() => PhoneLibrary.FindPhones());
                if (IsDisposed) return;
                phones.Items.Clear();
                foreach (Phone phone in devices) phones.Items.Add(phone);
                if (devices.Count == 0)
                {
                    int? code = await Task.Run(() => PhoneLibrary.AppleUsbProblemCode());
                    if (!IsDisposed) ShowPhoneUnavailable(code);
                    return;
                }
                int match = selected == null ? -1 : devices.FindIndex(phone => phone.Id == selected.Id);
                phones.SelectedIndex = Math.Max(match, 0);
                status.Text = "전체 백업을 시작하거나 갤러리에서 항목을 고르세요.";
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    phones.Items.Clear();
                    ShowError("아이폰을 검색하지 못했습니다.", error);
                }
            }
            finally
            {
                searching = false;
                if (!IsDisposed) UpdateButtons();
            }
        }

        private void ChooseFolder(object sender, EventArgs args)
        {
            PickFolder();
            UpdateButtons();
        }

        private bool PickFolder()
        {
            using (var picker = new FolderBrowserDialog { Description = "외장하드에서 사진과 동영상을 백업할 폴더를 선택하세요." })
            {
                if (Directory.Exists(destination.Text)) picker.SelectedPath = destination.Text;
                if (picker.ShowDialog(this) != DialogResult.OK) return false;
                destination.Text = picker.SelectedPath;
            }
            return true;
        }

        private async Task RunBackup(bool allFiles, bool checkFirst)
        {
            var phone = (Phone)phones.SelectedItem;
            if (allFiles && !Directory.Exists(destination.Text) && !PickFolder()) return;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            UpdateButtons();
            bar.Value = 0;
            bar.Style = ProgressBarStyle.Marquee;
            status.Text = "아이폰 사진 보관함을 읽는 중입니다…";
            current.Text = "";
            PhoneLibrary library = null;
            bool copying = false;
            try
            {
                List<MediaFile> files = null;
                List<GalleryItem> items = null;
                string metadataStatus = null;
                await Task.Run(() =>
                {
                    library = new PhoneLibrary(phone);
                    files = library.Scan(token, count => Post(() => current.Text = "파일 " + count.ToString("N0") + "개 찾음"));
                    token.ThrowIfCancellationRequested();
                    if (allFiles || files.Count == 0) return;

                    Post(() => current.Text = "아이폰의 즐겨찾기와 저장 앱 정보를 확인하는 중…");
                    List<PhotoMetadata> metadata = null;
                    try { metadata = PhotoCatalog.ReadDevice(phone, library.Identity, token); }
                    catch (Exception error)
                    {
                        if (!(error is IOException || error is UnauthorizedAccessException || error is ExternalException ||
                            error is EntryPointNotFoundException || error is DllNotFoundException || error is BadImageFormatException)) throw;
                        metadataStatus = "아이폰의 하트와 저장 앱 정보를 읽지 못했습니다. " + error.Message;
                    }
                    token.ThrowIfCancellationRequested();
                    items = PhotoCatalog.Build(files, metadata);
                    int matched = items.FindAll(item => item.Favorite.HasValue || item.Source != null).Count;
                    if (metadataStatus == null) metadataStatus = matched > 0 ?
                        "아이폰 사진 보관함 정보 확인 " + matched.ToString("N0") + "개 · 하트와 저장 앱은 확인된 항목에 표시됩니다." :
                        "하트와 저장 앱을 확인할 수 있는 항목이 없습니다.";
                });
                token.ThrowIfCancellationRequested();
                if (files.Count == 0) { ShowResult(new BackupResult()); return; }

                List<MediaFile> selection = files;
                if (!allFiles)
                {
                    selection = ChooseGalleryFiles(items, library, metadataStatus, token);
                    if (selection == null) return;
                }
                token.ThrowIfCancellationRequested();
                string target = destination.Text;

                outputFolder = null;
                reportPath = null;
                copying = true;
                bar.Style = ProgressBarStyle.Marquee;
                status.Text = checkFirst ? "외장하드의 완료 기록과 파일 정보를 대조하는 중입니다…" :
                    allFiles ? "전체 백업을 준비하는 중입니다…" : "선택한 파일의 백업을 준비하는 중입니다…";
                BackupResult result = await Task.Run(() =>
                {
                    var timer = Stopwatch.StartNew();
                    string lastPhase = null;
                    return Backup.Copy(target, library.Identity, selection, library.Read, token, (state, file, bytes) =>
                    {
                        string phase = state.Checking ? "완료 기록 대조 중" : state.Phase;
                        if (phase == lastPhase && timer.ElapsedMilliseconds < 110) return;
                        lastPhase = phase;
                        timer.Restart();
                        bool checking = state.Checking;
                        int done = checking ? state.Checked : state.Copied + state.Existing + state.Failed;
                        int total = state.Total;
                        Post(() => ShowProgress(done, total, file, bytes, phase));
                    }, checkFirst);
                });
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                status.Text = copying || allFiles ? "백업을 중지했습니다." : "갤러리 읽기를 중지했습니다.";
                current.Text = "";
            }
            catch (Exception error)
            {
                if (!copying && PhoneLibrary.IsDisconnectedError(error))
                {
                    // A WPD entry can remain visible after its backing USB session goes away.
                    phones.Items.Clear();
                    ShowPhoneUnavailable(PhoneLibrary.AppleUsbProblemCode());
                }
                else ShowError(copying || allFiles ? "백업을 마치지 못했습니다." : "갤러리를 열지 못했습니다.", error);
            }
            finally
            {
                try { if (library != null) library.Dispose(); }
                finally
                {
                    bar.Style = ProgressBarStyle.Blocks;
                    cancellation.Dispose();
                    cancellation = null;
                    if (closing) Close();
                    else UpdateButtons();
                }
            }
        }

        private List<MediaFile> ChooseGalleryFiles(List<GalleryItem> items, PhoneLibrary library, string metadataStatus, CancellationToken token)
        {
            bar.Style = ProgressBarStyle.Blocks;
            status.Text = "갤러리에서 백업할 항목을 고르세요.";
            current.Text = "";
            using (var window = new GalleryForm(items, library, metadataStatus,
                previousDevice == library.Identity ? previousSelection : null))
            {
                if (window.ShowDialog(this) != DialogResult.OK)
                {
                    status.Text = "갤러리를 닫았습니다.";
                    return null;
                }
                previousDevice = library.Identity;
                previousSelection = window.Selection;
            }
            if (destination.Text.Length == 0 && !PickFolder())
            {
                status.Text = "저장 위치를 선택한 뒤 갤러리를 다시 여세요.";
                return null;
            }
            token.ThrowIfCancellationRequested();
            if (MessageBox.Show(this, previousSelection.Count.ToString("N0") + "개 파일을 아래 위치에 백업할까요?" +
                Environment.NewLine + Environment.NewLine + destination.Text, Text, MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) == DialogResult.OK) return previousSelection;
            status.Text = "백업을 시작하지 않았습니다. 갤러리를 다시 열면 선택이 표시됩니다.";
            return null;
        }

        private void Post(Action action)
        {
            BeginInvoke((Action)delegate
            {
                if (cancellation != null && !cancellation.IsCancellationRequested) action();
            });
        }

        private void ShowProgress(int done, int total, MediaFile file, long bytes, string phase)
        {
            bar.Style = ProgressBarStyle.Blocks;
            bar.Maximum = total;
            bar.Value = done;
            status.Text = phase + "  " + done.ToString("N0") + " / " + total.ToString("N0");
            current.Text = file.RelativePath;
            if (bytes > 0)
            {
                string received = (bytes / (1024.0 * 1024)).ToString("N1");
                string expected = file.Size.HasValue ? " / " + (file.Size.Value / (1024.0 * 1024)).ToString("N1") : "";
                current.Text += "   (" + received + expected + " MB)";
            }
        }

        private void ShowResult(BackupResult result)
        {
            if (result.Total == 0)
            {
                status.Text = "아이폰에서 백업할 사진이나 동영상을 찾지 못했습니다.";
                current.Text = "아이폰 잠금과 ‘신뢰’ 설정을 확인하고 USB를 다시 연결해 보세요.";
                return;
            }

            outputFolder = result.Folder;
            reportPath = result.Report;
            int done = result.Copied + result.Existing + result.Failed;
            bar.Maximum = result.Total;
            bar.Value = done;
            status.Text = result.Cancelled ? "백업을 중지했습니다." : result.Stopped != null ? "백업이 중단되었습니다." :
                result.Failed > 0 ? "일부 파일을 백업하지 못했습니다." : "USB 목록의 백업 대상 파일을 모두 확인했습니다.";
            current.Text = "새로 복사 " + result.Copied.ToString("N0") + "개 · 기존 확인 " + result.Existing.ToString("N0") +
                "개 · 남은 파일 " + result.Remaining.ToString("N0") + "개 (실패 " + result.Failed.ToString("N0") + "개)";
            if (result.Stopped != null) current.Text += Environment.NewLine + result.Stopped;
        }

        private void ShowError(string title, Exception error)
        {
            status.Text = title;
            current.Text = error.Message;
            MessageBox.Show(this, title + Environment.NewLine + Environment.NewLine + error.Message +
                " (0x" + error.HResult.ToString("X8") + ")", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowPhoneUnavailable(int? code)
        {
            status.Text = code.HasValue ? "Windows에서 아이폰 USB 장치 오류가 발생했습니다. (코드 " + code.Value + ")" :
                "아이폰 사진 보관함에 연결할 수 없습니다.";
            current.Text = "아이폰 잠금을 풀고 다른 USB 포트에 연결하세요. ‘신뢰’를 누른 뒤 ‘다시 검색’을 누르세요.";
        }

        private void OpenOutput(string path)
        {
            if (path == null) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) { ShowError("백업 위치를 열 수 없습니다.", error); }
        }

        private void RequestStop(object sender, EventArgs args)
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            status.Text = "중지 요청을 처리 중입니다…";
            UpdateButtons();
        }

        private void HandleClosing(object sender, FormClosingEventArgs args)
        {
            if (cancellation == null) return;
            args.Cancel = true;
            closing = true;
            RequestStop(sender, args);
        }
    }
}
