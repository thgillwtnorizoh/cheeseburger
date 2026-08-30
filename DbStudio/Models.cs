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

    // Some modern charts override song-level identity/metadata. In particular,
    // ratingClass 3 + audioOverride=true is a distinct Beyond song variant.
    public string? VariantTitle { get; set; }
    public HashSet<string> VariantTitleAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? VariantArtist { get; set; }
    public string? VariantBpm { get; set; }
    public string? VariantAddedVersion { get; set; }
    public string? Background { get; set; }
    public string? ReleaseDate { get; set; }
    public bool AudioOverride { get; set; }
    public bool JacketOverride { get; set; }

    public bool IsDistinctBeyondVariant =>
        string.Equals(Difficulty, "BYD", StringComparison.OrdinalIgnoreCase) && AudioOverride;

    public ChartInfo Clone()
    {
        var clone = new ChartInfo
        {
            Difficulty = Difficulty,
            Level = Level,
            Constant = Constant,
            Notes = Notes,
            ChartDesigner = ChartDesigner,
            VariantTitle = VariantTitle,
            VariantArtist = VariantArtist,
            VariantBpm = VariantBpm,
            VariantAddedVersion = VariantAddedVersion,
            Background = Background,
            ReleaseDate = ReleaseDate,
            AudioOverride = AudioOverride,
            JacketOverride = JacketOverride,
        };
        foreach (var alias in VariantTitleAliases) clone.VariantTitleAliases.Add(alias);
        return clone;
    }

    public void MergeFrom(ChartInfo incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.Level)) Level = incoming.Level;
        if (incoming.Constant is not null) Constant = incoming.Constant;
        if (incoming.Notes is not null) Notes = incoming.Notes;
        if (!string.IsNullOrWhiteSpace(incoming.ChartDesigner)) ChartDesigner = incoming.ChartDesigner;
        if (!string.IsNullOrWhiteSpace(incoming.VariantTitle)) VariantTitle = incoming.VariantTitle;
        if (!string.IsNullOrWhiteSpace(incoming.VariantArtist)) VariantArtist = incoming.VariantArtist;
        if (!string.IsNullOrWhiteSpace(incoming.VariantBpm)) VariantBpm = incoming.VariantBpm;
        if (!string.IsNullOrWhiteSpace(incoming.VariantAddedVersion)) VariantAddedVersion = incoming.VariantAddedVersion;
        if (!string.IsNullOrWhiteSpace(incoming.Background)) Background = incoming.Background;
        if (!string.IsNullOrWhiteSpace(incoming.ReleaseDate)) ReleaseDate = incoming.ReleaseDate;
        AudioOverride |= incoming.AudioOverride;
        JacketOverride |= incoming.JacketOverride;
        foreach (var alias in incoming.VariantTitleAliases) VariantTitleAliases.Add(alias);
    }

    public bool MatchesTitle(string? title)
    {
        var needle = DbSong.Normalize(title);
        if (needle.Length == 0) return false;
        if (DbSong.Normalize(VariantTitle) == needle) return true;
        return VariantTitleAliases.Any(x => DbSong.Normalize(x) == needle);
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
    public HashSet<string> TitleAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
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

    public ChartInfo? BeyondVariant => Charts.TryGetValue("BYD", out var chart) && chart.IsDistinctBeyondVariant
        ? chart
        : null;

    public bool HasBeyondVariant => BeyondVariant is not null;

    public string DisplayTitle
    {
        get
        {
            var variant = BeyondVariant?.VariantTitle;
            return !string.IsNullOrWhiteSpace(variant) && Normalize(variant) != Normalize(Title)
                ? $"{Title} | {variant}"
                : Title;
        }
    }

    public string DisplayKey => !string.IsNullOrWhiteSpace(Id)
        ? $"id:{Normalize(Id)}"
        : $"title:{Normalize(Title)}|artist:{Normalize(Artist)}";

    public IEnumerable<string> AllTitles()
    {
        yield return Title;
        foreach (var alias in TitleAliases) yield return alias;
        foreach (var chart in Charts.Values)
        {
            if (!string.IsNullOrWhiteSpace(chart.VariantTitle)) yield return chart.VariantTitle!;
            foreach (var alias in chart.VariantTitleAliases) yield return alias;
        }
    }

    public bool MatchesTitle(string? title)
    {
        var needle = Normalize(title);
        return needle.Length > 0 && AllTitles().Any(x => Normalize(x) == needle);
    }

    public bool SharesTitleIdentity(DbSong other)
    {
        var mine = AllTitles().Select(Normalize).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        return other.AllTitles().Select(Normalize).Any(mine.Contains);
    }

    public bool MatchesArtist(string? artist)
    {
        var needle = Normalize(artist);
        if (needle.Length == 0) return false;
        if (Normalize(Artist) == needle) return true;
        return Charts.Values.Any(c => Normalize(c.VariantArtist) == needle);
    }

    public string SearchText => string.Join(' ', new[]
    {
        string.Join(' ', AllTitles()),
        Artist,
        Pack,
        Id,
        string.Join(" ", Charts.Values.Select(c => c.VariantArtist).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)),
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

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
        foreach (var alias in TitleAliases) clone.TitleAliases.Add(alias);
        foreach (var (key, value) in Charts) clone.Charts[key] = value.Clone();
        foreach (var source in Sources) clone.Sources.Add(source);
        return clone;
    }

    public void MergeFrom(DbSong incoming)
    {
        var incomingIsOfficial = !string.IsNullOrWhiteSpace(incoming.Id);
        if (incomingIsOfficial) Id = incoming.Id;

        // The official songlist owns base song identity. Wiki enrichments may fill
        // gaps, but must not replace canonical title/artist/etc. once an ID exists.
        if (incomingIsOfficial || string.IsNullOrWhiteSpace(Title))
            if (!string.IsNullOrWhiteSpace(incoming.Title)) Title = incoming.Title;
        if (incomingIsOfficial || string.IsNullOrWhiteSpace(Artist))
            if (!string.IsNullOrWhiteSpace(incoming.Artist)) Artist = incoming.Artist;
        if (incomingIsOfficial || string.IsNullOrWhiteSpace(Pack))
            if (!string.IsNullOrWhiteSpace(incoming.Pack)) Pack = incoming.Pack;
        if (incomingIsOfficial || string.IsNullOrWhiteSpace(Bpm))
            if (!string.IsNullOrWhiteSpace(incoming.Bpm)) Bpm = incoming.Bpm;
        if (incomingIsOfficial || string.IsNullOrWhiteSpace(Side))
            if (!string.IsNullOrWhiteSpace(incoming.Side)) Side = incoming.Side;
        if (incomingIsOfficial || string.IsNullOrWhiteSpace(AddedVersion))
            if (!string.IsNullOrWhiteSpace(incoming.AddedVersion)) AddedVersion = incoming.AddedVersion;

        if (!string.IsNullOrWhiteSpace(incoming.Length) && string.IsNullOrWhiteSpace(Length)) Length = incoming.Length;
        if (!string.IsNullOrWhiteSpace(incoming.Artwork) && string.IsNullOrWhiteSpace(Artwork)) Artwork = incoming.Artwork;
        if (!string.IsNullOrWhiteSpace(incoming.SourceUrl)) SourceUrl = incoming.SourceUrl;

        foreach (var alias in incoming.TitleAliases) TitleAliases.Add(alias);
        if (!string.IsNullOrWhiteSpace(incoming.Title)) TitleAliases.Add(incoming.Title);

        foreach (var (diff, chart) in incoming.Charts)
        {
            if (!Charts.TryGetValue(diff, out var existing))
                Charts[diff] = chart.Clone();
            else
                existing.MergeFrom(chart);
        }

        foreach (var source in incoming.Sources) Sources.Add(source);
    }

    public string DetailText(string? variantDifficulty = null)
    {
        var variant = !string.IsNullOrWhiteSpace(variantDifficulty)
            && Charts.TryGetValue(variantDifficulty, out var selected)
            && selected.IsDistinctBeyondVariant
            ? selected
            : null;

        var sb = new StringBuilder();
        if (variant is null)
        {
            sb.AppendLine(DisplayTitle);
        }
        else
        {
            sb.AppendLine($"{variant.VariantTitle ?? Title}  [{variant.Difficulty} variant of {Title}]");
        }
        if (!string.IsNullOrWhiteSpace(Id)) sb.AppendLine($"ID: {Id}");
        var artist = variant?.VariantArtist ?? Artist;
        var bpm = variant?.VariantBpm ?? Bpm;
        var version = variant?.VariantAddedVersion ?? AddedVersion;
        if (!string.IsNullOrWhiteSpace(artist)) sb.AppendLine($"Artist: {artist}");
        if (!string.IsNullOrWhiteSpace(Pack)) sb.AppendLine($"Pack: {Pack}");
        if (!string.IsNullOrWhiteSpace(bpm)) sb.AppendLine($"BPM: {bpm}");
        if (!string.IsNullOrWhiteSpace(Length)) sb.AppendLine($"Length: {Length}");
        if (!string.IsNullOrWhiteSpace(Side)) sb.AppendLine($"Side: {Side}");
        if (!string.IsNullOrWhiteSpace(version)) sb.AppendLine($"Added: {version}");
        if (variant is not null)
        {
            if (!string.IsNullOrWhiteSpace(variant.Background)) sb.AppendLine($"Background: {variant.Background}");
            if (!string.IsNullOrWhiteSpace(variant.ReleaseDate)) sb.AppendLine($"Release date: {variant.ReleaseDate}");
            sb.AppendLine($"Audio override: {(variant.AudioOverride ? "yes" : "no")}");
            sb.AppendLine($"Jacket override: {(variant.JacketOverride ? "yes" : "no")}");
        }
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
            if (chart.IsDistinctBeyondVariant && !string.IsNullOrWhiteSpace(chart.VariantTitle))
                sb.Append($"  title={chart.VariantTitle}");
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
