namespace Cheeseburger.DbStudio;

internal sealed class MainForm : Form
{
    private readonly List<SourceDocument> _sources = new();
    private readonly HashSet<string> _hiddenSongs = new(StringComparer.OrdinalIgnoreCase);
    private List<DbSong> _merged = new();

    private readonly ListBox _sourceList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search title / artist / pack..." };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
    };
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 9F),
        WordWrap = false,
    };
    private readonly PictureBox _jacket = new()
    {
        Dock = DockStyle.Top,
        Height = 180,
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Label _jacketState = new()
    {
        Dock = DockStyle.Top,
        Height = 36,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "Jacket resolver not configured yet",
    };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public MainForm()
    {
        Text = "Cheeseburger DB Studio - practical prototype";
        Width = 1450;
        Height = 850;
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;

        BuildColumns();
        BuildLayout();
        WireEvents();
        RebuildMerged();
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "J", Width = 32, Name = "Jacket" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Title", Width = 220, Name = "Title" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Artist", Width = 220, Name = "Artist" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pack", Width = 145, Name = "Pack" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ver", Width = 65, Name = "Version" });
        foreach (var diff in new[] { "PST", "PRS", "FTR", "ETR", "BYD", "INS" })
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = diff, Width = 105, Name = diff });
        }
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sources", Width = 180, Name = "Sources" });
    }

    private void BuildLayout()
    {
        var strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        strip.Items.Add(Button("Open JSON...", (_, _) => OpenFiles()));
        strip.Items.Add(Button("Rebuild / Merge All", (_, _) => RebuildMerged()));
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(Button("Detach Source", (_, _) => DetachSource()));
        strip.Items.Add(Button("Detach Entry", (_, _) => DetachEntry()));
        strip.Items.Add(Button("Hide Song", (_, _) => HideSong()));
        strip.Items.Add(Button("Restore Detached", (_, _) => RestoreDetached()));
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(Button("Export Merged...", (_, _) => ExportMerged()));

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        left.Controls.Add(_sourceList);
        left.Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Text = "Loaded source files", TextAlign = ContentAlignment.MiddleLeft });

        var rightBottom = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 300,
            FixedPanel = FixedPanel.Panel1,
        };
        var jacketPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        jacketPanel.Controls.Add(_details);
        jacketPanel.Controls.Add(_jacketState);
        jacketPanel.Controls.Add(_jacket);
        rightBottom.Panel1.Controls.Add(jacketPanel);
        rightBottom.Panel2.Controls.Add(_details);

        // A dedicated details box belongs on the right; keep jacket panel compact.
        jacketPanel.Controls.Remove(_details);
        rightBottom.Panel2.Controls.Clear();
        rightBottom.Panel2.Controls.Add(_details);

        var right = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 525,
        };
        var gridPanel = new Panel { Dock = DockStyle.Fill };
        gridPanel.Controls.Add(_grid);
        gridPanel.Controls.Add(_search);
        right.Panel1.Controls.Add(gridPanel);
        right.Panel2.Controls.Add(rightBottom);

        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 280,
            FixedPanel = FixedPanel.Panel1,
        };
        root.Panel1.Controls.Add(left);
        root.Panel2.Controls.Add(right);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        Controls.Add(root);
        Controls.Add(statusStrip);
        Controls.Add(strip);
    }

    private static ToolStripButton Button(string text, EventHandler click)
    {
        var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += click;
        return button;
    }

    private void WireEvents()
    {
        _search.TextChanged += (_, _) => RefreshGrid();
        _grid.SelectionChanged += (_, _) => ShowSelectedSong();
        _grid.CellDoubleClick += (_, _) => ShowSelectedSong();
    }

    private void OpenFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = true,
            Title = "Open songlist / fetched wiki / merged database JSON",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        foreach (var file in dialog.FileNames)
        {
            try
            {
                var doc = JsonAdapters.Load(file);
                _sources.Add(doc);
            }
            catch (Exception ex)
            {
                var answer = MessageBox.Show(this,
                    $"Could not read:\n{file}\n\n{ex.Message}\n\nSkip this file and continue?",
                    "Corrupt or unsupported file",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer == DialogResult.No) break;
            }
        }
        RefreshSources();
        RebuildMerged();
    }

    private void RefreshSources()
    {
        var selected = _sourceList.SelectedIndex;
        _sourceList.BeginUpdate();
        _sourceList.Items.Clear();
        foreach (var source in _sources) _sourceList.Items.Add(source);
        _sourceList.EndUpdate();
        if (_sourceList.Items.Count > 0) _sourceList.SelectedIndex = Math.Clamp(selected, 0, _sourceList.Items.Count - 1);
    }

    private void RebuildMerged()
    {
        _merged = JsonAdapters.Merge(_sources, _hiddenSongs);
        RefreshGrid();
        _status.Text = $"{_sources.Count} source file(s)   |   {_merged.Count} merged songs";
    }

    private void RefreshGrid()
    {
        var q = DbSong.Normalize(_search.Text);
        var visible = string.IsNullOrWhiteSpace(q)
            ? _merged
            : _merged.Where(s => DbSong.Normalize($"{s.Title} {s.Artist} {s.Pack} {s.Id}").Contains(q)).ToList();

        _grid.Rows.Clear();
        foreach (var song in visible)
        {
            var values = new List<object?>
            {
                string.IsNullOrWhiteSpace(song.Artwork) ? "" : "●",
                song.Title,
                song.Artist,
                song.Pack,
                song.AddedVersion,
            };
            foreach (var diff in new[] { "PST", "PRS", "FTR", "ETR", "BYD", "INS" })
            {
                values.Add(song.Charts.TryGetValue(diff, out var chart) ? chart.Compact() : "-");
            }
            values.Add(string.Join(", ", song.Sources.OrderBy(x => x)));
            var rowIndex = _grid.Rows.Add(values.ToArray());
            _grid.Rows[rowIndex].Tag = song;
        }
        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
        else ClearDetails();
    }

    private DbSong? SelectedSong()
        => _grid.CurrentRow?.Tag as DbSong;

    private void ShowSelectedSong()
    {
        var song = SelectedSong();
        if (song is null) { ClearDetails(); return; }
        _details.Text = song.DetailText();
        LoadJacket(song);
    }

    private void LoadJacket(DbSong song)
    {
        _jacket.Image?.Dispose();
        _jacket.Image = null;
        _jacketState.Text = "Jacket resolver not configured yet";

        if (string.IsNullOrWhiteSpace(song.Artwork)) return;
        try
        {
            if (File.Exists(song.Artwork))
            {
                using var source = Image.FromFile(song.Artwork);
                _jacket.Image = new Bitmap(source);
                _jacketState.Text = Path.GetFileName(song.Artwork);
            }
            else
            {
                _jacketState.Text = "Artwork metadata exists; file resolver pending";
            }
        }
        catch
        {
            _jacketState.Text = "Could not render jacket metadata";
        }
    }

    private void ClearDetails()
    {
        _details.Clear();
        _jacket.Image?.Dispose();
        _jacket.Image = null;
        _jacketState.Text = "Jacket resolver not configured yet";
    }

    private void DetachSource()
    {
        if (_sourceList.SelectedItem is not SourceDocument doc) return;
        if (MessageBox.Show(this, $"Detach source from this workspace?\n\n{doc.Name}\n\nThe original file is not deleted.",
            "Detach source", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _sources.Remove(doc);
        RefreshSources();
        RebuildMerged();
    }

    private void DetachEntry()
    {
        if (_sourceList.SelectedItem is not SourceDocument doc || SelectedSong() is not DbSong selected)
        {
            MessageBox.Show(this, "Select a source on the left and a song in the table first.", "Detach entry");
            return;
        }
        var candidate = doc.Songs.FirstOrDefault(s => SameSong(s, selected));
        if (candidate is null)
        {
            MessageBox.Show(this, "That source does not appear to contain the selected song.", "Detach entry");
            return;
        }
        doc.DetachedKeys.Add(candidate.DisplayKey);
        RebuildMerged();
    }

    private void HideSong()
    {
        if (SelectedSong() is not DbSong song) return;
        _hiddenSongs.Add(song.DisplayKey);
        RebuildMerged();
    }

    private void RestoreDetached()
    {
        _hiddenSongs.Clear();
        foreach (var source in _sources) source.DetachedKeys.Clear();
        RebuildMerged();
    }

    private static bool SameSong(DbSong a, DbSong b)
    {
        if (!string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(b.Id))
            return DbSong.Normalize(a.Id) == DbSong.Normalize(b.Id);
        return DbSong.Normalize(a.Title) == DbSong.Normalize(b.Title)
            && (string.IsNullOrWhiteSpace(a.Artist) || string.IsNullOrWhiteSpace(b.Artist)
                || DbSong.Normalize(a.Artist) == DbSong.Normalize(b.Artist));
    }

    private void ExportMerged()
    {
        if (_merged.Count == 0)
        {
            MessageBox.Show(this, "There is nothing to export yet.");
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = "cheeseburger-merged.json",
            Title = "Export normalized merged database",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            JsonAdapters.Export(dialog.FileName, _merged);
            _status.Text = $"Exported {_merged.Count} songs to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
