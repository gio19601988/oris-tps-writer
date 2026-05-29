using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClosedXML.Excel;
using Microsoft.Win32;
using OrisTpsWriter.Core;

namespace OrisTpsWriter
{
    public partial class MainWindow : Window
    {
        // ფილდის row (ListView-სთვის)
        public class FieldRow
        {
            public int Seq { get; set; }
            public string Name { get; set; }
            public string TypeName { get; set; }
            public int Length { get; set; }
        }

        public class KeyRow
        {
            public string KeyName { get; set; }
            public List<string> FieldNames { get; set; }
            public override string ToString() =>
                $"{KeyName}  ({string.Join(", ", FieldNames)})";
        }

        private readonly ObservableCollection<FieldRow> _fields = new();
        private readonly ObservableCollection<KeyRow> _keys = new();
        private DataTable _dataTable = new();

        public MainWindow()
        {
            InitializeComponent();
            LstFields.ItemsSource = _fields;
            LstKeys.ItemsSource = _keys;
            _fields.CollectionChanged += (s, e) => RefreshKeyFieldList();
            GridData.ItemsSource = _dataTable.DefaultView;
        }

        // -------------------------------------------------------------------
        // ფილდები
        // -------------------------------------------------------------------
        private void BtnAddField_Click(object sender, RoutedEventArgs e)
        {
            string name = (TxtFieldName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                Status("შეიყვანე ფილდის სახელი.", true);
                return;
            }
            if (_fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Status($"ფილდი '{name}' უკვე არსებობს.", true);
                return;
            }

            string typeName = ((ComboBoxItem)CmbFieldType.SelectedItem).Content.ToString();
            int length = TypeFixedLength(typeName);
            if (typeName == "STRING")
            {
                if (!int.TryParse(TxtFieldLen.Text, out length) || length < 1)
                {
                    Status("STRING ფილდს სჭირდება სწორი სიგრძე.", true);
                    return;
                }
            }

            _fields.Add(new FieldRow
            {
                Seq = _fields.Count,
                Name = name,
                TypeName = typeName,
                Length = length
            });

            TxtFieldName.Clear();
            RebuildDataColumns();
            Status($"ფილდი '{name}' დაემატა.");
        }

        private void BtnRemoveField_Click(object sender, RoutedEventArgs e)
        {
            if (LstFields.SelectedItem is FieldRow fr)
            {
                _fields.Remove(fr);
                // resequence
                for (int i = 0; i < _fields.Count; i++)
                    _fields[i].Seq = i;
                LstFields.Items.Refresh();
                RebuildDataColumns();
                Status($"ფილდი '{fr.Name}' წაიშალა.");
            }
            else Status("მონიშნე ფილდი წასაშლელად.", true);
        }

        private static int TypeFixedLength(string typeName) => typeName switch
        {
            "LONG" => 4,
            "ULONG" => 4,
            "SHORT" => 2,
            _ => 30
        };

        // -------------------------------------------------------------------
        // Keys
        // -------------------------------------------------------------------
        private void RefreshKeyFieldList()
        {
            LstKeyFields.ItemsSource = _fields.Select(f => new { f.Name }).ToList();
        }

