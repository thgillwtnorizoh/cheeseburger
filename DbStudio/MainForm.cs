namespace Cheeseburger.DbStudio;

internal sealed class MainForm : Form
{
    private readonly List<SourceDocument> _sources = new();
    private readonly HashSet<string> _hiddenSongs = new(StringComparer.OrdinalIgnoreCase);
    private readonly JacketResolver _jacketResolver = new();
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 120 };
    private List<DbSong> _merged = new();
    private bool _refreshingGrid;

    private readonly ListBox _sourceList = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = UiTheme.PanelAlt,
        ForeColor = UiTheme.Text,
        Font = new Font("Segoe UI", 9.25F),
    };

    private readonly TextBox _search = new()
    {
        Dock = DockStyle.Fill,
        PlaceholderText = "Search title / artist / pack / song ID...",
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = UiTheme.PanelAlt,
        ForeColor = UiTheme.Text,
        Font = new Font("Segoe UI", 10F),
    };

    private readonly SmoothDataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AllowUserToOrderColumns = true,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        ScrollBars = ScrollBars.Both,
        BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
        EnableHeadersVisualStyles = false,
        BackgroundColor = UiTheme.Window,
        GridColor = UiTheme.Border,
        ForeColor = UiTheme.Text,
        Font = new Font("Segoe UI", 9.25F),
    };

    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        BorderStyle = BorderStyle.None,
        BackColor = UiTheme.Panel,
        ForeColor = UiTheme.Text,
        Font = new Font("Cascadia Mono", 9F),
        WordWrap = false,
    };

    private readonly PictureBox _jacket = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(12, 14, 16),
    };

    private readonly Label _jacketState = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "Choose a jacket folder to resolve by song ID",
        BackColor = UiTheme.Panel,
        ForeColor = UiTheme.Muted,
        Font = new Font("Segoe UI", 8.5F),
        AutoEllipsis = true,
    };

    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0),
        BackColor = UiTheme.Panel,
        ForeColor = UiTheme.Muted,
        Font = new Font("Segoe UI", 9F),
    };

    public MainForm()
    {
        Text = "Cheeseburger DB Studio";
        Width = 1450;
        Height = 850;
        MinimumSize = new Size(1024, 650);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.Window;
        ForeColor = UiTheme.Text;
        Font = new Font("Segoe UI", 9.25F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResizeRedraw = true;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BuildColumns();
        BuildLayout();
        WireEvents();
        RebuildMerged();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.TryEnableDarkTitleBar(this);
    }

    private void BuildColumns()
    {
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Header,
            ForeColor = UiTheme.Text,
            SelectionBackColor = UiTheme.Header,
            SelectionForeColor = UiTheme.Text,
            Font = new Font("Segoe UI Semibold", 9F),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 2, 0),
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Panel,
            ForeColor = UiTheme.Text,
            SelectionBackColor = UiTheme.Selection,
            SelectionForeColor = Color.White,
            Padding = new Padding(4, 0, 2, 0),
        };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.PanelAlt,
            ForeColor = UiTheme.Text,
            SelectionBackColor = UiTheme.Selection,
            SelectionForeColor = Color.White,
        };
        _grid.RowTemplate.Height = 29;
        _grid.ColumnHeadersHeight = 32;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "J",
            Width = 36,
            MinimumWidth = 36,
            Name = "Jacket",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter },
        });
        AddFillColumn("Title", "Title", 18, 110);
        AddFillColumn("Artist", "Artist", 17, 110);
        AddFillColumn("Pack", "Pack", 11, 70);
        AddFillColumn("Ver", "Version", 6, 45);
        foreach (var diff in new[] { "PST", "PRS", "FTR", "ETR", "BYD", "INS" })
            AddFillColumn(diff, diff, 8, 52);
        AddFillColumn("Sources", "Sources", 12, 80);
    }

    private void AddFillColumn(string header, string name, float fillWeight, int minimumWidth)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Name = name,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            MinimumWidth = minimumWidth,
        });
    }

    private void BuildLayout()
    {
        SuspendLayout();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(8, 8, 8, 7),
            BackColor = UiTheme.Panel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
        };
        toolbar.Controls.Add(UiTheme.MakeButton("Open JSON/Songlist...", (_, _) => OpenFiles()));
        toolbar.Controls.Add(UiTheme.MakeButton("Choose Jacket Folder...", (_, _) => ChooseJacketFolder()));
        toolbar.Controls.Add(Separator());
        toolbar.Controls.Add(UiTheme.MakeButton("Rebuild / Merge All", (_, _) => RebuildMerged()));
        toolbar.Controls.Add(Separator());
        toolbar.Controls.Add(UiTheme.MakeButton("Detach Source", (_, _) => DetachSource()));
        toolbar.Controls.Add(UiTheme.MakeButton("Detach Entry", (_, _) => DetachEntry()));
        toolbar.Controls.Add(UiTheme.MakeButton("Hide Song", (_, _) => HideSong()));
        toolbar.Controls.Add(UiTheme.MakeButton("Restore Detached", (_, _) => RestoreDetached()));
        toolbar.Controls.Add(Separator());
        toolbar.Controls.Add(UiTheme.MakeButton("Export Merged...", (_, _) => ExportMerged()));

        var sourceHeader = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Loaded source files",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Muted,
            BackColor = UiTheme.Panel,
            Font = new Font("Segoe UI Semibold", 9F),
        };
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Panel,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 2,
        };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(sourceHeader, 0, 0);
        left.Controls.Add(_sourceList, 0, 1);

        var searchHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Window,
            Padding = new Padding(8, 7, 8, 7),
        };
        searchHost.Controls.Add(_search);

        var gridArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Window,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        gridArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        gridArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gridArea.Controls.Add(searchHost, 0, 0);
        gridArea.Controls.Add(_grid, 0, 1);

        var jacketArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Panel,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 2,
        };
        jacketArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        jacketArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        jacketArea.Controls.Add(_jacket, 0, 0);
        jacketArea.Controls.Add(_jacketState, 0, 1);

        var detailHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Panel,
            Padding = new Padding(12, 10, 10, 10),
        };
        detailHost.Controls.Add(_details);

        var detailsArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        detailsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 255));
        detailsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailsArea.Controls.Add(jacketArea, 0, 0);
        detailsArea.Controls.Add(detailHost, 1, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Window,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        right.Controls.Add(gridArea, 0, 0);
        right.Controls.Add(detailsArea, 0, 1);

        // Do not set large Panel1MinSize/Panel2MinSize values here. During form
        // construction SplitContainer still has its tiny design-time size, and
        // WinForms can throw before the window is ever shown if the requested
        // minimums cannot fit. The Form.MinimumSize already protects usability.
        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 220,
            SplitterWidth = 5,
            FixedPanel = FixedPanel.Panel1,
            BackColor = UiTheme.Border,
        };
        root.Panel1.BackColor = UiTheme.Panel;
        root.Panel2.BackColor = UiTheme.Window;
        root.Panel1.Controls.Add(left);
        root.Panel2.Controls.Add(right);

        var statusBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 29,
            BackColor = UiTheme.Panel,
            Padding = new Padding(0),
        };
        statusBar.Controls.Add(_status);

        Controls.Add(root);
        Controls.Add(statusBar);
        Controls.Add(toolbar);
        ResumeLayout(true);
    }

    private static Control Separator() => new Panel
    {
        Width = 1,
        Height = 24,
        BackColor = UiTheme.Border,
        Margin = new Padding(4, 3, 10, 3),
    };

    private void WireEvents()
    {
        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RefreshGrid();
        };
        _grid.SelectionChanged += (_, _) =>
        {
            if (!_refreshingGrid) ShowSelectedSong();
        };
        _grid.CellDoubleClick += (_, _) => ShowSelectedSong();
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            e.ToolTipText = Convert.ToString(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? string.Empty;
        };
    }

    private void OpenFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "JSON / songlist files (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = true,
            Title = "Open JSON / songlist / fetched wiki / merged database",
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

    private void ChooseJacketFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the master jacket folder. Supported layouts: <songid>.png/.jpg OR <songid>/base.png/.jpg OR dl_<songid>/base.png/.jpg",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = _jacketResolver.RootFolder ?? string.Empty,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            UseWaitCursor = true;
            _jacketResolver.Configure(dialog.SelectedPath);
            RefreshGrid();
            ShowSelectedSong();
            UpdateStatus($"Jackets: {_jacketResolver.Count} indexed from {_jacketResolver.RootFolder}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not index jacket folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
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
        UpdateStatus();
    }

    private void UpdateStatus(string? extra = null)
    {
        _status.Text = $"{_sources.Count} source file(s)   |   {_merged.Count} merged songs"
            + (_jacketResolver.IsConfigured ? $"   |   {_jacketResolver.Count} jackets" : string.Empty)
            + (string.IsNullOrWhiteSpace(extra) ? string.Empty : $"   |   {extra}");
    }

    private void RefreshGrid()
    {
        var selectedKey = SelectedSong()?.DisplayKey;
        var q = DbSong.Normalize(_search.Text);
        var visible = string.IsNullOrWhiteSpace(q)
            ? _merged
            : _merged.Where(s => DbSong.Normalize($"{s.Title} {s.Artist} {s.Pack} {s.Id}").Contains(q)).ToList();

        _refreshingGrid = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        DataGridViewRow? rowToSelect = null;
        foreach (var song in visible)
        {
            var jacketPath = _jacketResolver.Resolve(song.Id);
            var values = new List<object?>
            {
                jacketPath is null ? "" : "●",
                song.Title,
                song.Artist,
                song.Pack,
                song.AddedVersion,
            };
            foreach (var diff in new[] { "PST", "PRS", "FTR", "ETR", "BYD", "INS" })
                values.Add(song.Charts.TryGetValue(diff, out var chart) ? chart.Compact() : "-");
            values.Add(string.Join(", ", song.Sources.OrderBy(x => x)));

            var rowIndex = _grid.Rows.Add(values.ToArray());
            var row = _grid.Rows[rowIndex];
            row.Tag = song;
            if (selectedKey is not null && string.Equals(song.DisplayKey, selectedKey, StringComparison.OrdinalIgnoreCase))
                rowToSelect = row;
        }
        _grid.ResumeLayout();
        _refreshingGrid = false;

        if (rowToSelect is not null)
        {
            rowToSelect.Selected = true;
            _grid.CurrentCell = rowToSelect.Cells[Math.Min(1, rowToSelect.Cells.Count - 1)];
        }
        else if (_grid.Rows.Count > 0)
        {
            _grid.Rows[0].Selected = true;
            _grid.CurrentCell = _grid.Rows[0].Cells[Math.Min(1, _grid.Rows[0].Cells.Count - 1)];
        }
        else
        {
            ClearDetails();
            return;
        }
        ShowSelectedSong();
    }

    private DbSong? SelectedSong() => _grid.CurrentRow?.Tag as DbSong;

    private void ShowSelectedSong()
    {
        var song = SelectedSong();
        if (song is null)
        {
            ClearDetails();
            return;
        }
        _details.Text = song.DetailText();
        LoadJacket(song);
    }

    private void LoadJacket(DbSong song)
    {
        _jacket.Image?.Dispose();
        _jacket.Image = null;

        if (!_jacketResolver.IsConfigured)
        {
            _jacketState.Text = "Choose a jacket folder to resolve by song ID";
            return;
        }
        if (string.IsNullOrWhiteSpace(song.Id))
        {
            _jacketState.Text = "No song ID; cannot resolve jacket";
            return;
        }

        var path = _jacketResolver.Resolve(song.Id);
        if (path is null)
        {
            _jacketState.Text = $"No jacket found for {song.Id}";
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var source = Image.FromStream(stream);
            _jacket.Image = new Bitmap(source);
            _jacketState.Text = Path.GetRelativePath(_jacketResolver.RootFolder!, path);
        }
        catch (Exception ex)
        {
            _jacketState.Text = $"Could not render {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private void ClearDetails()
    {
        _details.Clear();
        _jacket.Image?.Dispose();
        _jacket.Image = null;
        _jacketState.Text = _jacketResolver.IsConfigured
            ? "Select a song"
            : "Choose a jacket folder to resolve by song ID";
    }

    private void DetachSource()
    {
        if (_sourceList.SelectedItem is not SourceDocument doc) return;
        if (MessageBox.Show(this,
            $"Detach source from this workspace?\n\n{doc.Name}\n\nThe original file is not deleted.",
            "Detach source",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes) return;
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
            UpdateStatus($"Exported {_merged.Count} songs to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
