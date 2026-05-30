using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClosedXML.Excel;
using Microsoft.Win32;
using OrisTpsWriter.Core;

namespace OrisTpsWriter
{
    public partial class MainWindow : Window
    {
        // ────────────────────────────────────────────────────────
        // Writer tab — model classes
        // ────────────────────────────────────────────────────────
        public class FieldRow
        {
            public int    Seq      { get; set; }
            public string Name     { get; set; }
            public string TypeName { get; set; }
            public int    Length   { get; set; }
        }

        public class KeyRow
        {
            public string       KeyName    { get; set; }
            public List<string> FieldNames { get; set; }
            public override string ToString() =>
                $"{KeyName}  ({string.Join(", ", FieldNames)})";
        }

        // ────────────────────────────────────────────────────────
        // Multi-Export row
        // ────────────────────────────────────────────────────────
        public class ExportFileRow : INotifyPropertyChanged
        {
            private bool   _isChecked = true;
            private string _status    = "–";
            private double _progress  = 0;

            public bool IsChecked
            {
                get => _isChecked;
                set { _isChecked = value; OnPropChanged(nameof(IsChecked)); }
            }
            public string FileName { get; set; }
            public string FullPath { get; set; }
            public string Status
            {
                get => _status;
                set { _status = value; OnPropChanged(nameof(Status)); }
            }
            public double Progress
            {
                get => _progress;
                set
                {
                    _progress = value;
                    OnPropChanged(nameof(Progress));
                    OnPropChanged(nameof(ProgressText));
                }
            }
            public string ProgressText => $"{_progress:0}%";

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropChanged(string name) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ────────────────────────────────────────────────────────
        // Writer state
        // ────────────────────────────────────────────────────────
        private readonly ObservableCollection<FieldRow> _fields = new();
        private readonly ObservableCollection<KeyRow>   _keys   = new();
        private DataTable _dataTable = new();

        // ────────────────────────────────────────────────────────
        // Reader state
        // ────────────────────────────────────────────────────────
        private List<TpsField>          _readerFields    = new();
        private Dictionary<int, byte[]> _rawRecords      = new();
        private bool                    _isConverted     = false;
        private DataTable               _readerTable     = new();

        // TpsTable for CRUD
        private TpsTable _openedTable    = null;
        private string   _openedFilePath = null;
        private bool     _isInsertMode   = false;
        private int      _editRecordNum  = -1;
        private readonly List<TextBox> _editBoxes = new();

        // ────────────────────────────────────────────────────────
        // Multi-Export state
        // ────────────────────────────────────────────────────────
        private readonly ObservableCollection<ExportFileRow> _exportFiles = new();

        // ────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();

            // Writer init
            LstFields.ItemsSource  = _fields;
            LstKeys.ItemsSource    = _keys;
            _fields.CollectionChanged += (s, e) => RefreshKeyFieldList();
            GridData.ItemsSource   = _dataTable.DefaultView;

            // Reader fonts
            string[] fontList = {
                "Segoe UI", "Arial", "Verdana", "Tahoma", "Calibri",
                "Courier New", "Consolas", "Times New Roman",
                "Sylfaen", "BPG Nino Mtavruli", "BPG Akademiuri",
                "BPG Arial", "BPG DejaVu Sans", "FreeSet"
            };
            foreach (var f in fontList)
                CmbReaderFont.Items.Add(f);
            CmbReaderFont.SelectedIndex = 0;

            // Multi-Export init
            GridExport.ItemsSource = _exportFiles;
            _exportFiles.CollectionChanged += (s, e) => UpdateExportStatus();
        }

        // ════════════════════════════════════════════════════════
        // TAB 1 — TPS READER
        // ════════════════════════════════════════════════════════

        private void BtnOpenTps_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "TPS ფაილები (*.tps)|*.tps|ყველა ფაილი (*.*)|*.*",
                Title  = "TPS ფაილის არჩევა"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                // TpsReader — for raw-byte display
                var reader    = new TpsReader(dlg.FileName);
                _readerFields = reader.Fields;
                _rawRecords   = reader.DataRecords;

                // TpsTable — for CRUD
                _openedTable    = TpsTable.Open(dlg.FileName);
                _openedFilePath = dlg.FileName;

                // Reset convert toggle
                _isConverted          = false;
                BtnConvert.Content    = "🔤  კონვერტაცია";
                BtnConvert.Background = (Brush)FindResource("Panel");
                BtnConvert.Foreground = (Brush)FindResource("Ink");

                TxtOpenedFile.Text = dlg.FileName;
                EditPanel.Visibility = Visibility.Collapsed;

                // Enable CRUD bar
                CrudBar.IsEnabled = true;
                CrudBar.Opacity   = 1.0;

                RefreshReaderGrid();

                TxtReaderStatus.Text =
                    $"{Path.GetFileName(dlg.FileName)} — " +
                    $"{_rawRecords.Count} ჩანაწერი · {_readerFields.Count} ფილდი";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "TPS გახსნის შეცდომა",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Column display name: drop the "TABLE:" prefix (everything up to and
        /// including the first ':') so the grid shows only the field name.
        /// </summary>
        private static string FieldDisplayName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return fieldName;
            int colon = fieldName.IndexOf(':');
            return colon >= 0 && colon < fieldName.Length - 1
                ? fieldName.Substring(colon + 1)
                : fieldName;
        }

