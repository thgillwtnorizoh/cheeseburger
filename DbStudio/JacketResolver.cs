using System.Drawing.Drawing2D;

namespace Cheeseburger.DbStudio;

internal sealed class JacketResolver : IDisposable
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };
    private static readonly string[] AudioExtensions = { ".ogg", ".oga", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif" };
    private static readonly string[] FolderImageNames = { "base", "1080_base" };
    private static readonly string[] FolderPreviewNames = { "preview", "base" };
    private static readonly string[] BeyondImageNames = { "3", "1080_3", "byd", "1080_byd" };
    private static readonly string[] BeyondPreviewNames = { "3", "byd" };

    private readonly Dictionary<string, string> _jackets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _previews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _beyondJackets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _beyondPreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    public string? RootFolder { get; private set; }
    public int Count => _jackets.Count;
    public int PreviewCount => _previews.Count;
    public int BeyondJacketCount => _beyondJackets.Count;
    public int BeyondPreviewCount => _beyondPreviews.Count;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(RootFolder);

    public void Clear()
    {
        RootFolder = null;
        _jackets.Clear();
        _previews.Clear();
        _beyondJackets.Clear();
        _beyondPreviews.Clear();
        DisposeThumbnails();
    }

    public void Configure(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);

        RootFolder = Path.GetFullPath(folder);
        _jackets.Clear();
        _previews.Clear();
        _beyondJackets.Clear();
        _beyondPreviews.Clear();
        DisposeThumbnails();

        foreach (var file in Directory.EnumerateFiles(RootFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var extension = Path.GetExtension(file);
            var stem = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(stem)) continue;

            if (TrySplitVariantStem(stem, out var id, out var isBeyond))
            {
                if (IsImageExtension(extension))
                {
                    if (isBeyond) _beyondJackets.TryAdd(id, file);
                    else _jackets.TryAdd(id, file);
                }
                else if (IsAudioExtension(extension))
                {
                    if (isBeyond) _beyondPreviews.TryAdd(id, file);
                    else _previews.TryAdd(id, file);
                }
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(RootFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(folderName)) continue;

            var id = folderName.StartsWith("dl_", StringComparison.OrdinalIgnoreCase)
                ? folderName[3..]
                : folderName;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();

            if (!_jackets.ContainsKey(id))
            {
                var jacket = FindNamedFile(files, FolderImageNames, ImageExtensions);
                if (jacket is not null) _jackets[id] = jacket;
            }
            if (!_previews.ContainsKey(id))
            {
                var preview = FindNamedFile(files, FolderPreviewNames, AudioExtensions);
                if (preview is not null) _previews[id] = preview;
            }
            if (!_beyondJackets.ContainsKey(id))
            {
                var jacket = FindNamedFile(files, BeyondImageNames, ImageExtensions);
                if (jacket is not null) _beyondJackets[id] = jacket;
            }
            if (!_beyondPreviews.ContainsKey(id))
            {
                var preview = FindNamedFile(files, BeyondPreviewNames, AudioExtensions);
                if (preview is not null) _beyondPreviews[id] = preview;
            }
        }
    }

    private static bool TrySplitVariantStem(string stem, out string id, out bool isBeyond)
    {
        foreach (var suffix in new[] { "_3", "_byd" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && stem.Length > suffix.Length)
            {
                id = stem[..^suffix.Length];
                isBeyond = true;
                return true;
            }
        }
        id = stem;
        isBeyond = false;
        return true;
    }

    private static string? FindNamedFile(IEnumerable<string> files, IEnumerable<string> names, IEnumerable<string> extensions)
    {
        var candidates = files.ToArray();
        foreach (var name in names)
        {
            foreach (var extension in extensions)
            {
                var match = candidates.FirstOrDefault(file =>
                    string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
        }
        return null;
    }

    private static bool IsImageExtension(string extension) => ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    private static bool IsAudioExtension(string extension) => AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public string? Resolve(string? songId, string? difficulty = null)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        var id = songId.Trim();
        if (string.Equals(difficulty, "BYD", StringComparison.OrdinalIgnoreCase)
            && _beyondJackets.TryGetValue(id, out var beyond)) return beyond;
        return _jackets.TryGetValue(id, out var path) ? path : null;
    }

    public string? ResolveExactBeyond(string? songId)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        return _beyondJackets.TryGetValue(songId.Trim(), out var path) ? path : null;
    }

    public string? ResolvePreview(string? songId, string? difficulty = null)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        var id = songId.Trim();
        if (string.Equals(difficulty, "BYD", StringComparison.OrdinalIgnoreCase)
            && _beyondPreviews.TryGetValue(id, out var beyond)) return beyond;
        return _previews.TryGetValue(id, out var path) ? path : null;
    }

    public Image? GetThumbnail(string? songId, int size = 40)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        var key = $"{songId.Trim()}|{size}";
        if (_thumbnailCache.TryGetValue(key, out var cached)) return cached;

        var path = Resolve(songId);
        if (path is null) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var source = Image.FromStream(stream);
            var thumb = new Bitmap(size, size);
            using var g = Graphics.FromImage(thumb);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var scale = Math.Min((float)size / source.Width, (float)size / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var x = (size - width) / 2;
            var y = (size - height) / 2;
            g.DrawImage(source, new Rectangle(x, y, width, height));
            _thumbnailCache[key] = thumb;
            return thumb;
        }
        catch
        {
            return null;
        }
    }

    public string DisplayPath(string path)
    {
        if (string.IsNullOrWhiteSpace(RootFolder)) return path;
        try { return Path.GetRelativePath(RootFolder, path); }
        catch { return path; }
    }

    private void DisposeThumbnails()
    {
        foreach (var image in _thumbnailCache.Values) image.Dispose();
        _thumbnailCache.Clear();
    }

    public void Dispose()
    {
        DisposeThumbnails();
        GC.SuppressFinalize(this);
    }
}
