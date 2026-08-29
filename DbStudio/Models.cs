using System.Globalization;
using System.Text;

namespace Cheeseburger.DbStudio;

internal sealed class ChartInfo
{
    public string Difficulty { get; set; } = "";
    public string? Level { get; set; }
    public double? Constant { get; set; }
    public int? Notes { get; set; }
    public string? ChartDesigner { get; set; }

    public ChartInfo Clone() => new()
    {
        Difficulty = Difficulty,
        Level = Level,
        Constant = Constant,
        Notes = Notes,
        ChartDesigner = ChartDesigner,
    };

    public void MergeFrom(ChartInfo incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.Level)) Level = incoming.Level;
        if (incoming.Constant is not null) Constant = incoming.Constant;
        if (incoming.Notes is not null) Notes = incoming.Notes;
        if (!string.IsNullOrWhiteSpace(incoming.ChartDesigner)) ChartDesigner = incoming.ChartDesigner;
    }

    public string Compact()
    {
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(Level)) bits.Add(Level!);
        if (Constant is not null) bits.Add($"c{Constant.Value.ToString("0.0##", CultureInfo.InvariantCulture)}");
        if (Notes is not null) bits.Add($"n{Notes.Value}");
        return bits.Count == 0 ? "-" : string.Join(" · ", bits);
    }
}

internal sealed class DbSong
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Pack { get; set; }
    public string? Bpm { get; set; }
    public string? Length { get; set; }
    public string? Side { get; set; }
    public string? Artwork { get; set; }
    public string? AddedVersion { get; set; }
    public string? SourceUrl { get; set; }
    public Dictionary<string, ChartInfo> Charts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string DisplayKey => !string.IsNullOrWhiteSpace(Id)
        ? $"id:{Normalize(Id)}"
        : $"title:{Normalize(Title)}|artist:{Normalize(Artist)}";

    public DbSong Clone()
    {
        var clone = new DbSong
        {
            Id = Id,
            Title = Title,
            Artist = Artist,
            Pack = Pack,
            Bpm = Bpm,
            Length = Length,
            Side = Side,
            Artwork = Artwork,
            AddedVersion = AddedVersion,
            SourceUrl = SourceUrl,
        };
        foreach (var (key, value) in Charts) clone.Charts[key] = value.Clone();
        foreach (var source in Sources) clone.Sources.Add(source);
        return clone;
    }

    public void MergeFrom(DbSong incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.Id)) Id = incoming.Id;
        if (!string.IsNullOrWhiteSpace(incoming.Title)) Title = incoming.Title;
        if (!string.IsNullOrWhiteSpace(incoming.Artist)) Artist = incoming.Artist;
        if (!string.IsNullOrWhiteSpace(incoming.Pack)) Pack = incoming.Pack;
        if (!string.IsNullOrWhiteSpace(incoming.Bpm)) Bpm = incoming.Bpm;
        if (!string.IsNullOrWhiteSpace(incoming.Length)) Length = incoming.Length;
        if (!string.IsNullOrWhiteSpace(incoming.Side)) Side = incoming.Side;
        if (!string.IsNullOrWhiteSpace(incoming.Artwork)) Artwork = incoming.Artwork;
        if (!string.IsNullOrWhiteSpace(incoming.AddedVersion)) AddedVersion = incoming.AddedVersion;
        if (!string.IsNullOrWhiteSpace(incoming.SourceUrl)) SourceUrl = incoming.SourceUrl;

        foreach (var (diff, chart) in incoming.Charts)
        {
            if (!Charts.TryGetValue(diff, out var existing))
            {
                Charts[diff] = chart.Clone();
            }
            else
            {
                existing.MergeFrom(chart);
            }
        }

        foreach (var source in incoming.Sources) Sources.Add(source);
    }

    public string DetailText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title);
        if (!string.IsNullOrWhiteSpace(Id)) sb.AppendLine($"ID: {Id}");
        if (!string.IsNullOrWhiteSpace(Artist)) sb.AppendLine($"Artist: {Artist}");
        if (!string.IsNullOrWhiteSpace(Pack)) sb.AppendLine($"Pack: {Pack}");
        if (!string.IsNullOrWhiteSpace(Bpm)) sb.AppendLine($"BPM: {Bpm}");
        if (!string.IsNullOrWhiteSpace(Length)) sb.AppendLine($"Length: {Length}");
        if (!string.IsNullOrWhiteSpace(Side)) sb.AppendLine($"Side: {Side}");
        if (!string.IsNullOrWhiteSpace(AddedVersion)) sb.AppendLine($"Added: {AddedVersion}");
        if (!string.IsNullOrWhiteSpace(Artwork)) sb.AppendLine($"Artwork: {Artwork}");
        if (!string.IsNullOrWhiteSpace(SourceUrl)) sb.AppendLine($"Page: {SourceUrl}");
        sb.AppendLine($"Sources: {string.Join(", ", Sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
        sb.AppendLine();
        sb.AppendLine("Charts");
        foreach (var diff in new[] { "PST", "PRS", "FTR", "ETR", "BYD", "INS" })
        {
            if (!Charts.TryGetValue(diff, out var chart)) continue;
            sb.Append($"{diff,-3}  level={chart.Level ?? "-",-3}  constant=");
            sb.Append(chart.Constant?.ToString("0.0##", CultureInfo.InvariantCulture) ?? "-");
            sb.Append($"  notes={chart.Notes?.ToString() ?? "-"}");
            if (!string.IsNullOrWhiteSpace(chart.ChartDesigner)) sb.Append($"  designer={chart.ChartDesigner}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return string.Join(' ', value.Normalize(NormalizationForm.FormKC)
            .Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

internal sealed class SourceDocument
{
    public string FilePath { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public List<DbSong> Songs { get; init; } = new();
    public HashSet<string> DetachedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => $"{Name}  [{Kind}]  {Songs.Count} songs";
}
