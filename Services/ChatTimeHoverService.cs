using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Clock.Windows;

// Hover handling has a few tiny state checks; I left them explicit to make it easier to debug in-game.


namespace Clock.Services;

public sealed class ChatTimeHoverService : IDisposable
{
    // AM/PM can come as symbols with private-use glyphs some times, so the parser treats them the same as plain text.
    private const char GameAmSymbol = '\ue06d';
    private const char GamePmSymbol = '\ue06e';

    // The chat link id is intentionally plugin-specific (ASCII-ish CLKT) and the original payloads are preserved around it.
    private const uint ChatTimeLinkId = 0x434C4B54;
    private static readonly string AmPmPart = $@"a\.?m\.?|p\.?m\.?|am|pm|a|p|{GameAmSymbol}|{GamePmSymbol}";
    private static readonly string GameNumberPart = @"(?:\d{1,2}|[\ue08f-\ue098]{1,2}|[\ue099-\ue0ae])";
    private static readonly string TimePart = $@"(?:noon|midnight|{GameNumberPart}(?:\s*[:h.]\s*{GameNumberPart})?)";
    private static readonly string ZoneCorePart = @"(?:utc[+-]\d{1,2}(?::?\d{2})?|gmt[+-]\d{1,2}(?::?\d{2})?|[a-z]{2,5})";
    private static readonly string ZoneWrappedPart = $@"(?:[\(\[\{{]\s*)?(?:{ZoneCorePart})(?:\s*[\)\]\}}])?";
    // Zone wrappers are allowed since some texts often have things like "(PDT)" or "[EDT]", but only the clean zone code will be captured.
    private static readonly string ZoneCapturePart = $@"(?:[\(\[\{{]\s*)?(?<zone>{ZoneCorePart})(?:\s*[\)\]\}}])?";
    private static readonly string MonthPart = @"(?:jan(?:uary|eiro|vier)?|fev(?:ereiro|rier)?|feb(?:ruary|ruar)?|f[eé]v(?:rier)?|mar(?:ch|[cç]o|s)?|m[aä]rz|maerz|apr(?:il)?|abr(?:il)?|avr(?:il)?|may|mai(?:o)?|jun(?:e|ho|i)?|jul(?:y|ho|i|let)?|aug(?:ust|osto)?|ago(?:sto)?|aou?t|sep(?:t(?:ember|embre)?|tembro)?|set(?:embro)?|oct(?:ober|obre)?|okt(?:ober)?|out(?:ubro)?|nov(?:ember|embre|embro)?|dec(?:ember|embre)?|d[eé]c(?:embre)?|dez(?:ember|embro)?)";