        private void BtnAddKey_Click(object sender, RoutedEventArgs e)
        {
            string keyName = (TxtKeyName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(keyName))
            {
                Status("შეიყვანე key-ის სახელი.", true);
                return;
            }
            var selected = LstKeyFields.SelectedItems
                .Cast<object>()
                .Select(o => (string)o.GetType().GetProperty("Name").GetValue(o))
                .ToList();
            if (selected.Count == 0)
            {
                Status("მონიშნე key-ის მინიმუმ ერთი ფილდი.", true);
                return;
            }

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

        // -------------------------------------------------------------------
        // მონაცემთა ცხრილი
        // -------------------------------------------------------------------
        private void RebuildDataColumns()
        {
            // შევინახოთ არსებული მონაცემები
            var oldData = _dataTable;
            _dataTable = new DataTable();
            foreach (var f in _fields)
                _dataTable.Columns.Add(f.Name, typeof(string));

            // გადავიტანოთ ძველი მონაცემები სადაც შესაძლებელია
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

            // DataGrid columns
            GridData.Columns.Clear();
            foreach (var f in _fields)
            {
                GridData.Columns.Add(new DataGridTextColumn
                {
                    Header = f.Name,
                    Binding = new System.Windows.Data.Binding($"[{f.Name}]"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                });
            }
            GridData.ItemsSource = _dataTable.DefaultView;
        }

        private void BtnDemo_Click(object sender, RoutedEventArgs e)
        {
            // გავასუფთაოთ და ჩავტვირთოთ ARN-ის მსგავსი demo
            _fields.Clear();
            _keys.Clear();
            TxtTableName.Text = "UNNAMED";

            _fields.Add(new FieldRow { Seq = 0, Name = "ARN:KADR", TypeName = "STRING", Length = 50 });
            _fields.Add(new FieldRow { Seq = 1, Name = "ARN:SECT", TypeName = "STRING", Length = 20 });
            LstFields.Items.Refresh();
            RefreshKeyFieldList();

            _keys.Add(new KeyRow
            {
                KeyName = "ARN:K1",
                FieldNames = new List<string> { "ARN:SECT", "ARN:KADR" }
            });

            RebuildDataColumns();

            string[] names = {
                "ოქრიაშვილი გ. .", "გოგიჩაძე ა. .", "კაპანაძე ე. .",
                "კიკვაძე დ. .", "ახვლედიანი ზ. .", "ბერიძე ნ. .",
                "მჭედლიშვილი თ. ."
            };
            foreach (var n in names)
            {
                var row = _dataTable.NewRow();
                row["ARN:KADR"] = n;
                row["ARN:SECT"] = "";
                _dataTable.Rows.Add(row);
            }

            Status($"Demo ჩაიტვირთა: {names.Length} ქართული გვარი.");
        }

        private void BtnAddRow_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0)
            {
                Status("ჯერ დაამატე ფილდები.", true);
                return;
            }
            _dataTable.Rows.Add(_dataTable.NewRow());
        }

        private void BtnDelRow_Click(object sender, RoutedEventArgs e)
        {
            if (GridData.SelectedItem is DataRowView drv)
            {
                drv.Row.Delete();
                Status("რიგი წაიშალა.");
            }
            else Status("მონიშნე რიგი წასაშლელად.", true);
        }

