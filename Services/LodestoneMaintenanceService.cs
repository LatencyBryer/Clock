using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// Lodestone parsing changes from time to time, so the checks below stay defensive.


namespace Clock.Services;

public sealed class LodestoneMaintenanceInfo
{
    public LodestoneMaintenanceInfo(
        string title,
        string url,
        string localStartText,
        string timeZoneText,
        DateTime startUtc,
        DateTime? endUtc)
    {
        Title = title;
        Url = url;
        LocalStartText = localStartText;
        TimeZoneText = timeZoneText;
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public string Title { get; }
    public string Url { get; }
    public string LocalStartText { get; }
    public string TimeZoneText { get; }
    public DateTime StartUtc { get; }
    public DateTime? EndUtc { get; }

    public string BuildSummary()
    {
        return $"{Title} - {LocalStartText} {TimeZoneText}";
    }
}

public sealed class LodestoneMaintenanceService : IDisposable
{
    private const string EnglishUsMaintenanceNewsUrl = "https://na.finalfantasyxiv.com/lodestone/news/category/2";
    private const string EnglishUkMaintenanceNewsUrl = "https://eu.finalfantasyxiv.com/lodestone/news/category/2";
    private const string JapaneseMaintenanceNewsUrl = "https://jp.finalfantasyxiv.com/lodestone/news/category/2";
    private const string GermanMaintenanceNewsUrl = "https://de.finalfantasyxiv.com/lodestone/news/category/2";
    private const string FrenchMaintenanceNewsUrl = "https://fr.finalfantasyxiv.com/lodestone/news/category/2";

    private const string MonthNamePattern = @"[\p{L}\.]+";
    private const string TimeSeparatorPattern = @"(?:to|until|bis|au|à|a|～|~|-|–|—)";
    private const string ZonePattern = @"[\(（](?<zone>[A-Z]{2,5})[\)）]";