    // Ranges are checked before single times so a line like "7PM-10PM EST" becomes one single clickable span, avoiding showing two unrelated links.
    private static readonly Regex TimeRangeRegex = new(
        $@"(?<![\p{{L}}\p{{N}}])(?<start>{TimePart})\s*(?<startampm>{AmPmPart})?\s*(?<sep>(?:-|–|—|@)|(?:till|to)\b|\s+(?={TimePart}\s*(?:{AmPmPart})?\s*{ZoneWrappedPart}(?![\p{{L}}\p{{N}}])))\s*(?<end>{TimePart})\s*(?<endampm>{AmPmPart})?\s*{ZoneCapturePart}(?![\p{{L}}\p{{N}}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Single-time detection stays deliberately narrow; plain numbers without a known timezone are left alone to avoid noisy chat mutations.
    private static readonly Regex TimeMentionRegex = new(
        $@"(?<![\p{{L}}\p{{N}}])(?<time>{TimePart})\s*(?<ampm>{AmPmPart})?\s*{ZoneCapturePart}(?![\p{{L}}\p{{N}}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Date text is only used as context for alarm creation; the date itself is not converted into a clickable chat region. Maybe in a future update.
    // These extra date patterns are deliberately closer to "strict" parsing: useful venue/event text, but not every casual number in chat.
    private static readonly Regex MonthFirstDateRegex = new(
        $@"\b(?<month>{MonthPart})[\.]?\s+(?<day>\d{{1,2}})(?:st|nd|rd|th|º|ª|e)?(?:(?:\s*[,.-]\s*|\s+)(?<year>20\d{{2}}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DayFirstDateRegex = new(
        $@"\b(?<day>\d{{1,2}})(?:st|nd|rd|th|º|ª|e)?(?:\s+(?:of|de|do|du|der))?\s+(?<month>{MonthPart})[\.]?(?:(?:\s*[,.-]\s*|\s+)(?<year>20\d{{2}}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // These Chrono-inspired date shapes are kept as context helpers only; they do not make date-only text clickable by themselves.
    // https://github.com/wanasit/chrono
    private static readonly Regex IsoDateRegex = new(
        @"\b(?<year>20\d{2})[-/](?<month>\d{1,2})[-/](?<day>\d{1,2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RelativeDateRegex = new(
        @"\b(?<word>today|tonight|tomorrow|tmrw)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WeekdayDateRegex = new(
        @"\b(?<modifier>this|next)?\s*(?<weekday>mon(?:day)?|tue(?:sday)?|wed(?:nesday)?|thu(?:rsday)?|fri(?:day)?|sat(?:urday)?|sun(?:day)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Configuration config;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Func<string, string> t;
    private readonly DalamudLinkPayload linkPayload;
    private readonly ChatTimeHoverPopupWindow popupWindow;
    private readonly Dictionary<string, (ChatTimeMatch Match, DateTime StoredAtUtc)> recentMatches = new(StringComparer.OrdinalIgnoreCase);
    // Keeping setup close to the constructor makes the object lifecycle easier to trace.

    public ChatTimeHoverService(Configuration config, IChatGui chatGui, IPluginLog log, Func<string, string> translate, ChatTimeHoverPopupWindow popupWindow)
    {
        this.config = config;
        this.chatGui = chatGui;
        this.log = log;
        t = translate;
        this.popupWindow = popupWindow;

        linkPayload = chatGui.AddChatLinkHandler(ChatTimeLinkId, OnClockTimeLinkClicked);
        chatGui.CheckMessageHandled += OnCheckMessageHandled;
    }

    public void Dispose()
    {
        chatGui.CheckMessageHandled -= OnCheckMessageHandled;
        chatGui.RemoveChatLinkHandler(ChatTimeLinkId);
    }

    private void OnCheckMessageHandled(IHandleableChatMessage message)
    {
        if (!config.ChatTimeHoverEnabled)
            return;

        try
        {
            // Do not re-process a line that already contains our link payload; this keeps repeated chat redraws from nesting links.
            if (message.Message.Payloads.Any(p => p is DalamudLinkPayload link && link.CommandId == ChatTimeLinkId))
                return;

            // Leave the damn colorized chat lines alone otherwise the game font analyzer will be unstable when native links are mixed into those payloads.
            if (message.Message.Payloads.Any(p => p is UIForegroundPayload))
                return;

            var targetId = string.IsNullOrWhiteSpace(config.ChatTimeHoverTimeZoneId)
                ? config.SelectedTimeZoneId
                : config.ChatTimeHoverTimeZoneId;

            if (!TimeZoneHelper.TryResolveTimeZone(targetId, out targetId))
                return;

            if (!TryWrapTimeMentions(message.Message, targetId, out var changed))
                return;

            // This only replaces the SeString shown in chat. It doesn't consume, resend or alter the underlying game message.
            message.Message = changed;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Clock.ChatTimeHover] Failed to add native chat hover links.");
        }
    }

    private void OnClockTimeLinkClicked(uint commandId, SeString linkText)
    {
        if (!config.ChatTimeHoverEnabled)
            return;

        try
        {
            var targetId = string.IsNullOrWhiteSpace(config.ChatTimeHoverTimeZoneId)
                ? config.SelectedTimeZoneId
                : config.ChatTimeHoverTimeZoneId;

            if (!TimeZoneHelper.TryResolveTimeZone(targetId, out targetId))
                return;

            CleanOldClickMatches();

            var text = linkText.ToString();
            if (recentMatches.TryGetValue(KeyFor(text), out var cached))
            {
                popupWindow.Show(cached.Match);
                return;
            }

            var match = FindMatches(text, targetId).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(match.SourceDisplay))
                return;

            popupWindow.Show(match);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Clock.ChatTimeHover] Failed to show clicked time conversion tooltip.");
        }
    }

    private bool TryWrapTimeMentions(SeString source, string targetTimeZoneId, out SeString changed)
    {
        changed = source;
        var builder = new SeStringBuilder();
        var foundAny = false;

        foreach (var payload in source.Payloads)
        {
            if (payload is TextPayload textPayload && !string.IsNullOrEmpty(textPayload.Text))
            {
                if (AppendTextWithNativeLinks(builder, textPayload.Text, targetTimeZoneId))
                    foundAny = true;
            }
            else
            {
                builder.Add(payload);
            }
        }

        if (!foundAny)
            return false;

        changed = builder.Build();
        return true;
    }

    private bool AppendTextWithNativeLinks(SeStringBuilder builder, string text, string targetTimeZoneId)
    {
        var matches = FindMatches(text, targetTimeZoneId).OrderBy(x => x.Index).ToList();
        if (matches.Count == 0)
        {
            builder.AddText(text);
            return false;
        }

        CleanOldClickMatches();

        var cursor = 0;
        foreach (var match in matches)
        {
            if (match.Index < cursor)
                continue;

            if (match.Index > cursor)
                builder.AddText(text[cursor..match.Index]);

            var visible = text.Substring(match.Index, Math.Min(match.Length, text.Length - match.Index));
            // The click callback receives just the clicked text, so the full parsed match is cached to keep date context from the original line.
            recentMatches[KeyFor(visible)] = (match, DateTime.UtcNow);

            builder.Add(linkPayload);
            builder.AddText(visible);
            builder.Add(RawPayload.LinkTerminator);
            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
            builder.AddText(text[cursor..]);

        return true;
    }

    public IReadOnlyList<ChatTimeMatch> FindMatches(string text, string targetTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<ChatTimeMatch>();

        if (string.IsNullOrWhiteSpace(targetTimeZoneId))
            targetTimeZoneId = config.SelectedTimeZoneId;

        var matches = new List<ChatTimeMatch>();
        var used = new List<(int Start, int End)>();

        // Time ranges are evaluated first so the single-time parser cannot steal part of a range before being able to build one clean clickable span.
        foreach (Match match in TimeRangeRegex.Matches(text))
        {
            if (!LooksLikeValidRange(match) || !TryBuildRangeMatch(text, match, targetTimeZoneId, out var converted))
                continue;

            matches.Add(converted);
            used.Add((match.Index, match.Index + match.Length));
        }

        // Single times are handled after occupied range spans are marked to keep range and single-time matches from overlapping.
        foreach (Match match in TimeMentionRegex.Matches(text))
        {
            if (used.Any(x => match.Index >= x.Start && match.Index < x.End))
                continue;

            if (!LooksLikeValidMention(match) || !TryBuildSingleMatch(text, match, targetTimeZoneId, out var converted))
                continue;

            matches.Add(converted);
        }

        return matches;
    }

    private static bool LooksLikeValidRange(Match match)
    {
        var zone = CleanZoneText(match.Groups["zone"].Value);
        if (string.IsNullOrWhiteSpace(zone))
            return false;

        if (string.Equals(zone, "am", StringComparison.OrdinalIgnoreCase) || string.Equals(zone, "pm", StringComparison.OrdinalIgnoreCase))
            return false;

        var sep = match.Groups["sep"].Value;
        if (sep.Trim().Length == 0 && string.IsNullOrWhiteSpace(match.Groups["startampm"].Value))
            return false;

        return TimeZoneHelper.TryResolveTimeZone(zone, out _) || zone.StartsWith("utc", StringComparison.OrdinalIgnoreCase) || zone.StartsWith("gmt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeValidMention(Match match)
    {
        var timeText = match.Groups["time"].Value;
        var ampm = match.Groups["ampm"].Value;
        var zone = CleanZoneText(match.Groups["zone"].Value);

        if (string.IsNullOrWhiteSpace(zone))
            return false;

        if (string.Equals(zone, "am", StringComparison.OrdinalIgnoreCase) || string.Equals(zone, "pm", StringComparison.OrdinalIgnoreCase))
            return false;

        var hasMinute = timeText.Contains(':') || timeText.Contains('h') || timeText.Contains('.');
        var hasAmPm = !string.IsNullOrWhiteSpace(ampm);
        var zoneLooksLikeOffset = zone.StartsWith("utc", StringComparison.OrdinalIgnoreCase) || zone.StartsWith("gmt", StringComparison.OrdinalIgnoreCase);
        var zoneLooksKnown = TimeZoneHelper.TryResolveTimeZone(zone, out _);

        return hasMinute || hasAmPm || zoneLooksKnown || zoneLooksLikeOffset;
    }

    private bool TryBuildSingleMatch(string wholeText, Match match, string targetTimeZoneId, out ChatTimeMatch converted)
    {
        converted = default;

        var timeText = match.Groups["time"].Value.Trim();
        var ampmText = match.Groups["ampm"].Value.Trim();
        var zoneText = CleanZoneText(match.Groups["zone"].Value);

        if (!TryResolveSourceTimeZone(zoneText, out var sourceTimeZoneId, out var sourceLabel))
            return false;

        if (!TimeZoneHelper.TryResolveTimeZone(targetTimeZoneId, out var resolvedTargetId))
            return false;

        if (!TryParseChatTime(timeText, ampmText, out var hour, out var minute))
            return false;

        var sourceZone = TimeZoneHelper.GetTimeZone(sourceTimeZoneId);
        var targetZone = TimeZoneHelper.GetTimeZone(resolvedTargetId);
        var sourceDate = ResolveSourceDate(wholeText, sourceZone);
        var sourceLocal = new DateTime(sourceDate.Year, sourceDate.Month, sourceDate.Day, hour, minute, 0, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(sourceLocal, sourceZone);
        var targetLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, targetZone);

        var targetLabel = LabelForZone(targetZone, resolvedTargetId);
        var sourceDisplay = FormatTime(sourceLocal, sourceLabel);
        if (SameResolvedZone(sourceTimeZoneId, resolvedTargetId) || string.Equals(sourceLabel, targetLabel, StringComparison.OrdinalIgnoreCase))
        {
            converted = new ChatTimeMatch(match.Index, match.Length, sourceDisplay, string.Empty, t("Same current timezone"), targetLocal, utc, resolvedTargetId);
            return true;
        }

        var targetDisplay = FormatTime(targetLocal, targetLabel);
        var difference = FormatOffsetDifference(sourceZone.GetUtcOffset(utc), targetZone.GetUtcOffset(utc));
        converted = new ChatTimeMatch(match.Index, match.Length, sourceDisplay, targetDisplay, difference, targetLocal, utc, resolvedTargetId);
        return true;
    }

    private bool TryBuildRangeMatch(string wholeText, Match match, string targetTimeZoneId, out ChatTimeMatch converted)
    {
        converted = default;

        var startText = match.Groups["start"].Value.Trim();
        var endText = match.Groups["end"].Value.Trim();
        var startAmPm = match.Groups["startampm"].Value.Trim();
        var endAmPm = match.Groups["endampm"].Value.Trim();
        var zoneText = CleanZoneText(match.Groups["zone"].Value);

        if (string.IsNullOrWhiteSpace(startAmPm) && !string.IsNullOrWhiteSpace(endAmPm))
            startAmPm = endAmPm;
        else if (string.IsNullOrWhiteSpace(endAmPm) && !string.IsNullOrWhiteSpace(startAmPm))
            endAmPm = startAmPm;

        if (!TryResolveSourceTimeZone(zoneText, out var sourceTimeZoneId, out var sourceLabel))
            return false;

        if (!TimeZoneHelper.TryResolveTimeZone(targetTimeZoneId, out var resolvedTargetId))
            return false;

        if (!TryParseChatTime(startText, startAmPm, out var startHour, out var startMinute) ||
            !TryParseChatTime(endText, endAmPm, out var endHour, out var endMinute))
            return false;

        var sourceZone = TimeZoneHelper.GetTimeZone(sourceTimeZoneId);
        var targetZone = TimeZoneHelper.GetTimeZone(resolvedTargetId);
        var sourceDate = ResolveSourceDate(wholeText, sourceZone);
        var startLocal = new DateTime(sourceDate.Year, sourceDate.Month, sourceDate.Day, startHour, startMinute, 0, DateTimeKind.Unspecified);
        var endLocal = new DateTime(sourceDate.Year, sourceDate.Month, sourceDate.Day, endHour, endMinute, 0, DateTimeKind.Unspecified);
        if (endLocal <= startLocal)
            endLocal = endLocal.AddDays(1);

        // Alarm setup intentionally uses the start of a range. The end time is only display context for the hover tooltip.
        // That should avoid alarm creation issues when two times are present in messages like "07PM-12PM BRT", using the first time "07PM..." for the alarm.
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, sourceZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, sourceZone);
        var targetStartLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, targetZone);
        var targetEndLocal = TimeZoneInfo.ConvertTimeFromUtc(endUtc, targetZone);

        var targetLabel = LabelForZone(targetZone, resolvedTargetId);
        var sourceDisplay = FormatRange(startLocal, endLocal, sourceLabel);
        if (SameResolvedZone(sourceTimeZoneId, resolvedTargetId) || string.Equals(sourceLabel, targetLabel, StringComparison.OrdinalIgnoreCase))
        {
            converted = new ChatTimeMatch(match.Index, match.Length, sourceDisplay, string.Empty, t("Same current timezone"), targetStartLocal, startUtc, resolvedTargetId);
            return true;
        }

        var targetDisplay = FormatRange(targetStartLocal, targetEndLocal, targetLabel);
        var difference = FormatOffsetDifference(sourceZone.GetUtcOffset(startUtc), targetZone.GetUtcOffset(startUtc));
        converted = new ChatTimeMatch(match.Index, match.Length, sourceDisplay, targetDisplay, difference, targetStartLocal, startUtc, resolvedTargetId);
        return true;
    }

    private static bool TryParseChatTime(string timeText, string ampmText, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;

        var normalized = timeText.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        if (normalized == "noon")
        {
            hour = 12;
            return true;
        }

        if (normalized == "midnight")
            return true;

        normalized = NormalizeGameNumberSymbols(normalized).Replace('h', ':').Replace('.', ':');
        var parts = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour))
            return false;

        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute))
            return false;

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;

        if (!string.IsNullOrWhiteSpace(ampmText))
        {
            var ampm = ampmText.Replace(".", string.Empty).Trim().ToLowerInvariant();
            if ((ampm is "p" or "pm" || ampmText.Contains(GamePmSymbol)) && hour < 12)
                hour += 12;
            else if ((ampm is "a" or "am" || ampmText.Contains(GameAmSymbol)) && hour == 12)
                hour = 0;
        }

        return true;
    }


    private static string NormalizeGameNumberSymbols(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (TryGameNumberValue(c, out var value))
                builder.Append(value.ToString(CultureInfo.InvariantCulture));
            else
                builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool TryGameNumberValue(char c, out int value)
    {
        value = c switch
        {
            '\ue08f' => 0,
            >= '\ue090' and <= '\ue098' => c - '\ue08f',
            >= '\ue099' and <= '\ue0ae' => c - '\ue08f',
            _ => -1,
        };

        return value >= 0 && value <= 31;
    }

    private static DateTime ResolveSourceDate(string text, TimeZoneInfo sourceZone)
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sourceZone).Date;
        // Dates are resolved in the source timezone, not the user's target timezone, because that is where the advertised time was written.
        return TryFindDateInMessage(text, now, out var detected) ? detected : now;
    }

    private static bool TryFindDateInMessage(string text, DateTime fallbackDate, out DateTime date)
    {
        date = default;

        foreach (var match in MonthFirstDateRegex.Matches(text).Cast<Match>().Concat(DayFirstDateRegex.Matches(text).Cast<Match>()))
        {
            if (!TryMonthNumber(match.Groups["month"].Value, out var month))
                continue;

            if (!int.TryParse(match.Groups["day"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
                continue;

            var year = fallbackDate.Year;
            if (match.Groups["year"].Success && int.TryParse(match.Groups["year"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
                year = parsedYear;

            if (TryMakeDate(year, month, day, out date))
                return true;
        }

        foreach (Match match in IsoDateRegex.Matches(text))
        {
            if (!int.TryParse(match.Groups["year"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ||
                !int.TryParse(match.Groups["month"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) ||
                !int.TryParse(match.Groups["day"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
                continue;

            if (TryMakeDate(year, month, day, out date))
                return true;
        }

        foreach (Match match in RelativeDateRegex.Matches(text))
        {
            var word = match.Groups["word"].Value.ToLowerInvariant();
            date = word is "tomorrow" or "tmrw" ? fallbackDate.AddDays(1) : fallbackDate;
            return true;
        }

        foreach (Match match in WeekdayDateRegex.Matches(text))
        {
            if (!TryWeekdayNumber(match.Groups["weekday"].Value, out var targetDay))
                continue;

            var modifier = match.Groups["modifier"].Value;
            date = ResolveWeekdayDate(fallbackDate, targetDay, modifier);
            return true;
        }

        return false;
    }

    private static bool TryMakeDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        if (month < 1 || month > 12 || day < 1 || year < 2000 || year > 2099 || day > DateTime.DaysInMonth(year, month))
            return false;

        date = new DateTime(year, month, day);
        return true;
    }

    private static DateTime ResolveWeekdayDate(DateTime fallbackDate, DayOfWeek targetDay, string modifier)
    {
        // Weekday text is resolved from the message/source timezone date so stuff like "Friday 8PM EST" behaves like the author meant, not like the user local date.
        var delta = ((int)targetDay - (int)fallbackDate.DayOfWeek + 7) % 7;
        if (modifier.Equals("next", StringComparison.OrdinalIgnoreCase))
            delta = delta == 0 ? 7 : delta + 7;

        return fallbackDate.AddDays(delta);
    }

    private static bool TryWeekdayNumber(string text, out DayOfWeek weekday)
    {
        weekday = DayOfWeek.Sunday;
        var key = text.Trim().ToLowerInvariant();
        if (key.Length > 3)
            key = key[..3];

        weekday = key switch
        {
            "sun" => DayOfWeek.Sunday,
            "mon" => DayOfWeek.Monday,
            "tue" => DayOfWeek.Tuesday,
            "wed" => DayOfWeek.Wednesday,
            "thu" => DayOfWeek.Thursday,
            "fri" => DayOfWeek.Friday,
            "sat" => DayOfWeek.Saturday,
            _ => DayOfWeek.Sunday
        };

        return key is "sun" or "mon" or "tue" or "wed" or "thu" or "fri" or "sat";
    }

    private static bool TryMonthNumber(string monthText, out int month)
    {
        month = 0;
        var key = StripMonthKey(monthText);
        if (key.Length > 3)
            key = key[..3];

        month = key switch
        {
            "jan" => 1,
            "feb" or "fev" => 2,
            "mar" or "mae" => 3,
            "apr" or "abr" or "avr" => 4,
            "may" or "mai" => 5,
            "jun" => 6,
            "jul" or "jui" => 7,
            "aug" or "ago" or "aou" => 8,
            "sep" or "set" => 9,
            "oct" or "okt" or "out" => 10,
            "nov" => 11,
            "dec" or "dez" => 12,
            _ => 0
        };

        return month != 0;
    }

    private static string StripMonthKey(string monthText)
    {
        var normalized = monthText.Trim().TrimEnd('.').ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var buffer = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                buffer.Append(ch);
        }

        return buffer.ToString();
    }

    private static bool TryResolveSourceTimeZone(string zoneText, out string sourceTimeZoneId, out string sourceLabel)
    {
        sourceTimeZoneId = string.Empty;
        zoneText = CleanZoneText(zoneText);
        sourceLabel = zoneText.ToUpperInvariant();
        return TimeZoneHelper.TryResolveTimeZone(zoneText, out sourceTimeZoneId);
    }

    private static string CleanZoneText(string zoneText)
    {
        // Event/news texts often wraps timezone codes like "(PDT)" or "[EDT]"; strip only the wrapper and keep the actual timezone token.
        // The resolver will still get the original code/offset without plugin-specific aliases.
        return (zoneText ?? string.Empty).Trim().Trim('(', ')', '[', ']', '{', '}').Trim();
    }

    private static string FormatTime(DateTime time, string label)
    {
        return $"{time.ToString("h:mm tt", CultureInfo.InvariantCulture)} {label}";
    }

    private static string FormatRange(DateTime start, DateTime end, string label)
    {
        return $"{start.ToString("h:mm tt", CultureInfo.InvariantCulture)}-{end.ToString("h:mm tt", CultureInfo.InvariantCulture)} {label}";
    }

    private string FormatOffsetDifference(TimeSpan sourceOffset, TimeSpan targetOffset)
    {
        var diff = targetOffset - sourceOffset;
        if (diff == TimeSpan.Zero)
            return t("Same UTC offset");

        var abs = diff.Duration();
        var totalMinutes = (int)Math.Round(abs.TotalMinutes);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        string text;

        if (hours > 0 && minutes > 0)
            text = string.Format(CultureInfo.InvariantCulture, t("{0}h {1}m"), hours, minutes);
        else if (hours > 0)
            text = string.Format(CultureInfo.InvariantCulture, t(hours == 1 ? "{0} hour" : "{0} hours"), hours);
        else
            text = string.Format(CultureInfo.InvariantCulture, t(minutes == 1 ? "{0} minute" : "{0} minutes"), minutes);

        return string.Format(CultureInfo.InvariantCulture, t(diff > TimeSpan.Zero ? "{0} ahead" : "{0} behind"), text);
    }

    private static string LabelForZone(TimeZoneInfo zone, string configuredId)
    {
        var label = TimeZoneHelper.ToShortText(configuredId);
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        var name = zone.StandardName;
        var bits = Regex.Matches(name, @"\b[A-Z]").Select(x => x.Value).ToArray();
        return bits.Length is >= 2 and <= 5 ? string.Concat(bits) : zone.StandardName;
    }

    private static bool SameResolvedZone(string sourceTimeZoneId, string targetTimeZoneId)
    {
        if (!TimeZoneHelper.TryResolveTimeZone(sourceTimeZoneId, out var sourceResolved))
            sourceResolved = sourceTimeZoneId;

        if (!TimeZoneHelper.TryResolveTimeZone(targetTimeZoneId, out var targetResolved))
            targetResolved = targetTimeZoneId;

        return string.Equals(sourceResolved, targetResolved, StringComparison.OrdinalIgnoreCase);
    }

    private static string KeyFor(string text)
    {
        // Normalize spacing before using the detected text as a cache key; chat payloads can carry odd spacing/newlines
        // but the click lookup should still match the same parsed time/date entry.
        return Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
    }

    private void CleanOldClickMatches()
    {
        if (recentMatches.Count == 0)
            return;

        // The cache is short-lived on purpose. It keeps click context for recent chat lines without holding old chat text forever.
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var key in recentMatches.Where(x => x.Value.StoredAtUtc < cutoff).Select(x => x.Key).ToArray())
            recentMatches.Remove(key);
    }
    // Keep the parsed chat span separate from the alarm request: the tooltip needs display/conversion details,
    // while alarm creation only needs the final target time and timezone to pre-fill the editor safely.
    public readonly record struct ChatTimeMatch(int Index, int Length, string SourceDisplay, string TargetDisplay, string DifferenceText, DateTime TargetLocal, DateTime TargetUtc, string TargetTimeZoneId);

    public readonly record struct ChatAlarmSetupRequest(DateTime TargetLocal, string TargetTimeZoneId);
}
