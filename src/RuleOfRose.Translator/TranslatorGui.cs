namespace RuleOfRose.Translator;

// Editor terjemahan .ARA: toolbar atas, panel Entries di kiri, editor
// Source/Translation di kanan (stacked), status strip bawah. Translation
// boleh lebih panjang dari source; #n = newline. Load/scan/import jalan di
// background thread supaya UI tidak freeze.
public sealed class TranslatorGui : Form
{
    private sealed class Row(TextEntryRecord record)
    {
        public TextEntryRecord Record { get; } = record;
        public string Translation { get; set; } = record.Source;
    }

    private readonly ListBox _entries = new();
    private readonly TextBox _search = new();
    private readonly TextBox _sourceBox = new();
    private readonly TextBox _translationBox = new();
    private readonly CheckBox _untranslatedOnly = new();
    private readonly CheckBox _wrap = new() { Text = "Wrap", Checked = true };
    private readonly Button _openAra = new();
    private readonly Button _openJson = new();
    private readonly Button _saveJson = new();
    private readonly Button _buildAra = new();
    private readonly Button _prev = new();
    private readonly Button _next = new();
    private readonly Button _nextUntranslated = new();
    private readonly Label _entriesHeader = new();
    private readonly Label _sourceHeader = new();
    private readonly Label _translationHeader = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusFile = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusCounts = new();
    private readonly ToolStripStatusLabel _statusDirty = new() { ForeColor = Color.FromArgb(0xB3, 0x55, 0x0F) };
    private readonly ToolTip _tip = new();
    private readonly SplitContainer _contentSplit = new();
    private Panel? _toolbar;

    private readonly List<Row> _allRows = new();
    private readonly List<Row> _view = new();
    private AraArchive? _ara;
    private string? _araPath;
    private string? _jsonPath;
    private bool _dirty;
    private bool _suspend;
    private bool _busy;
    private int _loadVersion;

    public TranslatorGui(string[] args)
    {
        // Dialog file butuh thread STA; gagal cepat dengan pesan jelas bila tidak.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException(
                "TranslatorGui harus dibuat di thread STA (lihat Program.Main [STAThread]).");

        Text = "Rule of Rose Translator";
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(940, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0xF0, 0xF1, 0xF3);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        // Tanpa AutoScaleDimensions, AutoScaleMode.Dpi tidak melakukan apa pun
        // dan layout manual tidak ikut membesar di monitor HiDPI.
        AutoScaleDimensions = new SizeF(96F, 96F);

        BuildEntryList();
        BuildEditors();   // contentSplit = Controls[0] so Fill docks last.
        BuildSidebar();
        BuildToolbar();
        BuildStatusBar();

        KeyDown += OnGlobalKeyDown;
        FormClosing += (_, _) => { FlushCurrent(); AutoSaveJsonOnClose(); };

        // argv opsional: RoRTranslator <path.ARA> langsung memuat file itu.
        // Default: aplikasi start kosong; Open .ARA = filedialog polos.
        if (args.Length > 0 && File.Exists(args[0])
            && args[0].EndsWith(".ARA", StringComparison.OrdinalIgnoreCase))
        {
            Shown += (_, _) => _ = OpenAraAsync(args[0]);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySplitterDistances();
    }

    // SplitterDistance yang di-set saat konstruksi di-clamp terhadap ukuran
    // default SplitContainer; re-apply setelah bounds nyata ada.
    private void ApplySplitterDistances()
    {
        try
        {
            int max = _contentSplit.Height - _contentSplit.SplitterWidth - _contentSplit.Panel2MinSize;
            if (max >= _contentSplit.Panel1MinSize)
                _contentSplit.SplitterDistance = Math.Clamp(260, _contentSplit.Panel1MinSize, max);
        }
        catch { /* layout best-effort */ }
    }

    // ---------------------------------------------------------------- layout