    private static readonly Regex DetailLinkRegex = new(
        "href=\\\"(?<path>/lodestone/news/detail/[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    private static readonly Regex EnglishMonthWindowRegex = new(
        $@"(?<startMonth>{MonthNamePattern})\s+(?<startDay>\d{{1,2}}),?\s+(?<startYear>\d{{4}})\s+" +
        $@"(?<startHour>\d{{1,2}}):(?<startMinute>\d{{2}})\s*(?<startPeriod>a\.m\.|p\.m\.|am|pm)?\s+{TimeSeparatorPattern}\s+" +
        $@"(?:(?<endMonth>{MonthNamePattern})\s+(?<endDay>\d{{1,2}}),?\s+(?<endYear>\d{{4}})\s+)?" +
        $@"(?<endHour>\d{{1,2}}):(?<endMinute>\d{{2}})\s*(?<endPeriod>a\.m\.|p\.m\.|am|pm)?\s*{ZonePattern}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DayMonthWindowRegex = new(
        $@"(?<startDay>\d{{1,2}})\.?\s+(?<startMonth>{MonthNamePattern})\s+(?<startYear>\d{{4}})\s+" +
        $@"(?<startHour>\d{{1,2}}):(?<startMinute>\d{{2}})\s*(?<startPeriod>a\.m\.|p\.m\.|am|pm)?\s+{TimeSeparatorPattern}\s+" +
        $@"(?:(?<endDay>\d{{1,2}})\.?\s+(?<endMonth>{MonthNamePattern})\s+(?<endYear>\d{{4}})\s+)?" +
        $@"(?<endHour>\d{{1,2}}):(?<endMinute>\d{{2}})\s*(?<endPeriod>a\.m\.|p\.m\.|am|pm)?\s*{ZonePattern}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericWindowRegex = new(
        $@"(?<startYear>\d{{4}})[/-](?<startMonth>\d{{1,2}})[/-](?<startDay>\d{{1,2}})\s+" +
        $@"(?<startHour>\d{{1,2}}):(?<startMinute>\d{{2}})\s*(?<startPeriod>a\.m\.|p\.m\.|am|pm)?\s+{TimeSeparatorPattern}\s+" +
        $@"(?:(?<endYear>\d{{4}})[/-](?<endMonth>\d{{1,2}})[/-](?<endDay>\d{{1,2}})\s+)?" +
        $@"(?<endHour>\d{{1,2}}):(?<endMinute>\d{{2}})\s*(?<endPeriod>a\.m\.|p\.m\.|am|pm)?\s*{ZonePattern}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JapaneseWindowRegex = new(
        $@"(?<startYear>\d{{4}})年\s*(?<startMonth>\d{{1,2}})月\s*(?<startDay>\d{{1,2}})日\s*" +
        $@"(?<startHour>\d{{1,2}}):(?<startMinute>\d{{2}})\s+{TimeSeparatorPattern}\s+" +
        $@"(?:(?<endYear>\d{{4}})年\s*(?<endMonth>\d{{1,2}})月\s*(?<endDay>\d{{1,2}})日\s*)?" +
        $@"(?<endHour>\d{{1,2}}):(?<endMinute>\d{{2}})\s*{ZonePattern}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EnglishFromWindowRegex = new(
        $@"\bfrom\s+(?<startMonth>{MonthNamePattern})\s+(?<startDay>\d{{1,2}}),?\s+(?<startYear>\d{{4}})\s+" +
        $@"(?<startHour>\d{{1,2}}):(?<startMinute>\d{{2}})\s*(?<startPeriod>a\.m\.|p\.m\.|am|pm)\s+{TimeSeparatorPattern}\s+" +
        $@"(?:(?<endMonth>{MonthNamePattern})\s+(?<endDay>\d{{1,2}}),?\s+(?<endYear>\d{{4}})\s+)?" +
        $@"(?<endHour>\d{{1,2}}):(?<endMinute>\d{{2}})\s*(?<endPeriod>a\.m\.|p\.m\.|am|pm)\s*{ZonePattern}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient httpClient = new();
    // Keeping setup close to the constructor makes the object lifecycle easier to trace.

    public LodestoneMaintenanceService()
    {
        httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    public async Task<LodestoneMaintenanceInfo?> GetLatestMaintenanceAsync(
        LodestoneMaintenanceLanguage language,
        string? cachedUrl = null,
        DateTime cachedStartUtc = default,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var maintenanceNewsUri = GetMaintenanceNewsUri(language);
        var indexHtml = await httpClient.GetStringAsync(maintenanceNewsUri, cancellationToken).ConfigureAwait(false);
        var detailUrls = ExtractDetailUrls(indexHtml, maintenanceNewsUri);
        if (detailUrls.Count == 0)
            return null;

        var nowUtc = DateTime.UtcNow;
        foreach (var detailUrl in detailUrls)
        {
            var html = await httpClient.GetStringAsync(detailUrl, cancellationToken).ConfigureAwait(false);
            if (!TryParseMaintenancePage(html, detailUrl, language, nowUtc, out var maintenance))
                continue;

            if (!forceRefresh && !string.IsNullOrWhiteSpace(cachedUrl) &&
                string.Equals(cachedUrl, detailUrl, StringComparison.OrdinalIgnoreCase) &&
                cachedStartUtc > nowUtc.AddMinutes(-30))
            {
                return null;
            }

            return maintenance;
        }

        return null;
    }

    public static string GetLanguageName(LodestoneMaintenanceLanguage language)
    {
        return language switch
        {
            LodestoneMaintenanceLanguage.EnglishUk => "English (UK)",
            LodestoneMaintenanceLanguage.Japanese => "Japanese",
            LodestoneMaintenanceLanguage.German => "Deutsch",
            LodestoneMaintenanceLanguage.French => "Français",
            _ => "English (US)"
        };
    }

    private static Uri GetMaintenanceNewsUri(LodestoneMaintenanceLanguage language)
    {
        return new Uri(language switch
        {
            LodestoneMaintenanceLanguage.EnglishUk => EnglishUkMaintenanceNewsUrl,
            LodestoneMaintenanceLanguage.Japanese => JapaneseMaintenanceNewsUrl,
            LodestoneMaintenanceLanguage.German => GermanMaintenanceNewsUrl,
            LodestoneMaintenanceLanguage.French => FrenchMaintenanceNewsUrl,
            _ => EnglishUsMaintenanceNewsUrl
        });
    }

    private static List<string> ExtractDetailUrls(string html, Uri maintenanceNewsUri)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in DetailLinkRegex.Matches(html))
        {
            var path = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var url = new Uri(maintenanceNewsUri, path).ToString();
            if (seen.Add(url))
                urls.Add(url);
        }

        return urls;
    }

