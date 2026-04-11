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
                baseName = baseName[..match.Index];
                break;
            }
        }

        if (!info.Season.HasValue)
        {
            var epMatch = EpisodeOnlyRegex().Match(baseName);
            if (epMatch.Success)
            {
                info.Season = 1;
                info.Episode = int.Parse(epMatch.Groups[1].Value);
                baseName = baseName[..epMatch.Index];
            }
        }

        var yearMatch = YearRegex().Match(" " + baseName + " ");
        if (yearMatch.Success)
        {
            info.Year = int.Parse(yearMatch.Groups[1].Value);
            var offset = yearMatch.Index - 1;
            if (offset >= 0 && offset < baseName.Length)
            {
                var len = Math.Min(yearMatch.Length, baseName.Length - offset);
                baseName = baseName[..offset] + baseName[(offset + len)..];
            }
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
        var match = Regex.Match(quality, @"(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var res))
            return res <= 720;
        return true;
    }

    public static string? DetectQuality(string text)
    {
        var match = QualityRegex().Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    public static string BuildFolderName(string name, int? year)
        => year.HasValue ? $"{name} ({year})" : name;

    public static double FuzzyMatch(string a, string b)
    {
        a = a.ToLowerInvariant().Trim();
        b = b.ToLowerInvariant().Trim();
        if (a == b) return 1.0;
        if (a.Contains(b) || b.Contains(a)) return 0.9;

        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return (double)intersection / union;
    }

    [GeneratedRegex(@"\b(\d{3,4})p\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex1();

    [GeneratedRegex(@"S(\d{1,2})\s+E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex2();

    [GeneratedRegex(@"S(\d{1,2})x(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex3();

    [GeneratedRegex(@"(\d{1,2})x(\d{2,3})")]
    private static partial Regex SeasonEpisodeRegex4();

    [GeneratedRegex(@"(\d)x(\d{2,3})")]
    private static partial Regex SeasonEpisodeRegex5();

    [GeneratedRegex(@"Season\s*(\d{1,2})\s*Episode\s*(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex6();

    [GeneratedRegex(@"Ep\.?\s*(\d{1,3})(?:\s*[-–]\s*\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnlyRegex();

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
