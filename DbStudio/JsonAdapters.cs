using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cheeseburger.DbStudio;

internal static class JsonAdapters
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static SourceDocument Load(string path)
    {
        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text) ?? throw new InvalidDataException("JSON root is null.");
        var songs = new List<DbSong>();
        string kind;

        if (IsNormalizedEntry(root))
        {
            kind = "wiki-entry";
            songs.Add(ParseNormalizedEntry(root.AsObject(), Path.GetFileName(path)));
        }
        else if (root is JsonObject obj && obj["entries"] is JsonArray entries)
        {
            kind = "merged-entries";
            songs.AddRange(entries.OfType<JsonObject>().Where(IsNormalizedEntry).Select(x => ParseNormalizedEntry(x, Path.GetFileName(path))));
        }
        else if (root is JsonObject pagesObj && pagesObj["songs"] is JsonArray maybeSongs && maybeSongs.OfType<JsonObject>().Any(IsNormalizedEntry))
        {
            kind = "wiki-collection";
            songs.AddRange(maybeSongs.OfType<JsonObject>().Where(IsNormalizedEntry).Select(x => ParseNormalizedEntry(x, Path.GetFileName(path))));
        }
        else if (root is JsonObject songlistObj && songlistObj["songs"] is JsonArray songlist)
        {
            kind = "songlist";
            songs.AddRange(songlist.OfType<JsonObject>().Where(x => x["deleted"]?.GetValue<bool?>() != true).Select(x => ParseSonglistSong(x, Path.GetFileName(path))));
        }
        else
        {
            throw new InvalidDataException("Unsupported JSON shape. Expected songlist, normalized wiki entry, or merged entry collection.");
        }

        if (songs.Count == 0) throw new InvalidDataException("No usable songs were found in this file.");
        return new SourceDocument { FilePath = path, Name = Path.GetFileName(path), Kind = kind, Songs = songs };
    }

    private static bool IsNormalizedEntry(JsonNode? node)
        => node is JsonObject o && o["source"] is not null && o["song"] is JsonObject && o["charts"] is JsonObject;

    private static DbSong ParseNormalizedEntry(JsonObject entry, string sourceName)
    {
        var songObj = entry["song"] as JsonObject ?? new JsonObject();
        var song = new DbSong
        {
            Id = Str(songObj, "id"),
            Title = Str(songObj, "title") ?? "(untitled)",
            Artist = Str(songObj, "artist"),
            Pack = Str(songObj, "pack"),
            Bpm = Scalar(songObj["bpm"]),
            Length = Str(songObj, "length"),
            Side = Scalar(songObj["side"]),
            Artwork = Str(songObj, "artwork"),
            AddedVersion = Str(songObj, "added_version"),
            SourceUrl = Str(entry["_meta"] as JsonObject, "source_url"),
        };
        song.TitleAliases.Add(song.Title);
        AddStringArray(song.TitleAliases, songObj["title_aliases"]);
        AddLocalizedValues(song.TitleAliases, songObj["title_localized"]);
        song.Sources.Add(Str(entry, "source") ?? sourceName);

        if (entry["charts"] is JsonObject charts)
        {
            foreach (var (diff, node) in charts)
            {
                if (node is not JsonObject c) continue;
                var chart = new ChartInfo
                {
                    Difficulty = diff,
                    Level = Scalar(c["level"]),
                    Constant = Number(c["constant"]),
                    Notes = Integer(c["notes"]),
                    ChartDesigner = Str(c, "chart_designer"),
                    VariantTitle = Str(c, "variant_title"),
                    VariantArtist = Str(c, "variant_artist"),
                    VariantBpm = Scalar(c["variant_bpm"]),
                    VariantAddedVersion = Str(c["variant_added_version"]),
                    Background = Str(c, "background"),
                    ReleaseDate = Scalar(c["release_date"]),
                    AudioOverride = Boolean(c["audio_override"]),
                    JacketOverride = Boolean(c["jacket_override"]),
                };
                ReadClassification(c, chart, diff);
                ReadVisibility(c, chart);
                AddStringArray(chart.VariantTitleAliases, c["variant_title_aliases"]);
                if (!string.IsNullOrWhiteSpace(chart.VariantTitle)) chart.VariantTitleAliases.Add(chart.VariantTitle);
                song.Charts[diff] = chart;
            }
        }
        return song;
    }

    private static DbSong ParseSonglistSong(JsonObject obj, string sourceName)
    {
        var title = Str(obj, "title") ?? Localized(obj["title_localized"]) ?? Str(obj, "id") ?? "(untitled)";
        var song = new DbSong
        {
            Id = Str(obj, "id"),
            Title = title,
            Artist = Str(obj, "artist"),
            Pack = Str(obj, "set") ?? Str(obj, "pack"),
            Bpm = Scalar(obj["bpm"]) ?? Scalar(obj["bpm_base"]),
            AddedVersion = Scalar(obj["version"]),
            Side = Scalar(obj["side"]),
        };
        song.TitleAliases.Add(title);
        AddLocalizedValues(song.TitleAliases, obj["title_localized"]);
        AddSearchValues(song.TitleAliases, obj["search_title"]);
        song.Sources.Add(sourceName);

        if (obj["difficulties"] is JsonArray diffs)
        {
            foreach (var node in diffs.OfType<JsonObject>())
            {
                var ratingClass = Integer(node["ratingClass"]);
                if (ratingClass is null) continue;
                var alias = Integer(node["ratingClassAlias"]);
                var diff = ratingClass switch
                {
                    0 => "PST",
                    1 => "PRS",
                    2 => "FTR",
                    3 when alias == 1 => "INS",
                    3 => "BYD",
                    4 => "ETR",
                    _ => $"RC{ratingClass}"
                };
                var rating = Integer(node["rating"]);
                var plus = node["ratingPlus"]?.GetValue<bool?>() == true;
                var chart = new ChartInfo
                {
                    Difficulty = diff,
                    Level = rating is null ? null : rating.Value.ToString(CultureInfo.InvariantCulture) + (plus ? "+" : ""),
                    Constant = Number(node["constant"]),
                    Notes = Integer(node["notes"]) ?? Integer(node["note_count"]),
                    ChartDesigner = Str(node, "chartDesigner") ?? Str(node, "designer"),
                    RatingClass = ratingClass,
                    RatingClassAlias = alias,
                    BydType = ratingClass == 3 ? (alias == 1 ? 1 : 0) : null,
                    ClassificationSource = "songlist",
                    HiddenUntilUnlocked = NullableBoolean(node["hiddenUntilUnlocked"]) ?? NullableBoolean(node["hidden_until_unlocked"]),
                    HiddenUntil = Str(node, "hiddenUntil") ?? Str(node, "hidden_until"),
                    VariantTitle = Localized(node["title_localized"]) ?? Str(node, "title"),
                    VariantArtist = Str(node, "artist"),
                    VariantBpm = Scalar(node["bpm"]) ?? Scalar(node["bpm_base"]),
                    VariantAddedVersion = Scalar(node["version"]),
                    Background = Str(node, "bg"),
                    ReleaseDate = Scalar(node["date"]),
                    AudioOverride = Boolean(node["audioOverride"]),
                    JacketOverride = Boolean(node["jacketOverride"]),
                };
                AddLocalizedValues(chart.VariantTitleAliases, node["title_localized"]);
                AddSearchValues(chart.VariantTitleAliases, node["search_title"]);
                if (!string.IsNullOrWhiteSpace(chart.VariantTitle)) chart.VariantTitleAliases.Add(chart.VariantTitle);
                song.Charts[diff] = chart;
            }
        }
        return song;
    }

    public static List<DbSong> Merge(IEnumerable<SourceDocument> docs, HashSet<string>? hidden = null)
    {
        var result = new List<DbSong>();
        foreach (var doc in docs)
        {
            foreach (var incoming in doc.Songs)
            {
                if (doc.DetachedKeys.Contains(incoming.DisplayKey) || hidden?.Contains(incoming.DisplayKey) == true) continue;
                var target = FindTarget(result, incoming);
                if (target is null) result.Add(incoming.Clone());
                else target.MergeFrom(incoming);
            }
        }
        return result.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static DbSong? FindTarget(List<DbSong> existing, DbSong incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.Id))
        {
            var byId = existing.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Id) && DbSong.Normalize(x.Id) == DbSong.Normalize(incoming.Id));
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(incoming.SourceUrl))
        {
            var byUrl = existing.FirstOrDefault(x => string.Equals(x.SourceUrl, incoming.SourceUrl, StringComparison.OrdinalIgnoreCase));
            if (byUrl is not null) return byUrl;
        }

        // Identity matching is localization-aware in both directions. This lets
        // songlist "Hikari" (alias 光) match a WikiWiki entry titled 光, even if
        // either source was loaded first. Difficulty-localized titles such as
        // Axium Divergence are also considered, but remain chart-scoped metadata.
        var titleMatches = existing.Where(x => x.SharesTitleIdentity(incoming)).ToList();
        if (titleMatches.Count == 1) return titleMatches[0];
        if (!string.IsNullOrWhiteSpace(incoming.Artist))
        {
            var byArtist = titleMatches.Where(x => x.MatchesArtist(incoming.Artist)).ToList();
            if (byArtist.Count == 1) return byArtist[0];
        }
        return null;
    }

    public static void Export(string path, IEnumerable<DbSong> songs)
    {
        File.WriteAllText(path, BuildExportDocument(songs).ToJsonString(Pretty));
    }

    private static JsonObject BuildExportDocument(IEnumerable<DbSong> songs)
    {
        var entries = new JsonArray();
        foreach (var song in songs)
        {
            var charts = new JsonObject();
            foreach (var (diff, c) in song.Charts)
            {
                var chartObject = new JsonObject
                {
                    ["level"] = c.Level,
                    ["constant"] = c.Constant,
                    ["notes"] = c.Notes,
                    ["chart_designer"] = c.ChartDesigner,
                    ["variant_title"] = c.VariantTitle,
                    ["variant_title_aliases"] = ToJsonArray(c.VariantTitleAliases),
                    ["variant_artist"] = c.VariantArtist,
                    ["variant_bpm"] = c.VariantBpm,
                    ["variant_added_version"] = c.VariantAddedVersion,
                    ["background"] = c.Background,
                    ["release_date"] = c.ReleaseDate,
                    ["audio_override"] = c.AudioOverride,
                    ["jacket_override"] = c.JacketOverride,
                };

                var classification = BuildClassification(c, diff);
                if (classification is not null) chartObject["classification"] = classification;

                if (c.HiddenUntilUnlocked is not null || !string.IsNullOrWhiteSpace(c.HiddenUntil))
                {
                    chartObject["visibility"] = new JsonObject
                    {
                        ["hiddenUntilUnlocked"] = c.HiddenUntilUnlocked,
                        ["hiddenUntil"] = c.HiddenUntil,
                    };
                }
                charts[diff] = chartObject;
            }
            entries.Add(new JsonObject
            {
                ["source"] = "cheeseburger_db_studio",
                ["song"] = new JsonObject
                {
                    ["id"] = song.Id,
                    ["title"] = song.Title,
                    ["title_aliases"] = ToJsonArray(song.TitleAliases),
                    ["artist"] = song.Artist,
                    ["pack"] = song.Pack,
                    ["bpm"] = song.Bpm,
                    ["length"] = song.Length,
                    ["side"] = song.Side,
                    ["artwork"] = song.Artwork,
                    ["added_version"] = song.AddedVersion,
                },
                ["charts"] = charts,
                ["_meta"] = new JsonObject
                {
                    ["source_url"] = song.SourceUrl,
                    ["sources"] = new JsonArray(song.Sources.OrderBy(x => x).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                },
            });
        }
        return new JsonObject
        {
            ["format"] = "arcaea_tracker_database",
            ["schema_version"] = 2,
            ["source_format"] = "cheeseburger_db_studio",
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["entries"] = entries,
        };
    }

    private static JsonObject? BuildClassification(ChartInfo chart, string difficulty)
    {
        var ratingClass = chart.RatingClass;
        var ratingClassAlias = chart.RatingClassAlias;
        var bydType = chart.BydType;
        var source = chart.ClassificationSource;
        var inferred = false;

        if (ratingClass is null)
        {
            var semantic = ClassificationFromDifficulty(difficulty);
            if (semantic is null) return null;
            ratingClass = semantic.Value.RatingClass;
            ratingClassAlias = semantic.Value.RatingClassAlias;
            bydType = semantic.Value.BydType;
            source = "inferred-semantic";
            inferred = true;
        }

        if (ratingClass == 3 && bydType is null)
        {
            bydType = ratingClassAlias == 1 || (inferred && difficulty.Equals("INS", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        }

        return new JsonObject
        {
            ["ratingClass"] = ratingClass,
            ["ratingClassAlias"] = ratingClassAlias,
            ["bydType"] = bydType,
            ["source"] = source ?? "database-v2",
        };
    }

    private static void ReadClassification(JsonObject source, ChartInfo chart, string difficulty)
    {
        var nested = source["classification"] as JsonObject;
        var directRatingClass = Integer(source["ratingClass"]);
        var ratingClass = Integer(nested?["ratingClass"]) ?? directRatingClass;
        var ratingClassAlias = Integer(nested?["ratingClassAlias"]) ?? Integer(source["ratingClassAlias"]);
        var bydType = Integer(nested?["bydType"]) ?? Integer(source["bydType"]);
        var classificationSource = Str(nested, "source");

        if (ratingClass is null)
        {
            var inferred = ClassificationFromDifficulty(difficulty);
            if (inferred is not null)
            {
                ratingClass = inferred.Value.RatingClass;
                ratingClassAlias = inferred.Value.RatingClassAlias;
                bydType = inferred.Value.BydType;
                classificationSource = "inferred-semantic";
            }
        }
        else
        {
            if (ratingClass == 3 && bydType is null) bydType = ratingClassAlias == 1 ? 1 : 0;
            classificationSource ??= nested is not null ? "database-v2" : "songlist-direct";
        }

        chart.RatingClass = ratingClass;
        chart.RatingClassAlias = ratingClassAlias;
        chart.BydType = bydType;
        chart.ClassificationSource = classificationSource;
    }

    private static void ReadVisibility(JsonObject source, ChartInfo chart)
    {
        var nested = source["visibility"] as JsonObject;
        chart.HiddenUntilUnlocked =
            NullableBoolean(nested?["hiddenUntilUnlocked"]) ??
            NullableBoolean(nested?["hidden_until_unlocked"]) ??
            NullableBoolean(source["hiddenUntilUnlocked"]) ??
            NullableBoolean(source["hidden_until_unlocked"]);
        chart.HiddenUntil =
            Str(nested, "hiddenUntil") ??
            Str(nested, "hidden_until") ??
            Str(source, "hiddenUntil") ??
            Str(source, "hidden_until");
    }

    private static (int RatingClass, int? RatingClassAlias, int? BydType)? ClassificationFromDifficulty(string difficulty)
        => difficulty.Trim().ToUpperInvariant() switch
        {
            "PST" or "PAST" => (0, null, null),
            "PRS" or "PRESENT" => (1, null, null),
            "FTR" or "FUTURE" => (2, null, null),
            "BYD" or "BEYOND" => (3, null, 0),
            "ETR" or "ETERNAL" => (4, null, null),
            "INS" or "INSCRIBED" => (3, 1, 1),
            _ => null,
        };

    public static void SelfTest()
    {
        var hikariObj = JsonNode.Parse("""
        {
          "id":"hikari",
          "title_localized":{"ja":"光","en":"Hikari"},
          "artist":"THB",
          "difficulties":[{"ratingClass":0,"rating":2},{"ratingClass":1,"rating":6},{"ratingClass":2,"rating":8}]
        }
        """)!.AsObject();
        var hikari = ParseSonglistSong(hikariObj, "songlist");
        if (!hikari.MatchesTitle("光")) throw new InvalidDataException("Localized title alias self-test failed.");

        var wikiHikari = ParseNormalizedEntry(JsonNode.Parse("""
        {
          "source":"arcaea_wikiwiki_jp",
          "song":{"title":"光","artist":"THB"},
          "charts":{"FTR":{"level":"8","constant":8.1,"notes":684}}
        }
        """)!.AsObject(), "wiki");
        var mergedHikari = Merge(new[]
        {
            new SourceDocument { Name = "songlist", Kind = "songlist", Songs = new() { hikari } },
            new SourceDocument { Name = "wiki", Kind = "wiki", Songs = new() { wikiHikari } },
        });
        if (mergedHikari.Count != 1 || mergedHikari[0].Id != "hikari" || mergedHikari[0].Title != "Hikari")
            throw new InvalidDataException("Localized merge self-test failed.");
        if (mergedHikari[0].Charts["FTR"].ClassificationSource != "songlist")
            throw new InvalidDataException("Official chart classification was overwritten by wiki enrichment.");

        var axiumObj = JsonNode.Parse("""
        {
          "id":"axiumcrisis",
          "title_localized":{"en":"Axium Crisis"},
          "artist":"ak+q",
          "bpm":"170",
          "version":"1.5",
          "difficulties":[
            {"ratingClass":2,"rating":10},
            {"ratingClass":3,"title_localized":{"en":"Axium Divergence"},"artist":"ak+q (lowiro)","bpm":"170?","bpm_base":180,"audioOverride":true,"jacketOverride":true,"bg":"axiumcrisis3","rating":11,"version":"6.13"}
          ]
        }
        """)!.AsObject();
        var axium = ParseSonglistSong(axiumObj, "songlist");
        if (!axium.HasBeyondVariant || axium.DisplayTitle != "Axium Crisis | Axium Divergence")
            throw new InvalidDataException("Beyond variant self-test failed.");
        if (!axium.MatchesTitle("Axium Divergence") || axium.BeyondVariant?.VariantBpm != "170?")
            throw new InvalidDataException("Beyond variant metadata self-test failed.");
        if (axium.Charts["BYD"].RatingClass != 3 || axium.Charts["BYD"].BydType != 0)
            throw new InvalidDataException("Beyond classification self-test failed.");

        var inscribedObj = JsonNode.Parse("""
        {
          "id":"testinscribed",
          "title_localized":{"en":"Test Inscribed"},
          "artist":"test",
          "difficulties":[
            {"ratingClass":2,"rating":10},
            {"ratingClass":3,"ratingClassAlias":1,"rating":11,"hiddenUntilUnlocked":true}
          ]
        }
        """)!.AsObject();
        var inscribed = ParseSonglistSong(inscribedObj, "songlist");
        if (!inscribed.Charts.TryGetValue("INS", out var ins) || ins.RatingClass != 3 || ins.RatingClassAlias != 1 || ins.BydType != 1)
            throw new InvalidDataException("Inscribed classification self-test failed.");

        var exported = BuildExportDocument(new[] { inscribed });
        if (exported["format"]?.GetValue<string>() != "arcaea_tracker_database" || exported["schema_version"]?.GetValue<int>() != 2)
            throw new InvalidDataException("Schema-v2 export header self-test failed.");
        var exportedEntry = exported["entries"]!.AsArray()[0]!.AsObject();
        var exportedIns = exportedEntry["charts"]!["INS"]!.AsObject();
        var exportedClass = exportedIns["classification"]!.AsObject();
        if (exportedClass["ratingClass"]?.GetValue<int>() != 3 || exportedClass["ratingClassAlias"]?.GetValue<int>() != 1 || exportedClass["bydType"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Schema-v2 Inscribed export self-test failed.");
        if (exportedIns["visibility"]?["hiddenUntilUnlocked"]?.GetValue<bool>() != true)
            throw new InvalidDataException("Schema-v2 visibility export self-test failed.");

        var roundTrip = ParseNormalizedEntry(exportedEntry, "v2");
        if (roundTrip.Charts["INS"].BydType != 1 || roundTrip.Charts["INS"].ClassificationSource != "songlist")
            throw new InvalidDataException("Schema-v2 classification round-trip self-test failed.");
    }

    private static string? Str(JsonObject? o, string name) => o?[name]?.GetValue<string?>();

    private static string? Localized(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        foreach (var key in new[] { "en", "ja", "ko" }) if (o[key]?.GetValue<string?>() is { Length: > 0 } s) return s;
        return o.Select(x => x.Value?.GetValue<string?>()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static void AddLocalizedValues(HashSet<string> target, JsonNode? node)
    {
        if (node is not JsonObject obj) return;
        foreach (var value in obj.Select(x => x.Value?.GetValue<string?>()).Where(x => !string.IsNullOrWhiteSpace(x)))
            target.Add(value!);
    }

    private static void AddSearchValues(HashSet<string> target, JsonNode? node)
    {
        if (node is not JsonObject obj) return;
        foreach (var array in obj.Select(x => x.Value).OfType<JsonArray>())
            foreach (var value in array.Select(x => x?.GetValue<string?>()).Where(x => !string.IsNullOrWhiteSpace(x)))
                target.Add(value!);
    }

    private static void AddStringArray(HashSet<string> target, JsonNode? node)
    {
        if (node is not JsonArray array) return;
        foreach (var value in array.Select(x => x?.GetValue<string?>()).Where(x => !string.IsNullOrWhiteSpace(x)))
            target.Add(value!);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
        => new(values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    private static string? Scalar(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<int>(out var i)) return i.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        }
        return n.ToJsonString();
    }

    private static double? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    private static int? Integer(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out i)) return i;
        return null;
    }

    private static bool Boolean(JsonNode? n) => NullableBoolean(n) ?? false;

    private static bool? NullableBoolean(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        return null;
    }
}