    private static bool TryParseMaintenancePage(
        string html,
        string url,
        LodestoneMaintenanceLanguage language,
        DateTime nowUtc,
        out LodestoneMaintenanceInfo maintenance)
    {
        maintenance = null!;

        var text = NormalizeHtmlText(html);
        var title = ExtractTitle(html);
        if (ShouldSkipTitleOrText(title, text) || !LooksLikeGameMaintenanceNotice(title, text, language))
            return false;

        var match = FindMaintenanceWindowMatch(text, language);
        if (!match.Success)
            return false;

        var zoneText = match.Groups["zone"].Value.ToUpperInvariant();
        if (!TryGetLodestoneTimeZone(zoneText, out var timeZone, out var fixedZoneOffset))
            return false;

        if (!TryBuildLocalDateTime(match, "start", out var localStart))
            return false;

        if (!TryBuildLocalDateTime(match, "end", out var localEnd))
            return false;

        if (!match.Groups["endMonth"].Success || !match.Groups["endDay"].Success || !match.Groups["endYear"].Success)
        {
            localEnd = new DateTime(localStart.Year, localStart.Month, localStart.Day, localEnd.Hour, localEnd.Minute, 0);
            if (localEnd <= localStart)
                localEnd = localEnd.AddDays(1);
        }

        var startUtc = ConvertLodestoneLocalToUtc(localStart, timeZone, fixedZoneOffset);
        var endUtc = ConvertLodestoneLocalToUtc(localEnd, timeZone, fixedZoneOffset);

        if (endUtc < nowUtc.AddMinutes(-30))
            return false;

        maintenance = new LodestoneMaintenanceInfo(
            title,
            url,
            FormatLodestoneDisplayTime(localStart),
            zoneText,
            startUtc,
            endUtc);

        return true;
    }


    private static bool LooksLikeGameMaintenanceNotice(string title, string text, LodestoneMaintenanceLanguage language)
    {
        var titleKey = RemoveDiacritics(title).ToLowerInvariant();
        if (titleKey.Contains("lodestone maintenance") ||
            titleKey.Contains("companion app maintenance") ||
            titleKey.Contains("online store maintenance") ||
            titleKey.Contains("mog station maintenance"))
        {
            return false;
        }

        var affected = ExtractAffectedServiceSection(text, language);
        if (string.IsNullOrWhiteSpace(affected))
            return titleKey.Contains("all worlds") || title.Contains("全ワールド", StringComparison.Ordinal);

        var key = RemoveDiacritics(affected).ToLowerInvariant();
        return key.Contains("final fantasy xiv") ||
               key.Contains("all worlds") ||
               affected.Contains("ファイナルファンタジーXIV", StringComparison.Ordinal) ||
               affected.Contains("ファイナルファンタジー14", StringComparison.Ordinal);
    }

    private static string ExtractAffectedServiceSection(string text, LodestoneMaintenanceLanguage language)
    {
        var markers = language switch
        {
            LodestoneMaintenanceLanguage.Japanese => new[] { "[対象サービス]", "対象サービス" },
            LodestoneMaintenanceLanguage.German => new[] { "[Betroffener Dienst]", "Betroffener Dienst" },
            LodestoneMaintenanceLanguage.French => new[] { "[Service concerné]", "Service concerné" },
            _ => new[] { "[Affected Service]", "Affected Service" }
        };

        var start = -1;
        foreach (var marker in markers)
        {
            start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start += marker.Length;
                break;
            }
        }

        if (start < 0)
            return string.Empty;

        var nextMarkers = new[]
        {
            "[Details]", "Details", "[Date & Time]", "Date & Time", "[日時]", "日時",
            "[詳細]", "詳細", "[Betroffener Dienst]", "[Service concerné]"
        };

        var end = -1;
        foreach (var marker in nextMarkers)
        {
            var index = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index > start && (end < 0 || index < end))
                end = index;
        }