        /// <summary>
        /// Fields shown / exported. GROUP fields (0x16) are overlays that span
        /// the same bytes as the individual fields after them, so including one
        /// would merge several real fields into a single column — skip them.
        /// </summary>
        private static bool IsDisplayField(TpsField f) => f.Type != FieldType.Group;

        private void RefreshReaderGrid()
        {
            _readerTable = new DataTable();
            _readerTable.Columns.Add("#", typeof(int));

            if (_openedTable != null)
            {
                var fields = _openedTable.Fields.Where(IsDisplayField).ToList();
                // Use TpsTable — decoded Georgian values
                foreach (var f in fields)
                    _readerTable.Columns.Add(f.Name, typeof(string));

                foreach (var (recNum, values) in _openedTable.AllRows())
                {
                    var row = _readerTable.NewRow();
                    row["#"] = recNum;
                    foreach (var f in fields)
                        row[f.Name] = values.TryGetValue(f.Name, out var v) ? v?.ToString() ?? "" : "";
                    _readerTable.Rows.Add(row);
                }
            }
            else
            {
                // Fallback: raw bytes (no TpsTable loaded)
                var fields = _readerFields.Where(IsDisplayField).ToList();
                foreach (var f in fields)
                    _readerTable.Columns.Add(f.Name, typeof(string));

                var enc = Encoding.GetEncoding(1252);
                foreach (var kvp in _rawRecords.OrderBy(k => k.Key))
                {
                    var row = _readerTable.NewRow();
                    row["#"] = kvp.Key;
                    foreach (var f in fields)
                    {
                        int end = f.Offset + f.Length;
                        if (end > kvp.Value.Length) continue;
                        var chunk = new byte[f.Length];
                        Array.Copy(kvp.Value, f.Offset, chunk, 0, f.Length);
                        row[f.Name] = _isConverted
                            ? OrisEncoding.FromFixedField(chunk)
                            : enc.GetString(chunk).TrimEnd();
                    }
                    _readerTable.Rows.Add(row);
                }
            }

            GridReader.Columns.Clear();
            foreach (DataColumn col in _readerTable.Columns)
            {
                // Fixed starting widths (not Star) so many columns overflow the
                // viewport horizontally — that is what makes the bottom scrollbar
                // appear and lets each column be resized independently.
                DataGridLength width = col.ColumnName == "#"
                    ? new DataGridLength(50)
                    : new DataGridLength(160);

                GridReader.Columns.Add(new DataGridTextColumn
                {
                    // Header shows only the field name; binding uses the real
                    // (prefixed) column key so values still resolve correctly.
                    Header  = col.ColumnName == "#" ? "#" : FieldDisplayName(col.ColumnName),
                    Binding = new System.Windows.Data.Binding($"[{col.ColumnName}]"),
                    Width   = width,
                    MinWidth = 40,
                });
            }
            GridReader.ItemsSource = _readerTable.DefaultView;
        }

