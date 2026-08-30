namespace Cheeseburger.DbStudio;

internal sealed class MainForm : Form
{
    private static readonly Color NormalVariantColor = ColorTranslator.FromHtml("#53365e");
    private static readonly Color BeyondVariantColor = ColorTranslator.FromHtml("#6d1b35");

    private readonly List<SourceDocument> _sources = new();
    private readonly HashSet<string> _hiddenSongs = new(StringComparer.OrdinalIgnoreCase);
    private readonly JacketResolver _jacketResolver = new();
    private readonly PreviewPlayer _previewPlayer = new();
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 120 };
    private List<DbSong> _merged = new();
    private bool _refreshingGrid;
    private string? _detailSongKey;
    private string? _detailVariantDifficulty;

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
        PlaceholderText = "Search title / localized title / artist / pack / song ID...",
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
        Text = "Choose a jacket/preview folder to resolve media by song ID",
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

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _previewPlayer.Dispose();
        _jacketResolver.Dispose();
        _searchDebounce.Dispose();
        base.OnFormClosed(e);
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
        _grid.RowTemplate.Height = 40;
        _grid.ColumnHeadersHeight = 32;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        var jacketColumn = new DataGridViewImageColumn
        {
            HeaderText = "Jacket",
            Width = 48,
            MinimumWidth = 48,
            Name = "Jacket",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        jacketColumn.DefaultCellStyle.NullValue = null;
        jacketColumn.DefaultCellStyle.Padding = new Padding(3);
        _grid.Columns.Add(jacketColumn);

        AddFillColumn("Title", "Title", 18, 110);
        AddFillColumn("Artist", "Artist", 17, 110);
        AddFillColumn("Pack", "Pack", 11, 70);
        AddFillColumn("Ver", "Version", 6, 45);
        foreach (var diff in new[] { "PST", "PRS", "FTR", "ETR", "BYD", "INS" }) AddFillColumn(diff, diff, 8, 52);
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
        toolbar.Controls.Add(UiTheme.MakeButton("Choose Jacket/Preview Folder...", (_, _) => ChooseMediaFolder()));
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
            Padding = Padding.Empty,
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
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex >= 0) _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            TogglePreview();
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "Jacket") return;
            if (_grid.Rows[e.RowIndex].Tag is not DbSong song) return;
            e.Value = _jacketResolver.GetThumbnail(song.Id, 34);
            e.FormattingApplied = true;
        };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name == "Jacket" && _grid.Rows[e.RowIndex].Tag is DbSong song)
            {
                var jacket = _jacketResolver.Resolve(song.Id);
                var preview = _jacketResolver.ResolvePreview(song.Id);
                e.ToolTipText = string.Join("\n", new[]
                {
                    jacket is null ? null : $"Jacket: {_jacketResolver.DisplayPath(jacket)}",
                    preview is null ? null : $"Preview: {_jacketResolver.DisplayPath(preview)}",
                    song.HasBeyondVariant ? "Beyond variant available in detail preview" : null,
                }.Where(x => x is not null));
                return;
            }
            e.ToolTipText = Convert.ToString(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? string.Empty;
        };
        _jacket.Click += (_, _) => ToggleJacketVariant();
        _jacket.Paint += (_, e) => PaintVariantTriangle(e.Graphics);
        _previewPlayer.PlaybackStopped += (_, _) => PreviewStoppedFromAnyThread();
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

    private void ChooseMediaFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the master jacket/preview folder. Jackets: <songid>.png/.jpg or <songid>/base|1080_base. Beyond variants also accept 3|1080_3 (and byd aliases). Previews: <songid>.<audio> or <songid>/preview|base.<audio>; Beyond audio accepts 3.<audio>. dl_<songid> folders are also supported.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = _jacketResolver.RootFolder ?? string.Empty,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            UseWaitCursor = true;
            _previewPlayer.StopImmediately();
            _grid.Rows.Clear();
            _jacketResolver.Configure(dialog.SelectedPath);
            RefreshGrid();
            ShowSelectedSong();
            UpdateStatus($"Media indexed from {_jacketResolver.RootFolder}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not index jacket/preview folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            + (_jacketResolver.IsConfigured
                ? $"   |   {_jacketResolver.Count} jackets   |   {_jacketResolver.PreviewCount} previews"
                : string.Empty)
            + (string.IsNullOrWhiteSpace(extra) ? string.Empty : $"   |   {extra}");
    }

    private void RefreshGrid()
    {
        var selectedKey = SelectedSong()?.DisplayKey;
        var q = DbSong.Normalize(_search.Text);
        var visible = string.IsNullOrWhiteSpace(q)
            ? _merged
            : _merged.Where(s => DbSong.Normalize(s.SearchText).Contains(q)).ToList();

        _refreshingGrid = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        DataGridViewRow? rowToSelect = null;

        foreach (var song in visible)
        {
            var values = new List<object?>
            {
                null,
                song.DisplayTitle,
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
            if (selectedKey is not null && string.Equals(song.DisplayKey, selectedKey, StringComparison.OrdinalIgnoreCase)) rowToSelect = row;
        }

        _grid.ApplyCurrentSort();
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

        if (!string.Equals(_detailSongKey, song.DisplayKey, StringComparison.OrdinalIgnoreCase))
        {
            _detailSongKey = song.DisplayKey;
            _detailVariantDifficulty = null;
        }
        if (!song.HasBeyondVariant) _detailVariantDifficulty = null;

        _details.Text = song.DetailText(_detailVariantDifficulty);
        LoadJacket(song);
        _jacket.Cursor = song.HasBeyondVariant ? Cursors.Hand : Cursors.Default;
        _jacket.Invalidate();
    }

    private void ToggleJacketVariant()
    {
        var song = SelectedSong();
        if (song is null || !song.HasBeyondVariant) return;
        _detailVariantDifficulty = string.Equals(_detailVariantDifficulty, "BYD", StringComparison.OrdinalIgnoreCase)
            ? null
            : "BYD";
        _details.Text = song.DetailText(_detailVariantDifficulty);
        LoadJacket(song);
        _jacket.Invalidate();
    }

    private void PaintVariantTriangle(Graphics graphics)
    {
        var song = SelectedSong();
        if (song is null || !song.HasBeyondVariant || _jacket.Width < 8 || _jacket.Height < 8) return;
        var size = Math.Min(28, Math.Max(16, Math.Min(_jacket.Width, _jacket.Height) / 7));
        var points = new[]
        {
            new Point(_jacket.ClientSize.Width - 1, 1),
            new Point(_jacket.ClientSize.Width - 1, size),
            new Point(_jacket.ClientSize.Width - size, 1),
        };
        using var brush = new SolidBrush(string.Equals(_detailVariantDifficulty, "BYD", StringComparison.OrdinalIgnoreCase)
            ? BeyondVariantColor
            : NormalVariantColor);
        graphics.FillPolygon(brush, points);
    }

    private void LoadJacket(DbSong song)
    {
        _jacket.Image?.Dispose();
        _jacket.Image = null;

        if (!_jacketResolver.IsConfigured)
        {
            _jacketState.Text = song.HasBeyondVariant
                ? "Click jacket to switch Normal / Beyond view; choose a media folder to show local jackets"
                : "Choose a jacket/preview folder to resolve media by song ID";
            return;
        }
        if (string.IsNullOrWhiteSpace(song.Id))
        {
            _jacketState.Text = "No song ID; cannot resolve local media";
            return;
        }

        var variant = string.Equals(_detailVariantDifficulty, "BYD", StringComparison.OrdinalIgnoreCase) ? "BYD" : null;
        var jacketPath = _jacketResolver.Resolve(song.Id, variant);
        var previewPath = _jacketResolver.ResolvePreview(song.Id, variant);
        var exactBeyond = variant is null ? null : _jacketResolver.ResolveExactBeyond(song.Id);

        if (jacketPath is not null)
        {
            try
            {
                using var stream = new FileStream(jacketPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var source = Image.FromStream(stream);
                _jacket.Image = new Bitmap(source);
            }
            catch (Exception ex)
            {
                _jacketState.Text = $"Could not render {Path.GetFileName(jacketPath)}: {ex.Message}";
                return;
            }
        }

        var jacketText = jacketPath is null ? "No jacket" : $"Jacket: {_jacketResolver.DisplayPath(jacketPath)}";
        var previewText = previewPath is null ? "No preview" : $"Preview: {_jacketResolver.DisplayPath(previewPath)}";
        var viewText = song.HasBeyondVariant
            ? variant is null
                ? "Normal • click jacket for Beyond"
                : exactBeyond is null
                    ? "Beyond • local Beyond jacket missing; showing base • click for Normal"
                    : "Beyond • click jacket for Normal"
            : null;
        _jacketState.Text = string.Join("   |   ", new[] { viewText, jacketText, previewText }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private void TogglePreview()
    {
        if (_previewPlayer.IsActive)
        {
            _previewPlayer.FadeOutAndStop();
            UpdateStatus("Fading out preview...");
            return;
        }

        var song = SelectedSong();
        if (song is null) return;
        if (!_jacketResolver.IsConfigured)
        {
            UpdateStatus("Choose a jacket/preview folder first");
            return;
        }

        var variant = song.HasBeyondVariant && string.Equals(_detailVariantDifficulty, "BYD", StringComparison.OrdinalIgnoreCase)
            ? "BYD"
            : null;
        var previewPath = _jacketResolver.ResolvePreview(song.Id, variant);
        if (previewPath is null)
        {
            UpdateStatus($"No preview found for {(variant is null ? song.Title : song.BeyondVariant?.VariantTitle ?? song.Title)}");
            return;
        }

        try
        {
            _previewPlayer.Play(previewPath);
            var playingTitle = variant is null ? song.Title : song.BeyondVariant?.VariantTitle ?? song.Title;
            UpdateStatus($"Playing preview: {playingTitle}   |   double-click to fade out");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Preview failed: {Path.GetFileName(previewPath)}");
            MessageBox.Show(this,
                $"Could not decode/play preview:\n{previewPath}\n\n{ex.Message}",
                "Preview playback failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void PreviewStoppedFromAnyThread()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(new Action(() => UpdateStatus("Preview stopped")));
            else UpdateStatus("Preview stopped");
        }
        catch
        {
            // Window is closing; no status update is needed.
        }
    }

    private void ClearDetails()
    {
        _detailSongKey = null;
        _detailVariantDifficulty = null;
        _details.Clear();
        _jacket.Image?.Dispose();
        _jacket.Image = null;
        _jacket.Cursor = Cursors.Default;
        _jacketState.Text = _jacketResolver.IsConfigured
            ? "Select a song"
            : "Choose a jacket/preview folder to resolve media by song ID";
        _jacket.Invalidate();
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
        return a.SharesTitleIdentity(b)
            && (string.IsNullOrWhiteSpace(a.Artist) || string.IsNullOrWhiteSpace(b.Artist)
                || a.MatchesArtist(b.Artist) || b.MatchesArtist(a.Artist));
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
