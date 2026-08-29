using System.Globalization;

namespace Cheeseburger.DbStudio;

internal sealed class SmoothDataGridView : DataGridView
{
    private static readonly HashSet<string> DifficultyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "PST", "PRS", "FTR", "ETR", "BYD", "INS"
    };

    private string? _sortColumnName;
    private SortOrder _sortOrder = SortOrder.None;
    private bool _sorting;

    public SmoothDataGridView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }

    protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0 || e.ColumnIndex >= Columns.Count)
            return;

        var column = Columns[e.ColumnIndex];
        if (column.SortMode == DataGridViewColumnSortMode.NotSortable)
            return;

        var nextOrder = string.Equals(_sortColumnName, column.Name, StringComparison.OrdinalIgnoreCase)
            && _sortOrder == SortOrder.Ascending
                ? SortOrder.Descending
                : SortOrder.Ascending;

        _sortColumnName = column.Name;
        _sortOrder = nextOrder;
        ApplyCurrentSort();

        // Intentionally do not call the base implementation. The stock grid sorts
        // the rendered cell strings, which makes Arcaea levels sort lexically
        // (for example 9, 8+, 8, 11) and leaves ties in unstable row order.
    }

    public void ApplyCurrentSort()
    {
        if (_sorting || string.IsNullOrWhiteSpace(_sortColumnName) || _sortOrder == SortOrder.None)
            return;
        if (!Columns.Contains(_sortColumnName))
            return;

        try
        {
            _sorting = true;
            Sort(new SongRowComparer(_sortColumnName, _sortOrder));

            foreach (DataGridViewColumn column in Columns)
            {
                if (column.SortMode != DataGridViewColumnSortMode.NotSortable)
                    column.HeaderCell.SortGlyphDirection = SortOrder.None;
            }

            var sortedColumn = Columns[_sortColumnName];
            if (sortedColumn.SortMode != DataGridViewColumnSortMode.NotSortable)
                sortedColumn.HeaderCell.SortGlyphDirection = _sortOrder;
        }
        finally
        {
            _sorting = false;
        }
    }

    private sealed class SongRowComparer : System.Collections.IComparer
    {
        private readonly string _columnName;
        private readonly SortOrder _order;

        public SongRowComparer(string columnName, SortOrder order)
        {
            _columnName = columnName;
            _order = order;
        }

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is not DataGridViewRow rowA) return 1;
            if (y is not DataGridViewRow rowB) return -1;
            if (rowA.Tag is not DbSong songA) return 1;
            if (rowB.Tag is not DbSong songB) return -1;

            var primary = ComparePrimary(songA, songB, _columnName, out var aMissing, out var bMissing);

            // Missing charts/values belong at the bottom in either direction.
            if (aMissing != bMissing)
                return aMissing ? 1 : -1;

            if (primary != 0)
                return _order == SortOrder.Descending ? -primary : primary;

            // Keep ties human-readable and deterministic. Importantly, this stays
            // A -> Z even when the primary difficulty is sorted descending.
            var title = CompareText(songA.Title, songB.Title);
            if (title != 0) return title;

            var artist = CompareText(songA.Artist, songB.Artist);
            if (artist != 0) return artist;

            return CompareText(songA.Id, songB.Id);
        }
    }

    private static int ComparePrimary(
        DbSong a,
        DbSong b,
        string columnName,
        out bool aMissing,
        out bool bMissing)
    {
        if (DifficultyColumns.Contains(columnName))
        {
            var aLevel = DifficultyKey(a, columnName);
            var bLevel = DifficultyKey(b, columnName);
            aMissing = aLevel is null;
            bMissing = bLevel is null;
            return Nullable.Compare(aLevel, bLevel);
        }

        string? aValue;
        string? bValue;

        switch (columnName)
        {
            case "Title":
                aValue = a.Title;
                bValue = b.Title;
                break;
            case "Artist":
                aValue = a.Artist;
                bValue = b.Artist;
                break;
            case "Pack":
                aValue = a.Pack;
                bValue = b.Pack;
                break;
            case "Version":
                aMissing = string.IsNullOrWhiteSpace(a.AddedVersion);
                bMissing = string.IsNullOrWhiteSpace(b.AddedVersion);
                return CompareVersion(a.AddedVersion, b.AddedVersion);
            case "Sources":
                aValue = string.Join(", ", a.Sources.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
                bValue = string.Join(", ", b.Sources.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
                break;
            default:
                aValue = null;
                bValue = null;
                break;
        }

        aMissing = string.IsNullOrWhiteSpace(aValue);
        bMissing = string.IsNullOrWhiteSpace(bValue);
        return CompareText(aValue, bValue);
    }

    private static double? DifficultyKey(DbSong song, string difficulty)
    {
        if (!song.Charts.TryGetValue(difficulty, out var chart) || string.IsNullOrWhiteSpace(chart.Level))
            return null;

        var text = chart.Level.Trim();
        if (text is "-" or "?") return null;

        var hasPlus = text.EndsWith('+');
        if (hasPlus) text = text[..^1].TrimEnd();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        // The exact offset is irrelevant; it simply has to sit between N and N+1.
        return value + (hasPlus ? 0.5 : 0.0);
    }

    private static int CompareVersion(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return 1;
        if (string.IsNullOrWhiteSpace(b)) return -1;

        var aParts = a.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var bParts = b.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(aParts.Length, bParts.Length);

        for (var i = 0; i < count; i++)
        {
            var ai = i < aParts.Length && int.TryParse(aParts[i], out var av) ? av : 0;
            var bi = i < bParts.Length && int.TryParse(bParts[i], out var bv) ? bv : 0;
            var cmp = ai.CompareTo(bi);
            if (cmp != 0) return cmp;
        }

        return CompareText(a, b);
    }

    private static int CompareText(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return 1;
        if (string.IsNullOrWhiteSpace(b)) return -1;
        return StringComparer.CurrentCultureIgnoreCase.Compare(a.Trim(), b.Trim());
    }
}