        var length = end > start ? end - start : Math.Min(500, text.Length - start);
        return text.Substring(start, length).Trim();
    }

    private static Match FindMaintenanceWindowMatch(string text, LodestoneMaintenanceLanguage language)
    {
        var dateSection = ExtractDateSection(text, language);

        if (language is LodestoneMaintenanceLanguage.EnglishUs or LodestoneMaintenanceLanguage.EnglishUk)
        {
            var fromMatch = EnglishFromWindowRegex.Match(dateSection);
            if (fromMatch.Success)
                return fromMatch;
        }

        var match = EnglishMonthWindowRegex.Match(dateSection);
        if (match.Success)
            return match;

        match = DayMonthWindowRegex.Match(dateSection);
        if (match.Success)
            return match;

        match = NumericWindowRegex.Match(dateSection);
        if (match.Success)
            return match;

        match = JapaneseWindowRegex.Match(dateSection);
        if (match.Success)
            return match;

        return language is LodestoneMaintenanceLanguage.EnglishUs or LodestoneMaintenanceLanguage.EnglishUk
            ? EnglishFromWindowRegex.Match(text)
            : Match.Empty;
    }

    private static bool ShouldSkipTitleOrText(string title, string text)
    {
        var combined = RemoveDiacritics($"{title} {text}").ToLowerInvariant();
        return combined.Contains("follow-up")
            || combined.Contains("follow up")
            || combined.Contains("cancellation")
            || combined.Contains("cancelled")
            || combined.Contains("canceled")
            || combined.Contains("annulation")
            || combined.Contains("abbruch")
            || combined.Contains("取消")
            || combined.Contains("中止");
    }

    private static string NormalizeHtmlText(string html)
    {
        var withLineBreaks = Regex.Replace(html, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        withLineBreaks = Regex.Replace(withLineBreaks, @"</\s*(p|div|li|h1|h2|h3|dt|dd)\s*>", "\n", RegexOptions.IgnoreCase);
        var withoutTags = HtmlTagRegex.Replace(withLineBreaks, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static string ExtractTitle(string html)
    {
        var titleMatch = Regex.Match(
            html,
            @"<title>\s*(?<title>.*?)\s*\|\s*FINAL FANTASY XIV",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!titleMatch.Success)
            return "Lodestone Maintenance";

        var decoded = WebUtility.HtmlDecode(titleMatch.Groups["title"].Value);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static string ExtractDateSection(string text, LodestoneMaintenanceLanguage language)
    {
        var markers = language switch
        {
            LodestoneMaintenanceLanguage.Japanese => new[] { "[日時]", "日時" },
            LodestoneMaintenanceLanguage.German => new[] { "[Datum & Uhrzeit]", "Datum & Uhrzeit", "Datum und Uhrzeit" },
            LodestoneMaintenanceLanguage.French => new[] { "[Date et heures]", "[Date et heure]", "Date et heures", "Date et heure" },
            _ => new[] { "[Date & Time]", "Date & Time", "Date and Time" }
        };

        var start = -1;
        foreach (var marker in markers)
        {
            start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
                break;
        }

        if (start < 0)
            return text;

        var endMarkers = new[]
        {
            "[Affected Service]",
            "Affected Service",
            "[Betroffener Dienst]",
            "Betroffener Dienst",
            "[Service concerné]",
            "Service concerné",
            "[対象サービス]",
            "対象サービス"
        };

        var end = -1;
        foreach (var marker in endMarkers)
        {
            var index = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index > start && (end < 0 || index < end))
                end = index;
        }

        return end > start
            ? text[start..end]
            : text[start..];
    }


    private static string FormatLodestoneDisplayTime(DateTime localTime)
    {
        var period = localTime.Hour < 12 ? "A.M." : "P.M.";
        var hour = localTime.Hour % 12;
        if (hour == 0)
            hour = 12;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd} {1:00}:{2:00} {3}",
            localTime,
            hour,
            localTime.Minute,
            period);
    }

    private static DateTime ConvertLodestoneLocalToUtc(DateTime localTime, TimeZoneInfo timeZone, TimeSpan? fixedZoneOffset)
    {
        if (fixedZoneOffset.HasValue)
            return new DateTimeOffset(localTime, fixedZoneOffset.Value).UtcDateTime;

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), timeZone);
    }

    private static bool TryBuildLocalDateTime(Match match, string prefix, out DateTime dateTime)
    {
        dateTime = DateTime.MinValue;

        var monthGroup = match.Groups[$"{prefix}Month"];
        var dayGroup = match.Groups[$"{prefix}Day"];
        var yearGroup = match.Groups[$"{prefix}Year"];

        if (!monthGroup.Success || !dayGroup.Success || !yearGroup.Success)
        {
            monthGroup = match.Groups["startMonth"];
            dayGroup = match.Groups["startDay"];
            yearGroup = match.Groups["startYear"];
        }

        if (!TryGetMonth(monthGroup.Value, out var month) ||
            !int.TryParse(dayGroup.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(yearGroup.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(match.Groups[$"{prefix}Hour"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(match.Groups[$"{prefix}Minute"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            return false;
        }

        var period = match.Groups[$"{prefix}Period"].Value.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(period))
        {
            hour %= 12;
            if (period.StartsWith("p", StringComparison.OrdinalIgnoreCase))
                hour += 12;
        }

        if (month < 1 || month > 12)
            return false;

        var maxDay = DateTime.DaysInMonth(year, month);
        if (day < 1 || day > maxDay || hour < 0 || hour > 23 || minute < 0 || minute > 59)
            return false;

        dateTime = new DateTime(year, month, day, hour, minute, 0);
        return true;
    }

    private static bool TryGetMonth(string text, out int month)
    {
        month = 0;
        var clean = RemoveDiacritics(text).Trim().TrimEnd('.').ToLowerInvariant();
        if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericMonth))
        {
            month = numericMonth;
            return month is >= 1 and <= 12;
        }

        var key = clean.Length > 4 ? clean[..4] : clean;
        month = key switch
        {
            "jan" or "janu" or "janv" => 1,
            "feb" or "febr" or "fevr" => 2,
            "mar" or "marc" or "mars" or "maer" => 3,
            "apr" or "avri" => 4,
            "may" or "mai" => 5,
            "jun" or "june" or "juin" or "juni" => 6,
            "jul" or "july" or "juil" or "juli" => 7,
            "aug" or "augu" or "aout" => 8,
            "sep" or "sept" => 9,
            "oct" or "octo" or "okt" or "okto" => 10,
            "nov" or "nove" => 11,
            "dec" or "dece" or "dez" or "deze" => 12,
            _ => 0
        };

        return month > 0;
    }

    private static bool TryGetLodestoneTimeZone(string zoneText, out TimeZoneInfo timeZone, out TimeSpan? fixedOffset)
    {
        fixedOffset = null;

        switch (zoneText.ToUpperInvariant())
        {
            case "PST":
                fixedOffset = TimeSpan.FromHours(-8);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("PST", fixedOffset.Value, "PST", "PST");
                return true;

            case "PDT":
                fixedOffset = TimeSpan.FromHours(-7);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("PDT", fixedOffset.Value, "PDT", "PDT");
                return true;

            case "EST":
                fixedOffset = TimeSpan.FromHours(-5);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("EST", fixedOffset.Value, "EST", "EST");
                return true;

            case "EDT":
                fixedOffset = TimeSpan.FromHours(-4);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("EDT", fixedOffset.Value, "EDT", "EDT");
                return true;

            case "MST":
                fixedOffset = TimeSpan.FromHours(-7);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("MST", fixedOffset.Value, "MST", "MST");
                return true;

            case "MDT":
                fixedOffset = TimeSpan.FromHours(-6);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("MDT", fixedOffset.Value, "MDT", "MDT");
                return true;

            case "UTC":
            case "GMT":
                fixedOffset = TimeSpan.Zero;
                timeZone = TimeZoneInfo.Utc;
                return true;

            case "BST":
                fixedOffset = TimeSpan.FromHours(1);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("BST", fixedOffset.Value, "BST", "BST");
                return true;

            case "JST":
                fixedOffset = TimeSpan.FromHours(9);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("JST", fixedOffset.Value, "JST", "JST");
                return true;

            case "ACST":
                fixedOffset = TimeSpan.FromHours(9.5);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("ACST", fixedOffset.Value, "ACST", "ACST");
                return true;

            case "AEST":
                fixedOffset = TimeSpan.FromHours(10);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("AEST", fixedOffset.Value, "AEST", "AEST");
                return true;

            case "AEDT":
                fixedOffset = TimeSpan.FromHours(11);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("AEDT", fixedOffset.Value, "AEDT", "AEDT");
                return true;

            case "CET":
                fixedOffset = TimeSpan.FromHours(1);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("CET", fixedOffset.Value, "CET", "CET");
                return true;

            case "CEST":
                fixedOffset = TimeSpan.FromHours(2);
                timeZone = TimeZoneInfo.CreateCustomTimeZone("CEST", fixedOffset.Value, "CEST", "CEST");
                return true;

            default:
                timeZone = TimeZoneInfo.Utc;
                return false;
        }
    }



    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(character);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
