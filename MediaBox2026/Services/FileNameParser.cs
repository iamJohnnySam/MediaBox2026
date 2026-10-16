using System.Globalization;
using System.Text.RegularExpressions;
using MediaBox2026.Models;

namespace MediaBox2026.Services;

public static partial class FileNameParser
{
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".flv", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".ts", ".vob", ".3gp", ".ogv"
    };

    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".ass", ".ssa", ".vtt", ".idx"
    };

    private static readonly string[] RemoveWords =
    [
        "EXTENDED", "REMASTERED", "REPACK", "BLURAY", "BRRip", "DVDRip",
        "HDTV", "WEB-DL", "WEBRip", "WEB", "x264", "x265", "HEVC",
        "H264", "H265", "AAC", "AC3", "DTS", "PROPER", "INTERNAL",
        "REAL", "Dir Cut", "IMAX", "EDITION", "Ep.", "XviD", "YIFY",
        "RARBG", "EVO", "FGT", "SPARKS", "GECKOS", "www.torrenting.com",
		"www.UIndex.org - ", "www.UIndex.org", ".org", "www.", "torrenting.com", 
        "UIndex.org"
	];

    /// <summary>Widest episode span accepted from a paired filename before it reads as a mis-parse.</summary>
    private const int MaxEpisodeSpan = 10;

    public static ParsedMediaInfo Parse(string fileName)
    {
        var info = new ParsedMediaInfo { OriginalFileName = fileName };
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        var qualityMatch = QualityRegex().Match(baseName);
        if (qualityMatch.Success)
            info.Quality = qualityMatch.Value.ToLowerInvariant();

        Regex[] patterns =
        [
            SeasonEpisodeRegex1(), // S01E01
            SeasonEpisodeRegex2(), // S01 E01
            SeasonEpisodeRegex3(), // S01x01
            SeasonEpisodeRegex4(), // 01x01
            SeasonEpisodeRegex5(), // 1x01
            SeasonEpisodeRegex6(), // Season 1 Episode 1
        ];

        foreach (var pattern in patterns)
        {
            var match = pattern.Match(baseName);
            if (match.Success && match.Groups.Count >= 3)
            {
                info.Season = int.Parse(match.Groups[1].Value);
                info.Episode = int.Parse(match.Groups[2].Value);

                // Only SeasonEpisodeRegex1 carries the optional second number ("S13E01E02"):
                // one file, two aired episodes. Keep the span — see ParsedMediaInfo.EpisodeEnd.
                var endGroup = match.Groups.Count > 3 && match.Groups[3].Success ? match.Groups[3]
                             : match.Groups.Count > 4 && match.Groups[4].Success ? match.Groups[4]
                             : null;
                if (endGroup is not null)
                {
                    var end = int.Parse(endGroup.Value);
                    // A pair or a short run is real; a wide span is a mis-parse, not a multi-episode file.
                    if (end > info.Episode && end - info.Episode <= MaxEpisodeSpan)
                        info.EpisodeEnd = end;
                }

                baseName = baseName[..match.Index];
                break;
            }
        }

        if (!info.Season.HasValue)
        {
            var epMatch = EpisodeOnlyRegex().Match(baseName);
            if (!epMatch.Success)
                epMatch = BareEpisodeRegex().Match(baseName);

            if (epMatch.Success)
            {
                info.Season = 1;
                info.Episode = int.Parse(epMatch.Groups[1].Value);

                if (epMatch.Groups.Count > 2 && epMatch.Groups[2].Success)
                {
                    var end = int.Parse(epMatch.Groups[2].Value);
                    if (end > info.Episode && end - info.Episode <= MaxEpisodeSpan)
                        info.EpisodeEnd = end;
                }

                baseName = baseName[..epMatch.Index];
            }
        }

        // Torrent filenames follow the pattern: Title.Year.Quality.Codec.Source-Group
        // Everything from the year onwards is technical metadata — truncate there.
        var yearMatch = YearRegex().Match(" " + baseName + " ");
        if (yearMatch.Success)
        {
            info.Year = int.Parse(yearMatch.Groups[1].Value);
            // offset accounts for the leading space prepended before matching
            var offset = yearMatch.Index - 1;
            if (offset >= 0 && offset < baseName.Length)
                baseName = baseName[..offset];
        }
        else if (qualityMatch.Success)
        {
            // No year found — truncate at the first quality token (e.g. 720p, 1080p)
            // so codec/source tags after it don't pollute the title.
            // Re-match against the current baseName since it may have been shortened by season/episode stripping.
            var currentQualityMatch = QualityRegex().Match(baseName);
            if (currentQualityMatch.Success && currentQualityMatch.Index > 0)
                baseName = baseName[..currentQualityMatch.Index];
        }

        info.CleanName = CleanName(baseName);
        return info;
    }

    public static string CleanName(string name)
    {
        foreach (var word in RemoveWords)
            name = Regex.Replace(name, @"\b" + Regex.Escape(word) + @"\b", " ", RegexOptions.IgnoreCase);

        name = name
            .Replace(".", " ").Replace("+", " ").Replace("!", "")
            .Replace("(", "").Replace(")", "").Replace("'", "")
            .Replace("-", " ").Replace("\"", "").Replace("?", "")
            .Replace("[", "").Replace("]", "").Replace("_", " ");

        name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        name = CollapseWhitespaceRegex().Replace(name, " ").Trim();
        return name;
    }

    public static (string Name, int? Year) ParseFolderName(string folderName)
    {
        var matchParen = FolderNameYearParenRegex().Match(folderName);
        if (matchParen.Success)
            return (matchParen.Groups[1].Value.Trim(), int.Parse(matchParen.Groups[2].Value));

        var matchBracket = FolderNameYearBracketRegex().Match(folderName);
        if (matchBracket.Success)
            return (matchBracket.Groups[1].Value.Trim(), int.Parse(matchBracket.Groups[2].Value));

        var match = FolderNameYearRegex().Match(folderName);
        if (match.Success)
            return (match.Groups[1].Value.Trim(), int.Parse(match.Groups[2].Value));
        return (folderName.Trim(), null);
    }

    public static bool IsMediaFile(string fileName)
        => MediaExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsSubtitleFile(string fileName)
        => SubtitleExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsQualityAcceptable(string? quality)
    {
        if (string.IsNullOrEmpty(quality)) return true;
        // "3D" is a YTS quality string, not a resolution — a bare \d+ read it as 3, passed the
        // <=720 test, and auto-downloaded a 1.49 GB half-SBS rip ahead of the 714 MB 720p.
        // Never the standard release, so it fails the standard outright.
        if (quality.Contains("3D", StringComparison.OrdinalIgnoreCase)) return false;
        var match = Regex.Match(quality, @"(\d+)p", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var res))
            return res <= 720;
        return true;
    }

    /// <summary>
    /// Returns true when the quality tag represents a resolution strictly above 1080p (e.g. 2160p / 4K).
    /// </summary>
    public static bool IsAbove1080p(string? quality)
    {
        if (string.IsNullOrEmpty(quality)) return false;
        // \d+p for the same reason as IsQualityAcceptable — these two must read a tag the same way.
        var match = Regex.Match(quality, @"(\d+)p", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var res))
            return res > 1080;
        return false;
    }

    public static string? DetectQuality(string text)
    {
        var match = QualityRegex().Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    public enum YearFormat
    {
        Parentheses,  // Show Name (2023)
        Brackets,     // Show Name [2023]
        Space         // Show Name 2023
    }

    public static string BuildFolderName(string name, int? year, YearFormat format = YearFormat.Parentheses)
    {
        if (!year.HasValue) return name;

        return format switch
        {
            YearFormat.Parentheses => $"{name} ({year})",
            YearFormat.Brackets => $"{name} [{year}]",
            YearFormat.Space => $"{name} {year}",
            _ => $"{name} ({year})"
        };
    }

    // "X-Men 97" and "X Men 97" are the same show. Splitting on spaces alone made the hyphen
    // part of the token, scored them 0.25, and the organizer created a second folder — so fold
    // punctuation to spaces before tokenizing. Apostrophes are dropped, not spaced, or
    // "Marvel's" would gain a stray "s" token that "Marvels" doesn't have.
    private static HashSet<string> Tokenize(string s) =>
        PunctuationRegex().Replace(s.ToLowerInvariant().Replace("'", "").Replace("’", ""), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

    /// <summary>Not identity: a leading article is the same work either way.</summary>
    private static readonly HashSet<string> TitleArticles = ["the", "a", "an"];

    /// <summary>
    /// The content words <paramref name="candidate"/> adds to <paramref name="wanted"/>, ignoring
    /// articles. Empty means the candidate names the same work.
    ///
    /// <see cref="FuzzyMatch"/> scores containment 0.90 in <i>either</i> direction, which is right
    /// when filing a release against a library folder ("Ted" vs "Ted (2024)") and wrong when
    /// choosing which release to <b>download</b>: a title that prefixes the wanted one with content
    /// words is a different work. "Just Seen It The Twilight Saga- BReaking Dawn Part 1" is an
    /// episode of a review show, not the film, and it scored 0.90 against the film's own name.
    /// Articles stay excluded so a watchlist entry for "Good Dinosaur" still accepts YTS's
    /// "The Good Dinosaur".
    /// </summary>
    public static HashSet<string> ExtraTitleWords(string candidate, string wanted)
    {
        var extra = Tokenize(candidate);
        extra.ExceptWith(Tokenize(wanted));
        extra.ExceptWith(TitleArticles);
        return extra;
    }

    public static double FuzzyMatch(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        if (setA.SetEquals(setB)) return 1.0;

        // whole-word containment (e.g. "Ted" vs "Ted (2024)"), not raw substring —
        // raw substring let "Ted" match inside "Superman The Anima[ted] Series"
        if (setA.IsSubsetOf(setB) || setB.IsSubsetOf(setA)) return 0.9;

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return (double)intersection / union;
    }

    /// <summary>
    /// The words of the episode title(s) a release names — the text between the SxxExx tag and
    /// the quality tag. Which of them actually carry identity is decided by the caller against
    /// the rest of the show (see MediaCatalogService), not by a word list here: "pups" and
    /// "save" are filler in PAW Patrol and "torrentgalaxy" is filler in Reacher, and no fixed
    /// list would know either. Single digits are kept — they are what separates "Pt 1" from
    /// "Pt 2". Empty when the name carries no SxxExx tag to measure from.
    /// </summary>
    public static HashSet<string> EpisodeTitleTokens(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        var tag = SeasonEpisodeRegex1().Match(baseName);
        if (!tag.Success) return [];

        var title = baseName[(tag.Index + tag.Length)..];

        // Everything from the quality tag on is technical metadata, not title.
        var quality = QualityRegex().Match(title);
        if (quality.Success) title = title[..quality.Index];

        return PunctuationRegex().Split(title)
            .Where(w => w.Length > 0)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\b(\d{3,4})p\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    // Two optional closing groups for a paired release.
    //
    // Group 3 is the E-prefixed form: "S13E01E02", "S12E04-E05", "S04E01+E02".
    //
    // Group 4 is the bare form, "S10E17-18" and "S05E21 & 22", and it demands an explicit "-"
    // or "&" because whitespace alone is not a range marker — an episode whose TITLE opens with
    // a number ("S05E04 6,741", "S03E14 20,000 Leaks Under the City") would otherwise swallow it
    // and claim episodes it does not have, which silently suppresses their download. The closing
    // number also may not be followed by a digit, letter or comma, ruling out "-720p", "-4K",
    // "-2CH" and thousands separators.
    [GeneratedRegex(@"S(\d{1,2})E(\d{1,3})(?:[-._+\s]*E(\d{1,3})|\s*[-&]\s*(\d{1,2})(?![\dA-Za-z,]))?", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex1();

    [GeneratedRegex(@"S(\d{1,2})\s+E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex2();

    // Case-sensitive on purpose. An uppercase "S09X01" is how the shorts and webisodes in this
    // library are named ("The Office S09X01 Retrospective"), and reading it as S09E01 made the
    // extra claim a real episode — "New Guys" then looked present and was never downloaded.
    // Lowercase "S01x01" stays the season-episode separator it has always been.
    [GeneratedRegex(@"S(\d{1,2})x(\d{1,3})")]
    private static partial Regex SeasonEpisodeRegex3();

    [GeneratedRegex(@"(\d{1,2})x(\d{2,3})")]
    private static partial Regex SeasonEpisodeRegex4();

    [GeneratedRegex(@"(\d)x(\d{2,3})")]
    private static partial Regex SeasonEpisodeRegex5();

    [GeneratedRegex(@"Season\s*(\d{1,2})\s*Episode\s*(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex6();

    // "PLANET SHEEN - Ep. 02-03 - ..." is a paired file in the same sense as "S13E01E02",
    // so the closing number is captured rather than matched and discarded.
    [GeneratedRegex(@"Ep\.?\s*(\d{1,3})(?:\s*[-–]\s*(\d{1,3})(?![\dA-Za-z,]))?", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnlyRegex();

    // Season-less shows numbered "E01" with no "p" ("Mr. Bean E01 Mr. Bean", "Shaun the
    // Sheep Championsheeps E01 The Relay"). Tried only after everything else has failed, and
    // the boundaries keep it off mid-word matches like the "e7" in "Se7en".
    [GeneratedRegex(@"(?<![A-Za-z0-9])E(\d{1,3})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex BareEpisodeRegex();

    [GeneratedRegex(@"[\s\.\(\[]((?:19|20)\d{2})[\s\.\)\]]")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"^(.+?)\s+((?:19|20)\d{2})$")]
    private static partial Regex FolderNameYearRegex();

    [GeneratedRegex(@"^(.+?)\s*\(((?:19|20)\d{2})\)$")]
    private static partial Regex FolderNameYearParenRegex();

    [GeneratedRegex(@"^(.+?)\s*\[((?:19|20)\d{2})\]$")]
    private static partial Regex FolderNameYearBracketRegex();
}