        private void CmbReaderFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridReader == null) return;
            if (CmbReaderFont.SelectedItem is string fontName)
                GridReader.FontFamily = new FontFamily(fontName);
        }

        private void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            _isConverted = !_isConverted;
            BtnConvert.Content    = _isConverted ? "✅  Unicode (ქართ.)" : "🔤  კონვერტაცია";
            BtnConvert.Background = _isConverted
                ? (Brush)FindResource("Accent")
                : (Brush)FindResource("Panel");
            BtnConvert.Foreground = _isConverted
                ? Brushes.White
                : (Brush)FindResource("Ink");

            if (_readerFields.Count > 0)
                RefreshReaderGrid();
        }

        // ────────────────────────────────────────────────────────
        // CRUD — INSERT / UPDATE / DELETE / SAVE
        // ────────────────────────────────────────────────────────

        private void BtnInsert_Click(object sender, RoutedEventArgs e)
        {
            if (_openedTable == null) return;
            BuildEditPanel(isInsert: true);
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_openedTable == null) return;
            if (GridReader.SelectedItem is not DataRowView drv)
            { TxtReaderStatus.Text = "მონიშნე ჩანაწერი შესაცვლელად."; return; }
            int recNum = Convert.ToInt32(drv.Row["#"]);
            BuildEditPanel(isInsert: false, recordNum: recNum);
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_openedTable == null) return;
            if (GridReader.SelectedItem is not DataRowView drv)
            { TxtReaderStatus.Text = "მონიშნე ჩანაწერი წასაშლელად."; return; }

            int recNum = Convert.ToInt32(drv.Row["#"]);
            var confirm = MessageBox.Show(
                $"ნამდვილად გსურს ჩანაწერი #{recNum} წაშლა?",
                "DELETE — დადასტურება", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            _openedTable.Delete(recNum);
            RefreshReaderGrid();
            TxtReaderStatus.Text = $"ჩანაწერი #{recNum} წაიშალა. (💾 შენახვა — ცვლილებები ფაილში)";
        }

        // ────────────────────────────────────────────────────────
        // Bulk import from Excel / CSV → opened table
        // ────────────────────────────────────────────────────────
        private async void BtnImportData_Click(object sender, RoutedEventArgs e)
        {
            if (_openedTable == null)
            { TxtReaderStatus.Text = "ჯერ გახსენი TPS ფაილი."; return; }

            var dlg = new OpenFileDialog
            {
                Filter = "Excel/CSV (*.xlsx;*.xls;*.csv)|*.xlsx;*.xls;*.csv|" +
                         "Excel ფაილები (*.xlsx;*.xls)|*.xlsx;*.xls|" +
                         "CSV ფაილები (*.csv)|*.csv|ყველა ფაილი (*.*)|*.*",
                Title  = "Excel ან CSV ფაილის არჩევა იმპორტისთვის"
            };
            if (dlg.ShowDialog() != true) return;

            BeginReaderProgress("📥 იმპორტი მიმდინარეობს…");
            SetCrudEnabled(false);
            try
            {
                string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                string file = dlg.FileName;

                // Read file off the UI thread.
                var rows = await Task.Run(() =>
                    ext == ".csv" ? ReadCsvRows(file) : ReadXlsxRows(file));

                if (rows.Count == 0)
                { EndReaderProgress("ფაილი ცარიელია."); return; }

                var fieldNames   = _openedTable.Fields.Select(f => f.Name).ToList();
                int[] colToField = MapColumns(rows[0], fieldNames, out bool hasHeader);
                int startRow     = hasHeader ? 1 : 0;

                int total = rows.Count - startRow;
                var toInsert = new List<Dictionary<string, object>>(Math.Max(0, total));
                for (int r = startRow; r < rows.Count; r++)
                {
                    var cells = rows[r];
                    if (!cells.All(string.IsNullOrWhiteSpace))
                    {
                        var values = new Dictionary<string, object>();
                        for (int c = 0; c < cells.Length; c++)
                        {
                            int fi = colToField[c];
                            if (fi >= 0) values[fieldNames[fi]] = cells[c];
                        }
                        toInsert.Add(values);
                    }
                    await ReportReaderProgressThrottled(r - startRow + 1, total);
                }

                if (toInsert.Count == 0)
                { EndReaderProgress("იმპორტისთვის ჩანაწერი ვერ მოიძებნა."); return; }

                var added = _openedTable.InsertMany(toInsert);
                ReportReaderProgress(100);
                RefreshReaderGrid();
                EndReaderProgress(
                    $"📥 იმპორტირდა {added.Count} ჩანაწერი " +
                    $"({(hasHeader ? "header-ით" : "პოზიციურად")}). " +
                    $"(💾 შენახვა — ცვლილებები ფაილში)");
            }
            catch (Exception ex)
            {
                EndReaderProgress(null);
                MessageBox.Show(ex.Message, "იმპორტის შეცდომა",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetCrudEnabled(true);
            }
        }

        // ── Reader progress-bar helpers (Tab 1) ──────────────────
        private int _lastReaderPct = -1;

        private void BeginReaderProgress(string message)
        {
            _lastReaderPct = -1;
            ReaderProgressBox.Visibility = Visibility.Visible;
            ReportReaderProgress(0);
            if (message != null) TxtReaderStatus.Text = message;
        }

        private void ReportReaderProgress(double pct)
        {
            pct = Math.Clamp(pct, 0, 100);
            PrgReader.Value = pct;
            TxtReaderProgress.Text = $"{pct:0}%";
        }

        /// <summary>Updates the bar only when the integer % changes, yielding so the UI repaints.</summary>
        private async Task ReportReaderProgressThrottled(int done, int total)
        {
            int pct = total <= 0 ? 100 : (int)(done * 100.0 / total);
            if (pct != _lastReaderPct)
            {
                _lastReaderPct = pct;
                ReportReaderProgress(pct);
                await Task.Yield();
            }
        }

        private void EndReaderProgress(string finalMessage)
        {
            ReaderProgressBox.Visibility = Visibility.Collapsed;
            if (finalMessage != null) TxtReaderStatus.Text = finalMessage;
        }

        private void SetCrudEnabled(bool on)
        {
            BtnInsert.IsEnabled     = on;
            BtnUpdate.IsEnabled     = on;
            BtnDelete.IsEnabled     = on;
            BtnImportData.IsEnabled = on;
            BtnExportData.IsEnabled = on;
            BtnSaveTps.IsEnabled    = on;
        }

        /// <summary>
        /// აბრუნებს column→fieldIndex მეპინგს. თუ პირველი რიგი ემთხვევა ფილდების
        /// სახელებს — header-ია და მეპინგი სახელებით კეთდება; თუ არა — პოზიციური.
        /// -1 ნიშნავს, რომ სვეტი იგნორირდება.
        /// </summary>
        private static int[] MapColumns(string[] firstRow, List<string> fieldNames, out bool hasHeader)
        {
            int matches = firstRow.Count(c =>
                fieldNames.Any(fn => fn.Equals(c?.Trim(), StringComparison.OrdinalIgnoreCase)));
            hasHeader = firstRow.Length > 0 && matches >= Math.Max(1, firstRow.Length / 2);

            var map = new int[firstRow.Length];
            if (hasHeader)
            {
                for (int c = 0; c < firstRow.Length; c++)
                    map[c] = fieldNames.FindIndex(fn =>
                        fn.Equals(firstRow[c]?.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                for (int c = 0; c < firstRow.Length; c++)
                    map[c] = c < fieldNames.Count ? c : -1;
            }
            return map;
        }

        private static List<string[]> ReadXlsxRows(string path)
        {
            var result = new List<string[]>();
            using var wb  = new XLWorkbook(path);
            var ws        = wb.Worksheets.First();
            var used      = ws.RangeUsed();
            if (used == null) return result;

            int firstRow = used.FirstRow().RowNumber();
            int lastRow  = used.LastRow().RowNumber();
            int firstCol = used.FirstColumn().ColumnNumber();
            int lastCol  = used.LastColumn().ColumnNumber();

            for (int r = firstRow; r <= lastRow; r++)
            {
                var cells = new string[lastCol - firstCol + 1];
                for (int c = firstCol; c <= lastCol; c++)
                    cells[c - firstCol] = ws.Cell(r, c).GetString().Trim();
                result.Add(cells);
            }
            return result;
        }

        private static List<string[]> ReadCsvRows(string path)
        {
            var result = new List<string[]>();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0) { result.Add(Array.Empty<string>()); continue; }
                result.Add(ParseCsvLine(line));
            }
            return result;
        }

        /// <summary>CSV ხაზის გარჩევა ბრჭყალებისა და escaped ბრჭყალების მხარდაჭერით.</summary>
        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var sb     = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(ch);
                }
                else
                {
                    if (ch == '"') inQuotes = true;
                    else if (ch == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
                    else sb.Append(ch);
                }
            }
            fields.Add(sb.ToString().Trim());
            return fields.ToArray();
        }

        // ────────────────────────────────────────────────────────
        // Export opened table → Excel / CSV (current in-memory state,
        // includes any INSERT / UPDATE / DELETE done since file open)
        // ────────────────────────────────────────────────────────
        private async void BtnExportData_Click(object sender, RoutedEventArgs e)
        {
            if (_openedTable == null)
            { TxtReaderStatus.Text = "ჯერ გახსენი TPS ფაილი."; return; }

            string baseName = string.IsNullOrEmpty(_openedFilePath)
                ? "export"
                : Path.GetFileNameWithoutExtension(_openedFilePath);

            var dlg = new SaveFileDialog
            {
                Filter   = "Excel ფაილი (*.xlsx)|*.xlsx|CSV ფაილი (*.csv)|*.csv",
                FileName = baseName + ".xlsx",
                Title    = "Excel ან CSV ფაილში ექსპორტი"
            };
            if (dlg.ShowDialog() != true) return;

            // Skip GROUP overlay fields so columns don't merge (matches the grid).
            var fields = _openedTable.Fields.Where(IsDisplayField).ToList();
            var rows   = _openedTable.AllRows().ToList();
            string path = dlg.FileName;
            string ext  = Path.GetExtension(path).ToLowerInvariant();

            BeginReaderProgress("📤 ექსპორტი მიმდინარეობს…");
            SetCrudEnabled(false);
            try
            {
                // marshals % updates back to the UI thread
                var progress = new Progress<double>(ReportReaderProgress);
                await Task.Run(() =>
                {
                    if (ext == ".csv") ExportCsv(path, fields, rows, progress);
                    else               ExportXlsx(path, fields, rows, progress);
                });

                ReportReaderProgress(100);
                EndReaderProgress(
                    $"📤 ექსპორტი დასრულდა: {Path.GetFileName(path)} " +
                    $"({rows.Count} ჩანაწერი · {fields.Count} ფილდი)");
                MessageBox.Show(
                    $"ფაილი შენახულია:\n{path}\n\n" +
                    $"ჩანაწერები: {rows.Count}\nფილდები: {fields.Count}",
                    "ექსპორტი", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                EndReaderProgress(null);
                MessageBox.Show(ex.Message, "ექსპორტის შეცდომა",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetCrudEnabled(true);
            }
        }

        private static void ExportXlsx(
            string path, List<TpsField> fields,
            List<(int RecordNumber, Dictionary<string, object> Values)> rows,
            IProgress<double> progress = null)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Data");

            // Header row — field name only (drop the "TABLE:" prefix)
            for (int c = 0; c < fields.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = FieldDisplayName(fields[c].Name);
                cell.Style.Font.Bold            = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2D6A4F");
                cell.Style.Font.FontColor       = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // Data rows — reserve last 5% for the file write
            int r = 2, done = 0, lastPct = -1;
            foreach (var (_, values) in rows)
            {
                for (int c = 0; c < fields.Count; c++)
                    ws.Cell(r, c + 1).Value =
                        values.TryGetValue(fields[c].Name, out var v) ? v?.ToString() ?? "" : "";
                r++; done++;
                int pct = rows.Count == 0 ? 95 : (int)(done * 95.0 / rows.Count);
                if (pct != lastPct) { lastPct = pct; progress?.Report(pct); }
            }
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();
            wb.SaveAs(path);
            progress?.Report(100);
        }

        private static void ExportCsv(
            string path, List<TpsField> fields,
            List<(int RecordNumber, Dictionary<string, object> Values)> rows,
            IProgress<double> progress = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", fields.Select(f => CsvEscape(FieldDisplayName(f.Name)))));
            int done = 0, lastPct = -1;
            foreach (var (_, values) in rows)
            {
                var cells = fields.Select(f =>
                    CsvEscape(values.TryGetValue(f.Name, out var v) ? v?.ToString() ?? "" : ""));
                sb.AppendLine(string.Join(",", cells));
                done++;
                int pct = rows.Count == 0 ? 95 : (int)(done * 95.0 / rows.Count);
                if (pct != lastPct) { lastPct = pct; progress?.Report(pct); }
            }
            // UTF-8 with BOM so Excel opens Georgian text correctly
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            progress?.Report(100);
        }

        private static string CsvEscape(string s)
        {
            s ??= "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private void BuildEditPanel(bool isInsert, int recordNum = -1)
        {
            _isInsertMode  = isInsert;
            _editRecordNum = recordNum;
            _editBoxes.Clear();
            EditFieldsPanel.Children.Clear();

            TxtEditPanelTitle.Text = isInsert
                ? "➕  ახალი ჩანაწერი"
                : $"✏️  ჩანაწერის რედაქტირება  (#{recordNum})";

            Dictionary<string, object> existing = null;
            if (!isInsert && recordNum >= 0)
            {
                try { existing = _openedTable.Get(recordNum); }
                catch { /* record not found */ }
            }

            foreach (var f in _openedTable.Fields.Where(IsDisplayField))
            {
                var cell = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin      = new Thickness(0, 0, 12, 8),
                    Width       = 220
                };

                cell.Children.Add(new TextBlock
                {
                    Text       = FieldDisplayName(f.Name),
                    FontSize   = 11,
                    Foreground = (Brush)FindResource("Muted"),
                    Margin     = new Thickness(0, 0, 0, 3)
                });

                string val = "";
                if (existing != null && existing.TryGetValue(f.Name, out var v))
                    val = v?.ToString() ?? "";

                var tb = new TextBox
                {
                    Text            = val,
                    FontSize        = 13,
                    Padding         = new Thickness(7, 5, 7, 5),
                    BorderBrush     = (Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1)
                };
                cell.Children.Add(tb);
                EditFieldsPanel.Children.Add(cell);
                _editBoxes.Add(tb);
            }

            EditPanel.Visibility = Visibility.Visible;
            // focus first box
            if (_editBoxes.Count > 0)
                _editBoxes[0].Focus();
        }

        private void BtnEditOk_Click(object sender, RoutedEventArgs e)
        {
            if (_openedTable == null) return;
            var values = new Dictionary<string, object>();
            // Must mirror BuildEditPanel's field order (GROUP overlays skipped).
            var editFields = _openedTable.Fields.Where(IsDisplayField).ToList();
            for (int i = 0; i < editFields.Count && i < _editBoxes.Count; i++)
                values[editFields[i].Name] = _editBoxes[i].Text;

            try
            {
                if (_isInsertMode)
                {
                    int newRec = _openedTable.Insert(values);
                    TxtReaderStatus.Text = $"ჩანაწერი #{newRec} დაემატა. (💾 შენახვა — ცვლილებები ფაილში)";
                }
                else
                {
                    _openedTable.Update(_editRecordNum, values);
                    TxtReaderStatus.Text = $"ჩანაწერი #{_editRecordNum} განახლდა. (💾 შენახვა — ცვლილებები ფაილში)";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            EditPanel.Visibility = Visibility.Collapsed;
            RefreshReaderGrid();
        }

        private void BtnEditCancel_Click(object sender, RoutedEventArgs e) =>
            EditPanel.Visibility = Visibility.Collapsed;

        private void BtnSaveTps_Click(object sender, RoutedEventArgs e)
        {
            if (_openedTable == null) return;
            try
            {
                int bytes = _openedTable.Save(_openedFilePath, backup: true);
                TxtReaderStatus.Text = $"✅ შენახულია: {Path.GetFileName(_openedFilePath)} ({bytes:N0} bytes) — backup შეიქმნა.";
                MessageBox.Show(
                    $"ფაილი შენახულია!\n\nგზა: {_openedFilePath}\nზომა: {bytes:N0} bytes\n\n" +
                    $"ძველი ვერსია შენახულია .bak ფაილში.",
                    "შენახვა", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "შენახვის შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════
        // TAB 2 — TPS WRITER
        // ════════════════════════════════════════════════════════

        private void BtnAddField_Click(object sender, RoutedEventArgs e)
        {
            string name = (TxtFieldName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) { Status("შეიყვანე ფილდის სახელი.", true); return; }
            if (_fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            { Status($"ფილდი '{name}' უკვე არსებობს.", true); return; }

            string typeName = ((ComboBoxItem)CmbFieldType.SelectedItem).Content.ToString();
            int    length   = TypeFixedLength(typeName);
            if (typeName == "STRING")
            {
                if (!int.TryParse(TxtFieldLen.Text, out length) || length < 1)
                { Status("STRING ფილდს სჭირდება სწორი სიგრძე.", true); return; }
            }

            _fields.Add(new FieldRow { Seq = _fields.Count, Name = name, TypeName = typeName, Length = length });
            TxtFieldName.Clear();
            RebuildDataColumns();
            Status($"ფილდი '{name}' დაემატა.");
        }

        private void BtnRemoveField_Click(object sender, RoutedEventArgs e)
        {
            if (LstFields.SelectedItem is FieldRow fr)
            {
                _fields.Remove(fr);
                for (int i = 0; i < _fields.Count; i++) _fields[i].Seq = i;
                LstFields.Items.Refresh();
                RebuildDataColumns();
                Status($"ფილდი '{fr.Name}' წაიშალა.");
            }
            else Status("მონიშნე ფილდი წასაშლელად.", true);
        }

        private static int TypeFixedLength(string t) => t switch
        {
            "LONG" => 4, "ULONG" => 4, "SHORT" => 2, _ => 30
        };

        private void RefreshKeyFieldList() =>
            LstKeyFields.ItemsSource = _fields.Select(f => new { f.Name }).ToList();

        private void BtnAddKey_Click(object sender, RoutedEventArgs e)
        {
            string keyName = (TxtKeyName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(keyName)) { Status("შეიყვანე key-ის სახელი.", true); return; }
            var selected = LstKeyFields.SelectedItems.Cast<object>()
                .Select(o => (string)o.GetType().GetProperty("Name").GetValue(o)).ToList();
            if (selected.Count == 0) { Status("მონიშნე key-ის მინიმუმ ერთი ფილდი.", true); return; }
            _keys.Add(new KeyRow { KeyName = keyName, FieldNames = selected });
            TxtKeyName.Clear();
            Status($"key '{keyName}' დაემატა.");
        }

        private void BtnRemoveKey_Click(object sender, RoutedEventArgs e)
        {
            if (LstKeys.SelectedItem is KeyRow kr)
            {
                _keys.Remove(kr);
                Status($"key '{kr.KeyName}' წაიშალა.");
            }
            else Status("მონიშნე key წასაშლელად.", true);
        }

        private void RebuildDataColumns()
        {
            var oldData = _dataTable;
            _dataTable = new DataTable();
            foreach (var f in _fields) _dataTable.Columns.Add(f.Name, typeof(string));
            if (oldData.Rows.Count > 0)
            {
                foreach (DataRow oldRow in oldData.Rows)
                {
                    var newRow = _dataTable.NewRow();
                    foreach (var f in _fields)
                        if (oldData.Columns.Contains(f.Name))
                            newRow[f.Name] = oldRow[f.Name];
                    _dataTable.Rows.Add(newRow);
                }
            }
            GridData.Columns.Clear();
            foreach (var f in _fields)
                GridData.Columns.Add(new DataGridTextColumn
                {
                    Header  = f.Name,
                    Binding = new System.Windows.Data.Binding($"[{f.Name}]"),
                    Width   = new DataGridLength(1, DataGridLengthUnitType.Star)
                });
            GridData.ItemsSource = _dataTable.DefaultView;
        }

        private void BtnDemo_Click(object sender, RoutedEventArgs e)
        {
            _fields.Clear(); _keys.Clear();
            TxtTableName.Text = "UNNAMED";
            _fields.Add(new FieldRow { Seq = 0, Name = "ARN:KADR", TypeName = "STRING", Length = 50 });
            _fields.Add(new FieldRow { Seq = 1, Name = "ARN:SECT", TypeName = "STRING", Length = 20 });
            LstFields.Items.Refresh();
            RefreshKeyFieldList();
            _keys.Add(new KeyRow { KeyName = "ARN:K1", FieldNames = new List<string> { "ARN:SECT", "ARN:KADR" } });
            RebuildDataColumns();
            string[] names = {
                "ოქრიაშვილი გ. .", "გოგიჩაძე ა. .", "კაპანაძე ე. .",
                "კიკვაძე დ. .", "ახვლედიანი ზ. .", "ბერიძე ნ. .", "მჭედლიშვილი თ. ."
            };
            foreach (var n in names)
            {
                var row = _dataTable.NewRow();
                row["ARN:KADR"] = n; row["ARN:SECT"] = "";
                _dataTable.Rows.Add(row);
            }
            Status($"Demo ჩაიტვირთა: {names.Length} ქართული გვარი.");
        }

        private void BtnAddRow_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0) { Status("ჯერ დაამატე ფილდები.", true); return; }
            _dataTable.Rows.Add(_dataTable.NewRow());
        }

        private void BtnDelRow_Click(object sender, RoutedEventArgs e)
        {
            if (GridData.SelectedItem is DataRowView drv)
            { drv.Row.Delete(); Status("რიგი წაიშალა."); }
            else Status("მონიშნე რიგი წასაშლელად.", true);
        }

        private void BtnImportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0) { Status("ჯერ დაამატე ფილდები.", true); return; }
            var dlg = new OpenFileDialog
            {
                Filter = "CSV ფაილები (*.csv)|*.csv|ყველა ფაილი (*.*)|*.*",
                Title  = "CSV ფაილის არჩევა"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8);
                int imported = 0; bool first = true;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (first)
                    {
                        first = false;
                        if (parts.Length > 0 &&
                            _fields.Any(f => f.Name.Equals(parts[0].Trim(), StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }
                    var row = _dataTable.NewRow();
                    for (int i = 0; i < _fields.Count && i < parts.Length; i++)
                        row[_fields[i].Name] = parts[i].Trim();
                    _dataTable.Rows.Add(row); imported++;
                }
                Status($"იმპორტირდა {imported} ჩანაწერი.");
            }
            catch (Exception ex) { Status($"CSV შეცდომა: {ex.Message}", true); }
        }

        private void BtnImportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0) { Status("ჯერ დაამატე ფილდები.", true); return; }
            var dlg = new OpenFileDialog
            {
                Filter = "Excel ფაილები (*.xlsx;*.xls)|*.xlsx;*.xls|ყველა ფაილი (*.*)|*.*",
                Title  = "Excel ფაილის არჩევა"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                using var wb   = new XLWorkbook(dlg.FileName);
                var ws         = wb.Worksheets.First();
                var usedRange  = ws.RangeUsed();
                if (usedRange == null) { Status("Excel ცხრილი ცარიელია.", true); return; }

                int firstRow = usedRange.FirstRow().RowNumber();
                int lastRow  = usedRange.LastRow().RowNumber();
                var firstVal = ws.Cell(firstRow, 1).GetString().Trim();
                if (_fields.Any(f => f.Name.Equals(firstVal, StringComparison.OrdinalIgnoreCase)))
                    firstRow++;

                int imported = 0;
                for (int r = firstRow; r <= lastRow; r++)
                {
                    var row = _dataTable.NewRow();
                    for (int i = 0; i < _fields.Count; i++)
                        row[_fields[i].Name] = ws.Cell(r, i + 1).GetString().Trim();
                    _dataTable.Rows.Add(row); imported++;
                }
                Status($"Excel-იდან იმპორტირდა {imported} ჩანაწერი.");
            }
            catch (Exception ex) { Status($"Excel შეცდომა: {ex.Message}", true); }
        }

        private OrisTable BuildTable()
        {
            var table = new OrisTable((TxtTableName.Text ?? "UNNAMED").Trim());
            foreach (var f in _fields)
            {
                IOrisField field = f.TypeName switch
                {
                    "LONG"  => new LongField(f.Name),
                    "ULONG" => new ULongField(f.Name),
                    "SHORT" => new ShortField(f.Name),
                    _       => new StringField(f.Name, f.Length),
                };
                table.AddField(field);
            }
            foreach (var k in _keys) table.AddKey(k.KeyName, k.FieldNames.ToArray());
            if (_keys.Count == 0 && _fields.Count > 0)
                table.AddKey(TxtTableName.Text.Trim() + ":K1", new[] { _fields[0].Name });

            GridData.CommitEdit(DataGridEditingUnit.Row, true);
            foreach (DataRow dr in _dataTable.Rows)
            {
                if (dr.RowState == DataRowState.Deleted) continue;
                var rowDict = new Dictionary<string, object>();
                foreach (var f in _fields)
                {
                    object val = dr[f.Name];
                    string s = val == DBNull.Value ? "" : val.ToString();
                    if (f.TypeName is "LONG" or "ULONG" or "SHORT")
                        rowDict[f.Name] = long.TryParse(s, out var n) ? n : 0;
                    else
                        rowDict[f.Name] = s;
                }
                table.AddRow(rowDict);
            }
            return table;
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0) { Status("ჯერ დაამატე ფილდები.", true); return; }
            var sb = new StringBuilder();
            sb.AppendLine($"ცხრილი: {TxtTableName.Text}");
            sb.AppendLine($"Record length: {_fields.Sum(f => f.Length)} bytes");
            sb.AppendLine($"ფილდები: {_fields.Count}");
            foreach (var f in _fields) sb.AppendLine($"   • {f.Name}  ({f.TypeName}, {f.Length})");
            sb.AppendLine($"Keys: {_keys.Count}");
            foreach (var k in _keys) sb.AppendLine($"   • {k}");
            sb.AppendLine($"ჩანაწერები: {_dataTable.Rows.Count}");
            MessageBox.Show(sb.ToString(), "სტრუქტურის გადახედვა",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0) { Status("ჯერ დაამატე ფილდები.", true); return; }
            var dlg = new SaveFileDialog
            {
                Filter   = "TPS ფაილები (*.tps)|*.tps|ყველა ფაილი (*.*)|*.*",
                FileName = (TxtTableName.Text ?? "table").Trim() + ".tps",
                Title    = ".tps ფაილის შენახვა"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var table = BuildTable();
                int bytes = table.Save(dlg.FileName);
                Status($"შენახულია: {Path.GetFileName(dlg.FileName)} ({bytes:N0} bytes, {table.RecordCount} ჩანაწერი)");
                MessageBox.Show(
                    $"ფაილი წარმატებით შეიქმნა!\n\nგზა: {dlg.FileName}\nზომა: {bytes:N0} bytes\n" +
                    $"ჩანაწერები: {table.RecordCount}\nფილდები: {table.FieldCount}\n\n" +
                    $"შეგიძლია გახსნა Clarion Viewer-ით ან ORIS-ით.",
                    "წარმატება", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Status($"შენახვის შეცდომა: {ex.Message}", true);
                MessageBox.Show(ex.ToString(), "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Status(string msg, bool error = false)
        {
            TxtStatus.Text       = msg;
            TxtStatus.Foreground = error
                ? (Brush)FindResource("Danger")
                : (Brush)FindResource("Muted");
        }

        // ════════════════════════════════════════════════════════
        // TAB 3 — MULTI EXPORT
        // ════════════════════════════════════════════════════════

        private void BtnAddExportFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter      = "TPS ფაილები (*.tps)|*.tps|ყველა ფაილი (*.*)|*.*",
                Title       = "TPS ფაილების არჩევა",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var path in dlg.FileNames)
            {
                if (_exportFiles.Any(f => f.FullPath == path)) continue;
                _exportFiles.Add(new ExportFileRow
                {
                    FileName = Path.GetFileName(path),
                    FullPath = path
                });
            }
        }

        private void BtnRemoveExportFile_Click(object sender, RoutedEventArgs e)
        {
            var toRemove = GridExport.SelectedItems.Cast<ExportFileRow>().ToList();
            if (toRemove.Count == 0) { TxtExportStatus.Text = "მონიშნე ფაილი წასაშლელად."; return; }
            foreach (var r in toRemove) _exportFiles.Remove(r);
        }

        private void BtnSelectAllExport_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _exportFiles) f.IsChecked = true;
            GridExport.Items.Refresh();
        }

        private void BtnDeselectAllExport_Click(object sender, RoutedEventArgs e)
        {
            foreach (var f in _exportFiles) f.IsChecked = false;
            GridExport.Items.Refresh();
        }

        private async void BtnExportMulti_Click(object sender, RoutedEventArgs e)
        {
            var checked_ = _exportFiles.Where(f => f.IsChecked).ToList();
            if (!checked_.Any())
            {
                MessageBox.Show("მონიშნე მინიმუმ ერთი ფაილი.", "ექსპორტი",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dlg = new SaveFileDialog
            {
                Filter   = "Excel ფაილები (*.xlsx)|*.xlsx",
                FileName = "tps_export.xlsx",
                Title    = "Excel ფაილის შენახვა"
            };
            if (dlg.ShowDialog() != true) return;

            // Reset progress/status for the run.
            foreach (var f in checked_) { f.Progress = 0; f.Status = "⏳ რიგში…"; }
            string outPath = dlg.FileName;
            int ok = 0, fail = 0;

            // Progress<T> marshals each report back to the UI thread.
            var progress = new Progress<(ExportFileRow Row, double Pct, string Status)>(p =>
            {
                p.Row.Progress = p.Pct;
                if (p.Status != null) p.Row.Status = p.Status;
            });
            var report = (IProgress<(ExportFileRow, double, string)>)progress;

            TxtExportStatus.Text = "ექსპორტი მიმდინარეობს…";
            try
            {
                await Task.Run(() =>
                {
                    using var wb = new XLWorkbook();
                    foreach (var fileRow in checked_)
                    {
                        try
                        {
                            report.Report((fileRow, 5, "⏳ მუშავდება…"));
                            var tbl    = TpsTable.Open(fileRow.FullPath);
                            string wsn = Path.GetFileNameWithoutExtension(fileRow.FileName);
                            if (wsn.Length > 31) wsn = wsn[..31];
                            var ws = wb.AddWorksheet(wsn);

                            // Skip GROUP overlays so columns don't merge.
                            var flds = tbl.Fields.Where(IsDisplayField).ToList();

                            // Header row — field name only (drop "TABLE:" prefix)
                            for (int c = 0; c < flds.Count; c++)
                            {
                                var cell = ws.Cell(1, c + 1);
                                cell.Value = FieldDisplayName(flds[c].Name);
                                cell.Style.Font.Bold            = true;
                                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2D6A4F");
                                cell.Style.Font.FontColor       = XLColor.White;
                                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                            }

                            // Data rows — 5%‥95% spans the row writing
                            var allRows = tbl.AllRows().ToList();
                            int r = 2, done = 0, lastPct = -1;
                            foreach (var (_, values) in allRows)
                            {
                                for (int c = 0; c < flds.Count; c++)
                                {
                                    string fn = flds[c].Name;
                                    ws.Cell(r, c + 1).Value =
                                        values.TryGetValue(fn, out var v) ? v?.ToString() ?? "" : "";
                                }
                                r++; done++;
                                int pct = allRows.Count == 0 ? 95 : 5 + (int)(done * 90.0 / allRows.Count);
                                if (pct != lastPct) { lastPct = pct; report.Report((fileRow, pct, null)); }
                            }
                            ws.Columns().AdjustToContents();

                            report.Report((fileRow, 100, $"✅  {tbl.Count} ჩანაწერი"));
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            report.Report((fileRow, 0, $"❌  {ex.Message}"));
                            fail++;
                        }
                    }
                    wb.SaveAs(outPath);
                });

                TxtExportStatus.Text = $"ექსპორტი დასრულდა — {ok} წარმატება, {fail} შეცდომა.";
                MessageBox.Show(
                    $"Excel ფაილი შეიქმნა:\n{outPath}\n\n" +
                    $"შიტები: {ok}   შეცდომა: {fail}",
                    "ექსპორტი", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "შეცდომა", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateExportStatus()
        {
            TxtExportStatus.Text = _exportFiles.Count == 0
                ? "ფაილები არ არის დამატებული."
                : $"{_exportFiles.Count} ფაილი სიაში.";
        }
    }
}
