namespace Cheeseburger.DbStudio;

internal sealed class JacketResolver
{
    private static readonly string[] Extensions =
    {
        ".png", ".jpg", ".jpeg"
    };

    private static readonly string[] FolderImageNames =
    {
        "base", "1080_base"
    };

    private readonly Dictionary<string, string> _bySongId = new(StringComparer.OrdinalIgnoreCase);

    public string? RootFolder { get; private set; }
    public int Count => _bySongId.Count;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(RootFolder);

    public void Clear()
    {
        RootFolder = null;
        _bySongId.Clear();
    }

    public void Configure(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(folder);

        RootFolder = Path.GetFullPath(folder);
        _bySongId.Clear();

        // Layout A:
        //   <master>/<songid>.png
        //   <master>/<songid>.jpg
        foreach (var file in Directory.EnumerateFiles(RootFolder, "*", SearchOption.TopDirectoryOnly))
        {
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            var id = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(id)) continue;
            _bySongId.TryAdd(id, file);
        }

        // Layout B:
        //   <master>/<songid>/base.png
        //   <master>/<songid>/1080_base.jpg
        //   <master>/dl_<songid>/base.jpg
        //   <master>/dl_<songid>/1080_base.png
        //
        // If both base.* and 1080_base.* exist, base.* keeps priority so
        // existing jacket folders behave exactly as before.
        foreach (var directory in Directory.EnumerateDirectories(RootFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(folderName)) continue;

            var id = folderName.StartsWith("dl_", StringComparison.OrdinalIgnoreCase)
                ? folderName[3..]
                : folderName;
            if (string.IsNullOrWhiteSpace(id) || _bySongId.ContainsKey(id)) continue;

            var jacket = FindFolderJacket(directory);
            if (jacket is not null)
                _bySongId[id] = jacket;
        }
    }

    private static string? FindFolderJacket(string directory)
    {
        foreach (var name in FolderImageNames)
        {
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    public string? Resolve(string? songId)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        return _bySongId.TryGetValue(songId.Trim(), out var path) ? path : null;
    }

    public string DisplayPath(string path)
    {
        if (string.IsNullOrWhiteSpace(RootFolder)) return path;
        try { return Path.GetRelativePath(RootFolder, path); }
        catch { return path; }
    }
}
