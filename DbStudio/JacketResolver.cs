namespace Cheeseburger.DbStudio;

internal sealed class JacketResolver
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg"
    };

    private readonly Dictionary<string, string> _bySongId = new(StringComparer.OrdinalIgnoreCase);

    public string? RootFolder { get; private set; }
    public int Count => _bySongId.Count;

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
            if (!Extensions.Contains(Path.GetExtension(file))) continue;
            var id = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(id)) continue;
            _bySongId.TryAdd(id, file);
        }

        // Layout B:
        //   <master>/<songid>/base.png
        //   <master>/dl_<songid>/base.jpg
        foreach (var directory in Directory.EnumerateDirectories(RootFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(folderName)) continue;

            var id = folderName.StartsWith("dl_", StringComparison.OrdinalIgnoreCase)
                ? folderName[3..]
                : folderName;
            if (string.IsNullOrWhiteSpace(id) || _bySongId.ContainsKey(id)) continue;

            var baseImage = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file =>
                    Extensions.Contains(Path.GetExtension(file))
                    && string.Equals(Path.GetFileNameWithoutExtension(file), "base", StringComparison.OrdinalIgnoreCase));

            if (baseImage is not null)
                _bySongId[id] = baseImage;
        }
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