        private void BtnImportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0)
            {
                Status("ჯერ დაამატე ფილდები.", true);
                return;
            }
            var dlg = new OpenFileDialog
            {
                Filter = "CSV ფაილები (*.csv)|*.csv|ყველა ფაილი (*.*)|*.*",
                Title = "აირჩიე CSV ფაილი"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8);
                int imported = 0;
                bool first = true;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    // header row skip heuristic: თუ პირველი ველი ემთხვევა ფილდის სახელს
                    if (first)
                    {
                        first = false;
                        if (parts.Length > 0 &&
                            _fields.Any(f => f.Name.Equals(parts[0].Trim(), StringComparison.OrdinalIgnoreCase)))
                            continue; // header
                    }
                    var row = _dataTable.NewRow();
                    for (int i = 0; i < _fields.Count && i < parts.Length; i++)
                        row[_fields[i].Name] = parts[i].Trim();
                    _dataTable.Rows.Add(row);
                    imported++;
                }
                Status($"იმპორტირდა {imported} ჩანაწერი.");
            }
            catch (Exception ex)
            {
                Status($"CSV შეცდომა: {ex.Message}", true);
            }
        }

        private void BtnImportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0)
            {
                Status("ჯერ დაამატე ფილდები.", true);
                return;
            }
            var dlg = new OpenFileDialog
            {
                Filter = "Excel ფაილები (*.xlsx;*.xls)|*.xlsx;*.xls|ყველა ფაილი (*.*)|*.*",
                Title = "აირჩიე Excel ფაილი"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                using var wb = new XLWorkbook(dlg.FileName);
                var ws = wb.Worksheets.First();
                var usedRange = ws.RangeUsed();
                if (usedRange == null) { Status("Excel ცხრილი ცარიელია.", true); return; }

                int firstRow = usedRange.FirstRow().RowNumber();
                int lastRow  = usedRange.LastRow().RowNumber();

                var firstCellVal = ws.Cell(firstRow, 1).GetString().Trim();
                if (_fields.Any(f => f.Name.Equals(firstCellVal, StringComparison.OrdinalIgnoreCase)))
                    firstRow++;

                int imported = 0;
                for (int r = firstRow; r <= lastRow; r++)
                {
                    var row = _dataTable.NewRow();
                    for (int i = 0; i < _fields.Count; i++)
                        row[_fields[i].Name] = ws.Cell(r, i + 1).GetString().Trim();
                    _dataTable.Rows.Add(row);
                    imported++;
                }
                Status($"Excel-იდან იმპორტირდა {imported} ჩანაწერი.");
            }
            catch (Exception ex)
            {
                Status($"Excel შეცდომა: {ex.Message}", true);
            }
        }

        // -------------------------------------------------------------------
        // Build & Save
        // -------------------------------------------------------------------
        private OrisTable BuildTable()
        {
            var table = new OrisTable((TxtTableName.Text ?? "UNNAMED").Trim());

            foreach (var f in _fields)
            {
                IOrisField field = f.TypeName switch
                {
                    "LONG" => new LongField(f.Name),
                    "ULONG" => new ULongField(f.Name),
                    "SHORT" => new ShortField(f.Name),
                    _ => new StringField(f.Name, f.Length),
                };
                table.AddField(field);
            }

            foreach (var k in _keys)
                table.AddKey(k.KeyName, k.FieldNames.ToArray());

            // default key თუ არცერთი არ არის
            if (_keys.Count == 0 && _fields.Count > 0)
                table.AddKey(TxtTableName.Text.Trim() + ":K1", new[] { _fields[0].Name });

            // commit any pending grid edits
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
            int recLen = _fields.Sum(f => f.Length);
            sb.AppendLine($"Record length: {recLen} bytes");
            sb.AppendLine($"ფილდები: {_fields.Count}");
            foreach (var f in _fields)
                sb.AppendLine($"   • {f.Name}  ({f.TypeName}, {f.Length})");
            sb.AppendLine($"Keys: {_keys.Count}");
            foreach (var k in _keys)
                sb.AppendLine($"   • {k}");
            sb.AppendLine($"ჩანაწერები: {_dataTable.Rows.Count}");
            MessageBox.Show(sb.ToString(), "სტრუქტურის გადახედვა",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_fields.Count == 0)
            {
                Status("ჯერ დაამატე ფილდები.", true);
                return;
            }
            var dlg = new SaveFileDialog
            {
                Filter = "TPS ფაილები (*.tps)|*.tps|ყველა ფაილი (*.*)|*.*",
                FileName = (TxtTableName.Text ?? "table").Trim() + ".tps",
                Title = ".tps ფაილის შენახვა"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var table = BuildTable();
                int bytes = table.Save(dlg.FileName);
                Status($"შენახულია: {Path.GetFileName(dlg.FileName)} " +
                       $"({bytes:N0} bytes, {table.RecordCount} ჩანაწერი)");
                MessageBox.Show(
                    $"ფაილი წარმატებით შეიქმნა!\n\n" +
                    $"გზა: {dlg.FileName}\n" +
                    $"ზომა: {bytes:N0} bytes\n" +
                    $"ჩანაწერები: {table.RecordCount}\n" +
                    $"ფილდები: {table.FieldCount}\n\n" +
                    $"შეგიძლია გახსნა Clarion Viewer-ით ან ORIS-ით.",
                    "წარმატება", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Status($"შენახვის შეცდომა: {ex.Message}", true);
                MessageBox.Show(ex.ToString(), "შეცდომა",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // -------------------------------------------------------------------
        private void Status(string msg, bool error = false)
        {
            TxtStatus.Text = msg;
            TxtStatus.Foreground = error
                ? (System.Windows.Media.Brush)FindResource("Danger")
                : (System.Windows.Media.Brush)FindResource("Muted");
        }
    }
}