    private void BuildEntryList()
    {
        _entries.Dock = DockStyle.Fill;
        _entries.Font = new Font("Segoe UI", 9F);
        _entries.IntegralHeight = false;
        _entries.BorderStyle = BorderStyle.FixedSingle;
        _entries.DrawMode = DrawMode.OwnerDrawFixed;
        _entries.ItemHeight = 22;
        _entries.DrawItem += DrawEntryItem;
        _entries.SelectedIndexChanged += (_, _) => LoadSelectedEntry();

        _entriesHeader.Text = "Entries";
        _entriesHeader.Dock = DockStyle.Top;
        _entriesHeader.Height = 24;
        _entriesHeader.TextAlign = ContentAlignment.MiddleLeft;
        _entriesHeader.Padding = new Padding(4, 0, 0, 0);
        _entriesHeader.Font = new Font("Segoe UI", 8.25F, FontStyle.Bold);
        _entriesHeader.ForeColor = Color.FromArgb(0x66, 0x6A, 0x70);
    }

    private void BuildSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 280, Padding = new Padding(8, 0, 8, 8) };
        panel.Controls.Add(_entries);
        panel.Controls.Add(_entriesHeader);
        Controls.Add(panel);
    }

    private void BuildEditors()
    {
        StyleEditor(_sourceBox, readOnly: true);
        StyleEditor(_translationBox, readOnly: false);
        _translationBox.TextChanged += (_, _) => OnTranslationChanged();

        _sourceHeader.Text = "Source";
        _translationHeader.Text = "Translation";
        foreach (var header in new[] { _sourceHeader, _translationHeader })
        {
            header.Dock = DockStyle.Top;
            header.Height = 24;
            header.TextAlign = ContentAlignment.MiddleLeft;
            header.Padding = new Padding(4, 0, 0, 0);
            header.Font = new Font("Segoe UI", 8.25F, FontStyle.Bold);
            header.ForeColor = Color.FromArgb(0x66, 0x6A, 0x70);
        }

        var sourcePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
        sourcePanel.Controls.Add(_sourceBox);
        sourcePanel.Controls.Add(_sourceHeader);

        var translationPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 8) };
        translationPanel.Controls.Add(_translationBox);
        translationPanel.Controls.Add(_translationHeader);

        _contentSplit.Dock = DockStyle.Fill;
        _contentSplit.Orientation = Orientation.Horizontal;
        _contentSplit.SplitterWidth = 6;
        _contentSplit.FixedPanel = FixedPanel.Panel1;
        _contentSplit.BackColor = BackColor;
        _contentSplit.Panel1MinSize = 100;
        _contentSplit.Panel2MinSize = 140;
        _contentSplit.Panel1.Controls.Add(sourcePanel);
        _contentSplit.Panel2.Controls.Add(translationPanel);

        Controls.Add(_contentSplit);
    }

    private void BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.White };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(0xE1, 0xE4, 0xE8));
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        StyleButton(_openAra, "Open .ARA");
        StyleButton(_openJson, "Open JSON");
        StyleButton(_saveJson, "Save JSON  Ctrl+S");
        StyleButton(_buildAra, "Build .ARA");
        StyleButton(_prev, "▲");
        StyleButton(_next, "▼");
        StyleButton(_nextUntranslated, "Next untranslated  F4");
        _prev.Size = _next.Size = new Size(34, 28);
        _tip.SetToolTip(_prev, "Entry sebelumnya  (Alt+Up)");
        _tip.SetToolTip(_next, "Entry berikutnya  (Alt+Down)");
        _tip.SetToolTip(_openAra, "Buka .ARA asli game (versi PS2)");
        _tip.SetToolTip(_openJson, "Muat terjemahan dari JSON ke scan saat ini");
        _tip.SetToolTip(_saveJson, "Simpan JSON terjemahan");
        _tip.SetToolTip(_buildAra, "Bangun .ARA terpatch dari arsip asli — translation boleh lebih panjang");

        _openAra.Click += (_, _) => PickAra();
        _openJson.Click += (_, _) => _ = OpenJsonAsync();
        _saveJson.Click += (_, _) => _ = SaveJsonAsync();
        _buildAra.Click += (_, _) => _ = BuildAraAsync();
        _prev.Click += (_, _) => MoveSelection(-1);
        _next.Click += (_, _) => MoveSelection(1);
        _nextUntranslated.Click += (_, _) => JumpToNextUntranslated();

        _search.PlaceholderText = "Filter source atau translation...   Ctrl+F";
        _search.Font = new Font("Segoe UI", 9F);
        _search.Size = new Size(300, 25);
        _search.TextChanged += (_, _) => RefreshEntries();

        _untranslatedOnly.Text = "Untranslated only";
        _untranslatedOnly.Font = new Font("Segoe UI", 8.5F);
        _untranslatedOnly.AutoSize = true;
        _untranslatedOnly.CheckedChanged += (_, _) => RefreshEntries();

        _wrap.Text = "Wrap";
        _wrap.Font = new Font("Segoe UI", 8.5F);
        _wrap.AutoSize = true;
        _wrap.CheckedChanged += (_, _) =>
        {
            var scroll = _wrap.Checked ? ScrollBars.Vertical : ScrollBars.Both;
            _sourceBox.WordWrap = _translationBox.WordWrap = _wrap.Checked;
            _sourceBox.ScrollBars = _translationBox.ScrollBars = scroll;
        };

        bar.Controls.Add(_openAra);
        bar.Controls.Add(_openJson);
        bar.Controls.Add(_saveJson);
        bar.Controls.Add(_buildAra);
        bar.Controls.Add(_prev);
        bar.Controls.Add(_next);
        bar.Controls.Add(_nextUntranslated);
        bar.Controls.Add(_search);
        bar.Controls.Add(_untranslatedOnly);
        bar.Controls.Add(_wrap);

        int x = 8;
        foreach (var b in new[] { _openAra, _openJson, _saveJson, _buildAra })
        {
            b.Location = new Point(x, 9);
            x += b.Width + 6;
        }
        x += 8;
        foreach (var b in new[] { _prev, _next, _nextUntranslated })
        {
            b.Location = new Point(x, 9);
            x += b.Width + 6;
        }

        bar.Resize += (_, _) =>
        {
            int right = bar.Width - 12;
            _wrap.Location = new Point(right - _wrap.Width, 13);
            _untranslatedOnly.Location = new Point(_wrap.Left - _untranslatedOnly.Width - 14, 13);
            _search.Location = new Point(_untranslatedOnly.Left - _search.Width - 14, 10);
        };

        _toolbar = bar;
        Controls.Add(bar);
    }

    private void BuildStatusBar()
    {
        _statusStrip.BackColor = Color.White;
        _statusStrip.SizingGrip = false;
        _statusStrip.Items.Add(_statusFile);
        _statusStrip.Items.Add(_statusCounts);
        _statusStrip.Items.Add(new ToolStripStatusLabel
        {
            Text = "   #n = newline · translation boleh lebih panjang",
            ForeColor = Color.FromArgb(0x8A, 0x8F, 0x98)
        });
        _statusStrip.Items.Add(_statusDirty);
        Controls.Add(_statusStrip);
    }

    private static void StyleButton(Button b, string text)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xEC, 0xEF, 0xF3);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xE0, 0xE4, 0xEA);
        b.BackColor = Color.White;
        b.ForeColor = Color.FromArgb(0x33, 0x37, 0x3D);
        b.Font = new Font("Segoe UI", 8.5F);
        b.Size = new Size(Math.Max(70, text.Length * 7 + 24), 28);
        b.Cursor = Cursors.Hand;
    }

    private static void StyleEditor(TextBox box, bool readOnly)
    {
        box.Multiline = true;
        box.ReadOnly = readOnly;
        box.ScrollBars = ScrollBars.Vertical;
        box.Dock = DockStyle.Fill;
        box.Font = new Font("Consolas", 10F);
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = readOnly ? Color.FromArgb(0xF7, 0xF8, 0xFA) : Color.White;
        box.ForeColor = Color.FromArgb(0x2A, 0x2E, 0x34);
        box.AcceptsTab = true;
    }

    // ------------------------------------------------------------------ data

    // Dialog file standar; harus jalan di UI thread STA (lihat Program.Main).
    private void PickAra()
    {
        if (_busy) return;
        if (!ConfirmDiscardChanges("membuka .ARA lain")) return;
        try
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Rule of Rose archive (*.ARA)|*.ARA",
                Title = "Open .ARA",
                RestoreDirectory = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) _ = OpenAraAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open .ARA gagal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task OpenAraAsync(string path)
    {
        if (_busy) return;
        int version = ++_loadVersion;
        EnterBusy();
        try
        {
            // Scan di background thread: file besar / modul aneh tidak
            // pernah membekukan UI.
            var (ara, scan) = await Task.Run(() =>
            {
                var a = RtpkArchive.Load(path);
                return (a, ModuleScanner.Scan(a.Module));
            });
            if (version != _loadVersion) return;

            FlushCurrent();
            _ara = ara;
            _araPath = path;
            // JSON milik arsip sebelumnya tidak boleh ikut terbawa: Ctrl+S
            // berikutnya akan menimpa file itu dengan isi arsip baru.
            _jsonPath = null;
            _allRows.Clear();
            // Urut menurut id (offset instruksi) agar baris tidak berpindah
            // setelah arsip di-rebuild; id-nya stabil, offset teksnya tidak.
            foreach (var e in scan.Entries.OrderBy(e => e.Id, StringComparer.Ordinal))
                _allRows.Add(new Row(e));
            _dirty = false;
            _search.Text = "";
            _untranslatedOnly.Checked = false;
            Text = $"Rule of Rose Translator — {Path.GetFileName(path)} ({_allRows.Count} entries)";
            RefreshEntries();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            MessageBox.Show(this, ex.Message, "Open .ARA gagal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (version == _loadVersion) LeaveBusy();
        }
    }

    // Semua tombol aksi ada di panel toolbar; simpan referensinya, jangan
    // menebak panel dari Height.
    private void SetToolbarEnabled(bool enabled)
    {
        if (_toolbar is not null) _toolbar.Enabled = enabled;
    }

    private void EnterBusy()
    {
        _busy = true;
        UseWaitCursor = true;
        SetToolbarEnabled(false);
    }

    private void LeaveBusy()
    {
        _busy = false;
        UseWaitCursor = false;
        SetToolbarEnabled(true);
    }

    // Membuka arsip/JSON lain membuang isi _allRows; jangan sampai perubahan
    // yang belum disimpan hilang tanpa peringatan.
    private bool ConfirmDiscardChanges(string action)
    {
        if (!_dirty) return true;
        var result = MessageBox.Show(this,
            "Terjemahan belum disimpan ke JSON.\n" +
            $"Lanjutkan {action} dan buang perubahan?",
            "Perubahan belum disimpan",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        return result == DialogResult.OK;
    }

    private async Task OpenJsonAsync()
    {
        if (_busy) return;
        if (_ara is not { } ara)
        {
            MessageBox.Show(this, "Buka .ARA asli dulu (Open .ARA).", "Open JSON",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!ConfirmDiscardChanges("memuat JSON lain")) return;
        string jsonPath;
        try
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Translation JSON (*.json)|*.json",
                Title = "Open JSON",
                RestoreDirectory = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            jsonPath = dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open JSON gagal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        int version = ++_loadVersion;
        EnterBusy();
        try
        {
            var doc = await Task.Run(() => TranslationJson.Read(jsonPath));
            if (version != _loadVersion) return;
            // v3 mencocokkan lewat id (offset instruksi) yang stabil antar-
            // rebuild, jadi JSON dari hasil rebuild sebelumnya tetap berlaku.
            // v2 memakai offset teks: hanya sah untuk arsip yang sama.
            bool stableIds = doc.Format == TranslationDocument.CurrentFormat;
            if (!stableIds && doc.SourceSha256.Length > 0 && doc.SourceSha256 != ara.Sha256)
                throw new InvalidDataException(
                    "JSON berasal dari .ARA lain (source_sha256 tidak cocok).");

            var byId = doc.Entries.ToDictionary(e => e.Id);
            int applied = 0, missing = 0;
            foreach (var row in _allRows)
            {
                if (!byId.TryGetValue(row.Record.Id, out var edit)) { missing++; continue; }
                if (!stableIds && edit.Source != row.Record.Source) { missing++; continue; }
                row.Translation = edit.Translation;
                applied++;
            }
            if (applied > 0)
            {
                _jsonPath = jsonPath;
                // Isi memori sama dengan file JSON yang baru dimuat; belum ada
                // perubahan pengguna yang perlu disimpan.
                _dirty = false;
            }
            RefreshEntries();
            _statusCounts.Text = $"  JSON loaded: {applied} applied"
                + (missing > 0 ? $", {missing} entry tidak ada di JSON (dilewati)" : "")
                + (applied == 0 ? " — tidak ada entry yang cocok" : "");
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            MessageBox.Show(this, ex.Message, "Open JSON gagal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (version == _loadVersion) LeaveBusy();
        }
    }

    private string DefaultJsonPath => _araPath is null
        ? ""
        : Path.Combine(Path.GetDirectoryName(_araPath)!,
            Path.GetFileNameWithoutExtension(_araPath) + ".text.json");

    private async Task SaveJsonAsync()
    {
        if (_busy) return;
        if (_ara is null || _araPath is null) return;
        FlushCurrent();
        string path = _jsonPath ?? "";
        if (path.Length == 0)
        {
            try
            {
                using var dialog = new SaveFileDialog
                {
                    Filter = "Translation JSON (*.json)|*.json",
                    DefaultExt = "json",
                    Title = "Save JSON",
                    RestoreDirectory = true,
                    FileName = Path.GetFileNameWithoutExtension(_araPath) + ".text.json"
                };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                path = dialog.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save JSON gagal",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        EnterBusy();
        try
        {
            var doc = BuildDocument();
            await Task.Run(() => TranslationJson.Write(path, doc));
            _jsonPath = path;
            _dirty = false;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save JSON gagal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            LeaveBusy();
        }
    }

    // Menutup dengan JSON belum tersimpan = save senyap ke path default
    // di sebelah .ARA.
    private void AutoSaveJsonOnClose()
    {
        if (!_dirty || _araPath is null) return;
        try
        {
            string path = _jsonPath ?? DefaultJsonPath;
            TranslationJson.Write(path, BuildDocument());
            _jsonPath = path;
            _dirty = false;
        }
        catch { /* close tidak boleh diblokir IO */ }
    }

    private TranslationDocument BuildDocument()
    {
        foreach (var row in _allRows) row.Record.Translation = row.Translation;
        return new TranslationDocument
        {
            Format = TranslationDocument.CurrentFormat,
            SourceSha256 = _ara!.Sha256,
            // Urut mengikuti id (offset instruksi) agar JSON hasil GUI sama
            // urutannya dengan export CLI di tiap rebuild.
            Entries = _allRows.Select(r => r.Record).OrderBy(r => r.Id, StringComparer.Ordinal).ToList()
        };
    }

    private async Task BuildAraAsync()
    {
        if (_busy) return;
        FlushCurrent();
        if (_ara is null || _araPath is null) return;
        string outputPath;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Rule of Rose archive (*.ARA)|*.ARA",
                DefaultExt = "ARA",
                Title = "Build .ARA",
                RestoreDirectory = true,
                FileName = Path.GetFileNameWithoutExtension(_araPath) + ".translated.ARA"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            outputPath = dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Build .ARA gagal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string araPath = _araPath;
        string temp = Path.Combine(Path.GetTempPath(), "ror-gui-" + Guid.NewGuid().ToString("N") + ".json");
        EnterBusy();
        try
        {
            var doc = BuildDocument();
            var result = await Task.Run(() =>
            {
                TranslationJson.Write(temp, doc);
                return TextPatcher.Import(araPath, temp, outputPath);
            });
            string msg = result.NoChanges
                ? "No changes: output identik dengan asli."
                : $"Changed {result.Changed} strings; added {result.AddedBytes} bytes;" +
                  $" output {result.OutputBytes} bytes.";
            MessageBox.Show(this, msg, "Build .ARA", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Build .ARA gagal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            LeaveBusy();
            try { File.Delete(temp); } catch { /* cleanup best-effort */ }
        }
    }

    private void RefreshEntries()
    {
        if (_suspend) return;
        Row? keep = SelectedRow();
        _suspend = true;

        string filter = _search.Text.Trim();
        _view.Clear();
        foreach (var row in _allRows)
        {
            // "Untranslated only" menyisakan entry yang belum diterjemahkan.
            if (_untranslatedOnly.Checked && row.Translation != row.Record.Source) continue;
            if (filter.Length > 0 &&
                row.Record.Source.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                row.Translation.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _view.Add(row);
        }

        _entries.BeginUpdate();
        _entries.Items.Clear();
        if (_view.Count > 0) _entries.Items.AddRange(_view.Cast<object>().ToArray());
        int index = keep is null ? -1 : _view.IndexOf(keep);
        _entries.SelectedIndex = _view.Count == 0 ? -1 : Math.Clamp(index < 0 ? 0 : index, 0, _view.Count - 1);
        _entries.EndUpdate();
        _suspend = false;

        LoadSelectedEntry();
        _entriesHeader.Text = _view.Count == _allRows.Count
            ? $"Entries  ({_allRows.Count})"
            : $"Entries  ({_view.Count} of {_allRows.Count})";
        UpdateStatus();
    }

    private Row? SelectedRow() => _entries.SelectedItem as Row;

    private void LoadSelectedEntry()
    {
        if (_suspend) return;
        if (SelectedRow() is not { } row)
        {
            // Filter bisa menyisakan nol entry: jangan biarkan teks entry lama
            // tetap tampil di editor.
            _suspend = true;
            _sourceBox.Text = "";
            _translationBox.Text = "";
            _sourceHeader.Text = "Source";
            _translationHeader.Text = "Translation";
            _translationHeader.ForeColor = Color.FromArgb(0x66, 0x6A, 0x70);
            _suspend = false;
            return;
        }
        _suspend = true;
        string refs = string.Join(" ", row.Record.References.Select(r => $"{r.Kind}@0x{r.InstructionOffset:X}"));
        _sourceBox.Text = row.Record.Source;
        _sourceHeader.Text =
            $"Source — {row.Record.Id} · module 0x{row.Record.ModuleOffset:X} · {row.Record.Source.Length} chars · {refs}";
        _translationBox.Text = row.Translation;
        _suspend = false;
        UpdateTranslationHeader();
    }

    private void OnTranslationChanged()
    {
        if (_suspend) return;
        if (SelectedRow() is not { } row) return;
        string data = _translationBox.Text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (data == row.Translation) return;
        row.Translation = data;
        _dirty = true;
        _entries.Invalidate();
        UpdateTranslationHeader();
        UpdateStatus();
    }

    private void UpdateTranslationHeader()
    {
        if (SelectedRow() is not { } row) return;
        int src = row.Record.Source.Length;
        int len = row.Translation.Length;
        string diff = len > src ? $"  (+{len - src}, lebih panjang)"
            : len < src ? $"  ({len - src})" : "  (sama)";
        string issue = TranslationIssue(row.Translation);
        _translationHeader.Text = $"Translation — {len} chars{diff}{(issue.Length == 0 ? "" : $"   ⚠ {issue}")}";
        _translationHeader.ForeColor = issue.Length == 0
            ? Color.FromArgb(0x66, 0x6A, 0x70)
            : Color.FromArgb(0xC0, 0x39, 0x2B);
    }

    // Import: byte >=0x20 saja (ASCII cetak + SJIS/penanda game), wajib
    // prefix #R/#F; #n = newline.
    private static string TranslationIssue(string translation)
        => translation.Length < 2 || translation[0] != '#'
            || (translation[1] != 'R' && translation[1] != 'F')
            ? "translation harus diawali #R atau #F"
            : translation.Any(c => c < 32)
                ? "karakter kontrol (<0x20) — gunakan #n untuk newline"
                : "";

    // ------------------------------------------------------------- navigation

    private void MoveSelection(int delta)
    {
        if (_view.Count == 0) return;
        SelectRow(Math.Clamp(_entries.SelectedIndex + delta, 0, _view.Count - 1));
    }

    private void JumpToNextUntranslated()
    {
        if (_view.Count == 0) return;
        int start = _entries.SelectedIndex;
        for (int step = 1; step <= _view.Count; step++)
        {
            int i = (start + step) % _view.Count;
            if (_view[i].Translation == _view[i].Record.Source)
            {
                SelectRow(i);
                return;
            }
        }
        _statusCounts.Text = "  semua entry visible sudah diterjemahkan";
    }

    private void SelectRow(int index)
    {
        _entries.SelectedIndex = index;
        _entries.TopIndex = Math.Max(0, index - 3);
        _translationBox.Focus();
        _translationBox.SelectionStart = _translationBox.Text.Length;
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S) { _ = SaveJsonAsync(); e.Handled = true; }
        else if (e.KeyCode == Keys.F4) { JumpToNextUntranslated(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Down) { MoveSelection(1); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Up) { MoveSelection(-1); e.Handled = true; }
    }

    private void FlushCurrent()
    {
        if (SelectedRow() is { } row)
        {
            string data = _translationBox.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (data != row.Translation)
            {
                row.Translation = data;
                _dirty = true;
            }
        }
    }

    private void UpdateStatus()
    {
        if (_ara is null || _araPath is null)
        {
            _statusFile.Text = "Buka .ARA asli untuk memulai (tombol Open .ARA)";
            _statusCounts.Text = "";
            _statusDirty.Text = "";
            return;
        }
        int translated = _allRows.Count(r => r.Translation != r.Record.Source);
        double pct = _allRows.Count == 0 ? 0 : translated * 100.0 / _allRows.Count;
        _statusCounts.Text = $"  {translated}/{_allRows.Count} translated ({pct:0.0}%)";
        _statusDirty.Text = _dirty ? "● unsaved" : "";
        _statusFile.Text = Path.GetFileName(_araPath);
    }

    // ---------------------------------------------------------------- drawing

    private void DrawEntryItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _view.Count) return;
        var row = _view[e.Index];
        bool translated = row.Translation != row.Record.Source;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.DrawBackground();

        using var markerBrush = new SolidBrush(selected ? SystemColors.HighlightText :
            translated ? Color.FromArgb(0x4C, 0x8C, 0x4A) : Color.FromArgb(0xB0, 0xB4, 0xBA));
        using var textBrush = new SolidBrush(selected ? SystemColors.HighlightText : Color.FromArgb(0x2A, 0x2E, 0x34));
        using var idBrush = new SolidBrush(selected ? SystemColors.HighlightText : Color.FromArgb(0x9A, 0x9F, 0xA6));

        e.Graphics.DrawString(translated ? "[+]" : "[ ]", _entries.Font, markerBrush,
            e.Bounds.X + 6, e.Bounds.Y + 2);

        string text = OneLine(row.Record.Source);
        var clip = new RectangleF(e.Bounds.X + 40, e.Bounds.Y, e.Bounds.Width - 40 - 70, e.Bounds.Height);
        e.Graphics.DrawString(text, _entries.Font, textBrush, clip);

        string id = row.Record.Id.Replace("text_", "");
        var idSize = e.Graphics.MeasureString(id, _entries.Font);
        e.Graphics.DrawString(id, _entries.Font, idBrush,
            e.Bounds.Right - idSize.Width - 8, e.Bounds.Y + 2);
        e.DrawFocusRectangle();
    }

    private static string OneLine(string text)
    {
        string flat = text.Replace("#n", " / ");
        return flat.Length > 160 ? flat[..160] + "..." : flat;
    }
}
