using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ManagedFontAtlas;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Clock.Services;
using Clock.Windows;

// bryer // The plugin class is a bit of a hub, so the ordering here favours "where I look first" over perfect layering.


namespace Clock;

// Main plugin entry point. It wires services together and owns the Dalamud-facing lifecycle.
public sealed class Plugin : IDalamudPlugin
{

    // I keep one shared Lodestone state because Events/Maintenance hit different pages but they still share the same external site.
    private enum LodestoneCheckKind
    {
        None,
        Maintenance,
        SeasonalEvents
    }

    private const string CommandName = "/clock";
    private const string AlarmsCommand = "/clockalarms";
    private const string DirectAlarmsCommand = "/alarms";

    public const int MinAlarmSoundEffectId = 1;
    public const int MaxAlarmSoundEffectId = 16;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IChatGui chatGui;
    private readonly IToastGui toastGui;
    private readonly IKeyState keyState;
    private readonly LodestoneMaintenanceService maintenanceService = new();
    private readonly EventService eventService = new();
    private readonly ChatTimestampService chatTimestampService;
    private readonly ChatTimeHoverService chatTimeHoverService;
    private readonly QuickSymbolsIpcService quickSymbolsIpcService;
    private readonly IFontHandle? digitalClockFontHandle;
    private readonly IFontHandle? alarmSessionDigitalFontHandle;
    private readonly IFontHandle? technologyClockFontHandle;
    private readonly IFontHandle? ka1ClockFontHandle;
    private readonly IFontHandle? countdownClockFontHandle;
    private readonly IFontHandle? alarmPanelFont;
    private readonly IFontHandle? alarmTitleFont;
    private readonly IFontHandle? largeAlarmIconFont;

    private bool digitalFontQueued;
    private bool digitalReadyLogged;
    private bool digitalLoadLogged;
    private bool alarmFontQueued;
    private bool alarmReadyLogged;
    private bool alarmLoadLogged;
    private bool techFontQueued;
    private bool techReadyLogged;
    private bool techLoadLogged;
    private bool ka1ClockFontBuildQueued;
    private bool ka1ClockFontReadyLogged;
    private bool ka1ClockFontLoadLogged;
    private bool countdownFontQueued;
    private bool countdownReadyLogged;
    private bool countdownLoadLogged;
    private bool alarmPanelAlarmFontBuildQueued;
    private bool alarmPanelAlarmFontReadyLogged;
    private bool alarmPanelAlarmFontLoadLogged;
    private bool alarmPanelAlarmTitleFontBuildQueued;
    private bool alarmPanelAlarmTitleFontReadyLogged;
    private bool alarmPanelAlarmTitleFontLoadLogged;
    private bool largeAlarmIconFontBuildQueued;
    private bool largeAlarmIconFontReadyLogged;
    private bool largeAlarmIconFontLoadLogged;
    private bool hasAutoStarted;
    private bool wantedMainWindowOpen;
    private DateTime lastReminderCheckUtc = DateTime.MinValue;
    private DateTime lastMaintenanceCheckUtc = DateTime.MinValue;
    private Task<LodestoneMaintenanceInfo?>? maintenanceRefreshTask;
    private bool maintenanceManual;
    private bool maintenanceQueued;
    private bool maintenanceQueuedManual;
    private long maintenanceQueueOrder;
    private bool eventQueued;
    private bool eventQueuedManual;
    private long eventQueueOrder;
    private long lodestoneQueueOrder;
    private LodestoneCheckKind lodestoneCheckKind = LodestoneCheckKind.None;
    private DateTime alarmOverlayUntilUtc = DateTime.MinValue;
    private DateTime alarmOverlayTriggerUtc = DateTime.MinValue;
    private string alarmOverlayTimeZoneId = string.Empty;
    private bool alarmOverlayPreviewActive;
    private bool alarmsWindowHotkeyWasDown;
    private Guid repeatingAlarmSoundId;
    private int repeatingAlarmSoundEffectId;
    private DateTime repeatingAlarmSoundNextUtc = DateTime.MinValue;

    private Task<IReadOnlyList<EventInfo>>? eventTask;
    private bool eventTaskManual;
    private DateTime lastEventCheckUtc = DateTime.MinValue;
    private bool wasLoggedIn;
    private bool loginEventNoticePending;
    private int loginEventSession;
    private readonly HashSet<string> maintenanceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> recentAlarmIds = new();

    public Configuration Configuration { get; private set; }

    public readonly WindowSystem WindowSystem = new("Clock");

    public IDalamudPluginInterface PluginInterface => pluginInterface;
    public IPluginLog Log => log;
    public IKeyState KeyState => keyState;
    public QuickSymbolsIpcService QuickSymbolsIpc => quickSymbolsIpcService;

    public ConfigWindow ConfigWindow { get; private init; }
    private MainWindow MainWindow { get; init; }
    private AlarmOverlayWindow AlarmOverlayWindow { get; init; }
    private CommandHintWindow CommandHintWindow { get; init; }
    private ChatTimeHoverPopupWindow ChatTimeHoverPopupWindow { get; init; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IClientState clientState,
        ICondition condition,
        IChatGui chatGui,
        IToastGui toastGui,
        IKeyState keyState,
        IGameInteropProvider gameInteropProvider,
        IGameGui gameGui)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
        this.clientState = clientState;
        this.condition = condition;
        this.chatGui = chatGui;
        this.toastGui = toastGui;
        this.keyState = keyState;

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);
        Configuration.EnsureInitialized();
        Configuration.Save();

        // Theme fonts are registered once at startup and then reused by every clock draw path;
        // keeping them centralized to avoid rebuilding font atlases while the UI is rendering.
        digitalClockFontHandle = CreateClockFontHandle("DS-DIGI.ttf", "digital");
        alarmSessionDigitalFontHandle = CreateClockFontHandle("DS-DIGI.ttf", "alarm session digital", pluginInterface.UiBuilder.FontDefaultSizePx * 1.95f);
        technologyClockFontHandle = CreateClockFontHandle("E1234.ttf", "technology");
        ka1ClockFontHandle = CreateClockFontHandle("ka1.ttf", "ka1");
        countdownClockFontHandle = CreateClockFontHandle("DigitTech7-Regular.otf", "countdown");
        alarmPanelFont = CreateWindowsFontHandle("segoeui.ttf", 42f, "alarmPanel alarm");
        alarmTitleFont = CreateWindowsFontHandle("segoeui.ttf", 34f, "alarmPanel alarm title");
        largeAlarmIconFont = CreateDalamudIconFontHandle(48f, "large alarm icon");
        QueueClockFontBuild(digitalClockFontHandle, ref digitalFontQueued);
        QueueClockFontBuild(alarmSessionDigitalFontHandle, ref alarmFontQueued);
        QueueClockFontBuild(technologyClockFontHandle, ref techFontQueued);
        QueueClockFontBuild(ka1ClockFontHandle, ref ka1ClockFontBuildQueued);
        QueueClockFontBuild(countdownClockFontHandle, ref countdownFontQueued);
        QueueClockFontBuild(alarmPanelFont, ref alarmPanelAlarmFontBuildQueued);
        QueueClockFontBuild(alarmTitleFont, ref alarmPanelAlarmTitleFontBuildQueued);
        QueueClockFontBuild(largeAlarmIconFont, ref largeAlarmIconFontBuildQueued);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        AlarmOverlayWindow = new AlarmOverlayWindow(this);
        CommandHintWindow = new CommandHintWindow(T);
        ChatTimeHoverPopupWindow = new ChatTimeHoverPopupWindow(Configuration, pluginInterface, log, T, SetupAlarmFromChatTime);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(AlarmOverlayWindow);
        WindowSystem.AddWindow(CommandHintWindow);
        WindowSystem.AddWindow(ChatTimeHoverPopupWindow);

        chatTimestampService = new ChatTimestampService(Configuration, gameInteropProvider, log);
        chatTimeHoverService = new ChatTimeHoverService(Configuration, chatGui, log, T, ChatTimeHoverPopupWindow);
        quickSymbolsIpcService = new QuickSymbolsIpcService(pluginInterface, log, keyState);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Clock commands: /clock, /clock on, /clock off, /clock help, /clock timezone <TimeZoneInfo ID>, /clock format 12|24|12s|24s|weekday|date, " +
                "/clock colon default|always|hidden|slow|fast, /clock layout horizontal|vertical, " +
                "/clock <timezone> to <timezone>, /clock lock, /clock unlock, " +
                "/clock profile next|list|set <n>|add <name>|rename <name>|delete"
        });


        commandManager.AddHandler(AlarmsCommand, new CommandInfo(OnAlarmsCommand)
        {
            HelpMessage = T("Open alarm overlay")
        });

        commandManager.AddHandler(DirectAlarmsCommand, new CommandInfo(OnAlarmsCommand)
        {
            HelpMessage = T("Open alarm overlay")
        });

        pluginInterface.UiBuilder.DisableCutsceneUiHide = !Configuration.HideDuringCutscenes;
        pluginInterface.UiBuilder.Draw += DrawUI;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

    }




    private IFontHandle? CreateClockFontHandle(string fileName, string label, float? sizePx = null)
    {
        var baseDir = pluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            log.Warning("Clock {Label} font path could not be resolved: assembly directory is empty.", label);
            return null;
        }

        var fontPath = FindClockFontPath(baseDir, fileName);
        if (fontPath == null)
        {
            log.Warning("Clock {Label} font file was not found. Expected {FileName} inside a Fonts folder near the plugin output or source folder.", label, fileName);
            return null;
        }

        return pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var config = new SafeFontConfig
            {
                SizePx = sizePx ?? pluginInterface.UiBuilder.FontDefaultSizePx,
                OversampleH = 3,
                OversampleV = 1
            };

            var font = tk.AddFontFromFile(fontPath, config);
            tk.Font = font;
        }));
    }



    private IFontHandle? CreateWindowsFontHandle(string fileName, float sizePx, string label)
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrWhiteSpace(fontsDir))
            return null;

        var fontPath = Path.Combine(fontsDir, fileName);
        if (!File.Exists(fontPath))
            return null;

        return pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var config = new SafeFontConfig
            {
                SizePx = sizePx,
                OversampleH = 3,
                OversampleV = 2
            };

            var font = tk.AddFontFromFile(fontPath, config);
            tk.Font = font;
        }));
    }

    private IFontHandle? CreateDalamudIconFontHandle(float sizePx, string label)
    {
        try
        {
            return pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
                tk.AddFontAwesomeIconFont(new() { SizePx = sizePx })));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Clock {Label} icon font could not be created.", label);
            return null;
        }
    }

    private static string? FindClockFontPath(string baseDir, string fileName)
    {
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
        {
            var inFonts = Path.Combine(dir.FullName, "Fonts", fileName);
            if (File.Exists(inFonts))
                return inFonts;

            var beside = Path.Combine(dir.FullName, fileName);
            if (File.Exists(beside))
                return beside;
        }

        return null;
    }

    private void QueueClockFontBuild(IFontHandle? handle, ref bool queued)
    {
        if (handle == null || queued || handle.Available)
            return;

        queued = true;
        try
        {
            pluginInterface.UiBuilder.FontAtlas.BuildFontsOnNextFrame();
        }
        catch (InvalidOperationException)
        {
            try
            {
                _ = pluginInterface.UiBuilder.FontAtlas.BuildFontsAsync();
            }
            catch (Exception ex)
            {
                queued = false;
                log.Warning(ex, "Could not start async Clock font rebuild.");
            }
        }
        catch (Exception ex)
        {
            queued = false;
            log.Warning(ex, "Could not queue Clock font rebuild.");
        }
    }

    private void CheckClockFontState(IFontHandle? handle, ref bool queued, ref bool readyLogged, ref bool loadLogged, string label)
    {
        if (handle == null)
            return;

        if (!handle.Available && handle.LoadException == null)
            QueueClockFontBuild(handle, ref queued);

        if (!readyLogged && handle.Available)
        {
            readyLogged = true;
            log.Information("Clock {Label} font is ready.", label);
        }

        if (!loadLogged && handle.LoadException != null)
        {
            loadLogged = true;
            log.Warning(handle.LoadException, "Clock {Label} font failed to load.", label);
        }
    }

    private void CheckDigitalClockFontState()
    {
        CheckClockFontState(digitalClockFontHandle, ref digitalFontQueued, ref digitalReadyLogged, ref digitalLoadLogged, "digital");
    }

    private void CheckAlarmSessionDigitalFontState()
    {
        CheckClockFontState(alarmSessionDigitalFontHandle, ref alarmFontQueued, ref alarmReadyLogged, ref alarmLoadLogged, "alarm session digital");
    }

    public ILockedImFont? LockAlarmSessionDigitalFont()
    {
        if (alarmSessionDigitalFontHandle == null)
            return null;

        CheckAlarmSessionDigitalFontState();
        return alarmSessionDigitalFontHandle.Available ? alarmSessionDigitalFontHandle.Lock() : null;
    }

    private void CheckTechnologyClockFontState()
    {
        CheckClockFontState(technologyClockFontHandle, ref techFontQueued, ref techReadyLogged, ref techLoadLogged, "technology");
    }

    private void CheckKa1ClockFontState()
    {
        CheckClockFontState(ka1ClockFontHandle, ref ka1ClockFontBuildQueued, ref ka1ClockFontReadyLogged, ref ka1ClockFontLoadLogged, "ka1");
    }

    private void CheckCountdownClockFontState()
    {
        CheckClockFontState(countdownClockFontHandle, ref countdownFontQueued, ref countdownReadyLogged, ref countdownLoadLogged, "countdown");

        CheckClockFontState(alarmPanelFont, ref alarmPanelAlarmFontBuildQueued, ref alarmPanelAlarmFontReadyLogged, ref alarmPanelAlarmFontLoadLogged, "alarmPanel alarm");
    }

    public IDisposable PushClockTimeFont(ClockTimeTextFont font)
    {
        if (font == ClockTimeTextFont.Digital)
        {
            if (digitalClockFontHandle == null)
                return EmptyFontScope.Instance;

            CheckDigitalClockFontState();
            return digitalClockFontHandle.Available ? digitalClockFontHandle.Push() : EmptyFontScope.Instance;
        }

        if (font == ClockTimeTextFont.Technology)
        {
            if (technologyClockFontHandle == null)
                return EmptyFontScope.Instance;

            CheckTechnologyClockFontState();
            return technologyClockFontHandle.Available ? technologyClockFontHandle.Push() : EmptyFontScope.Instance;
        }

        if (font == ClockTimeTextFont.Ka1)
        {
            if (ka1ClockFontHandle == null)
                return EmptyFontScope.Instance;

            CheckKa1ClockFontState();
            return ka1ClockFontHandle.Available ? ka1ClockFontHandle.Push() : EmptyFontScope.Instance;
        }

        if (font == ClockTimeTextFont.Countdown)
        {
            if (countdownClockFontHandle == null)
                return EmptyFontScope.Instance;

            CheckCountdownClockFontState();
            return countdownClockFontHandle.Available ? countdownClockFontHandle.Push() : EmptyFontScope.Instance;
        }

        return EmptyFontScope.Instance;
    }

    public bool IsDigitalClockFontReady()
    {
        CheckDigitalClockFontState();
        return digitalClockFontHandle?.Available == true;
    }

    public bool IsTechnologyClockFontReady()
    {
        CheckTechnologyClockFontState();
        return technologyClockFontHandle?.Available == true;
    }

    public bool IsKa1ClockFontReady()
    {
        CheckKa1ClockFontState();
        return ka1ClockFontHandle?.Available == true;
    }

    public bool IsCountdownClockFontReady()
    {
        CheckCountdownClockFontState();
        return countdownClockFontHandle?.Available == true;
    }

    private void CheckAlarmPanelAlarmFontState()
    {
        CheckClockFontState(alarmPanelFont, ref alarmPanelAlarmFontBuildQueued, ref alarmPanelAlarmFontReadyLogged, ref alarmPanelAlarmFontLoadLogged, "alarmPanel alarm");
    }

    public IDisposable PushAlarmPanelAlarmFont()
    {
        if (alarmPanelFont == null)
            return EmptyFontScope.Instance;

        CheckAlarmPanelAlarmFontState();
        return alarmPanelFont.Available ? alarmPanelFont.Push() : EmptyFontScope.Instance;
    }

    private void CheckAlarmPanelAlarmTitleFontState()
    {
        CheckClockFontState(alarmTitleFont, ref alarmPanelAlarmTitleFontBuildQueued, ref alarmPanelAlarmTitleFontReadyLogged, ref alarmPanelAlarmTitleFontLoadLogged, "alarmPanel alarm title");
    }

    public IDisposable PushAlarmPanelAlarmTitleFont()
    {
        if (alarmTitleFont == null)
            return PushAlarmPanelAlarmFont();

        CheckAlarmPanelAlarmTitleFontState();
        return alarmTitleFont.Available ? alarmTitleFont.Push() : PushAlarmPanelAlarmFont();
    }

    private void CheckLargeAlarmIconFontState()
    {
        CheckClockFontState(largeAlarmIconFont, ref largeAlarmIconFontBuildQueued, ref largeAlarmIconFontReadyLogged, ref largeAlarmIconFontLoadLogged, "large alarm icon");
    }

    public IDisposable PushLargeAlarmIconFont()
    {
        if (largeAlarmIconFont == null)
            return pluginInterface.UiBuilder.IconFontHandle.Push();

        CheckLargeAlarmIconFontState();
        return largeAlarmIconFont.Available ? largeAlarmIconFont.Push() : pluginInterface.UiBuilder.IconFontHandle.Push();
    }

    public ILockedImFont? LockLargeAlarmIconFont()
    {
        if (largeAlarmIconFont == null)
            return null;

        CheckLargeAlarmIconFontState();
        return largeAlarmIconFont.Available ? largeAlarmIconFont.Lock() : null;
    }

    private sealed class EmptyFontScope : IDisposable
    {
        public static readonly EmptyFontScope Instance = new();
        public void Dispose() { }
    }

    // Chat tooltip alarms are intentionally committed immediately instead of opening the UI first (old behavior)
    // because the clicked chat timestamp already contains the final target time.
    private void SetupAlarmFromChatTime(ChatTimeHoverService.ChatAlarmSetupRequest request)
    {
        if (!ConfigWindow.CreateAlarmFromChatConversion(request.TargetLocal, request.TargetTimeZoneId))
            return;

        AlarmOverlayWindow.ShowHistory();
        OpenAlarmOverlay();
    }

    public void RefreshChatTimestampSettings()
    {
        chatTimestampService.ApplyConfiguration();
    }

    public void PrintChatTimeHoverTestMessage()
    {
        // These local-only chat lines aim to give users a quick way to verify the whole hover pipeline without needing a real message in chat:
        // single time parsing, range parsing, date context, tooltip display and alarm creation.
        var (singleTime, rangeTime, venueDate) = BuildChatTimeHoverTestBits();
        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("This is a test message showing how Time like {0} is detected. Please hover your mouse above the time and click it."), singleTime), "Clock");
        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("This is a second test message showing how Time like {0} is detected. Please hover your mouse above the time and click it."), rangeTime), "Clock");
        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Clock Venue is opening on Exo-Gob-W0-P00 {0} at {1}! And we have gone crazy with 50mil Giveaways and prizes!!"), venueDate, singleTime), "Clock");
    }

    private (string SingleTime, string RangeTime, string VenueDate) BuildChatTimeHoverTestBits()
    {
        // Test chat uses a different source timezone on purpose, otherwise the hover path can look like a no-op for the user.
        var zoneId = PickChatTimeHoverTestSourceZone();
        var startUtc = DateTime.UtcNow.AddHours(1);
        var start = TimeZoneHelper.ConvertFromUtc(startUtc, zoneId);
        if (start.Minute != 0 || start.Second != 0 || start.Millisecond != 0)
        {
            start = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0).AddHours(1);
            startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), TimeZoneHelper.GetTimeZone(zoneId));
        }

        var end = TimeZoneHelper.ConvertFromUtc(startUtc.AddHours(5), zoneId);
        var shortZone = TimeZoneHelper.ToShortText(zoneId);
        var single = $"{start:hhtt} {shortZone}".ToUpperInvariant();
        var range = $"{start:hhtt}-{end:hhtt} {shortZone}".ToUpperInvariant();
        var venueDate = FormatEnglishMonthDay(DateTime.Now.Date.AddDays(1));
        return (single, range, venueDate);
    }

    private string PickChatTimeHoverTestSourceZone()
    {
        var primaryId = Configuration.SelectedTimeZoneId;
        if (!TimeZoneHelper.TryResolveTimeZone(primaryId, out primaryId))
            primaryId = TimeZoneInfo.Local.Id;

        var nowUtc = DateTime.UtcNow;
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(nowUtc);
        // Used only by the Test button for generated examples readable by the parser. doesn't affect real chat detection.
        // Keep this list boring and OS-resolvable; the helper below tries both Windows and IANA ids so the same build works outside Windows dev boxes too.
        var known = new[]
        {
            (Windows: "GMT Standard Time", Iana: "Europe/London"),
            (Windows: "W. Europe Standard Time", Iana: "Europe/Berlin"),
            (Windows: "Romance Standard Time", Iana: "Europe/Paris"),
            (Windows: "Tokyo Standard Time", Iana: "Asia/Tokyo"),
            (Windows: "Singapore Standard Time", Iana: "Asia/Singapore"),
            (Windows: "AUS Eastern Standard Time", Iana: "Australia/Sydney"),
            (Windows: "UTC", Iana: "Etc/UTC"),
        };

        string? picked = null;
        TimeSpan? pickedOffset = null;
        foreach (var item in known)
        {
            var zoneId = ResolveForThisMachine(item.Windows, item.Iana);
            if (string.IsNullOrWhiteSpace(zoneId) || string.Equals(zoneId, primaryId, StringComparison.OrdinalIgnoreCase))
                continue;

            var offset = TimeZoneHelper.GetTimeZone(zoneId).GetUtcOffset(nowUtc);
            if (offset < localOffset.Add(TimeSpan.FromHours(1)))
                continue;

            if (picked == null || pickedOffset == null || offset < pickedOffset.Value)
            {
                picked = zoneId;
                pickedOffset = offset;
            }
        }

        if (!string.IsNullOrWhiteSpace(picked))
            return picked;

        var fallback = ResolveForThisMachine("Tokyo Standard Time", "Asia/Tokyo");
        if (!string.IsNullOrWhiteSpace(fallback) && !string.Equals(fallback, primaryId, StringComparison.OrdinalIgnoreCase))
            return fallback;

        return TimeZoneInfo.Local.Id;
    }

    private static string? ResolveForThisMachine(string windowsId, string ianaId)
    {
        if (TimeZoneHelper.TryResolveTimeZone(windowsId, out var resolved))
            return resolved;

        return TimeZoneHelper.TryResolveTimeZone(ianaId, out resolved)
            ? resolved
            : null;
    }

    private static string FormatEnglishMonthDay(DateTime date)
    {
        var day = date.Day;
        var suffix = "th";
        var teen = day % 100;
        if (teen != 11 && teen != 12 && teen != 13)
        {
            var lastDigit = day % 10;
            if (lastDigit == 1)
                suffix = "st";
            else if (lastDigit == 2)
                suffix = "nd";
            else if (lastDigit == 3)
                suffix = "rd";
        }

        // The test string intentionally uses English month text because the parser supports it and it mirrors common venue ads.
        return $"{date.ToString("MMMM", CultureInfo.InvariantCulture)} {day}{suffix}";
    }

    public void Dispose()
    {
        Configuration.Save();

        chatTimestampService.Dispose();
        chatTimeHoverService.Dispose();
        quickSymbolsIpcService.Dispose();
        maintenanceService.Dispose();
        eventService.Dispose();

        pluginInterface.UiBuilder.Draw -= DrawUI;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        AlarmOverlayWindow.Dispose();
        CommandHintWindow.Dispose();
        ChatTimeHoverPopupWindow.Dispose();
        digitalClockFontHandle?.Dispose();
        technologyClockFontHandle?.Dispose();
        ka1ClockFontHandle?.Dispose();
        countdownClockFontHandle?.Dispose();
        alarmPanelFont?.Dispose();
        alarmTitleFont?.Dispose();

        commandManager.RemoveHandler(CommandName);
        commandManager.RemoveHandler(AlarmsCommand);
        commandManager.RemoveHandler(DirectAlarmsCommand);
    }
    // A bit verbose, but UI rendering is nicer to adjust when each step is visible.

    private void DrawUI()
    {
        pluginInterface.UiBuilder.DisableCutsceneUiHide = !Configuration.HideDuringCutscenes;
        var activeFont = Configuration.GetActiveProfile().TimeTextFont;
        if (activeFont == ClockTimeTextFont.Digital)
            CheckDigitalClockFontState();
        else if (activeFont == ClockTimeTextFont.Technology)
            CheckTechnologyClockFontState();
        else if (activeFont == ClockTimeTextFont.Ka1)
            CheckKa1ClockFontState();
        else if (activeFont == ClockTimeTextFont.Countdown)
            CheckCountdownClockFontState();

        if (!hasAutoStarted && Configuration.AutoStart && clientState.IsLoggedIn)
        {
            hasAutoStarted = true;
            wantedMainWindowOpen = true;
        }

        CheckReminders();
        MonitorCommandHints();
        CheckAlarmsWindowKeybind();
        CheckRepeatingAlarmSound();
        MainWindow.IsOpen = wantedMainWindowOpen && !ShouldHideClock();
        WindowSystem.Draw();
    }


    private void CheckAlarmsWindowKeybind()
    {
        var keys = Configuration.AlarmsWindowHotkey ?? [];
        if (keys.Length == 0 || ConfigWindow.IsCapturingAlarmsWindowKeybind)
        {
            alarmsWindowHotkeyWasDown = false;
            return;
        }

        var validKeys = keyState.GetValidVirtualKeys().ToHashSet();
        foreach (var key in keys)
        {
            if (!validKeys.Contains(key) || !keyState[key])
            {
                alarmsWindowHotkeyWasDown = false;
                return;
            }
        }

        if (alarmsWindowHotkeyWasDown)
            return;

        alarmsWindowHotkeyWasDown = true;
        foreach (var key in keys)
            keyState[key] = false;

        ToggleAlarmOverlay();
    }

    public string FormatAlarmsWindowKeybind()
    {
        var keys = Configuration.AlarmsWindowHotkey ?? [];
        return keys.Length == 0 ? "None" : string.Join("+", keys.Select(FormatVirtualKey));
    }

    public static string FormatVirtualKey(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.KEY_0 => "0",
            VirtualKey.KEY_1 => "1",
            VirtualKey.KEY_2 => "2",
            VirtualKey.KEY_3 => "3",
            VirtualKey.KEY_4 => "4",
            VirtualKey.KEY_5 => "5",
            VirtualKey.KEY_6 => "6",
            VirtualKey.KEY_7 => "7",
            VirtualKey.KEY_8 => "8",
            VirtualKey.KEY_9 => "9",
            VirtualKey.CONTROL => "Ctrl",
            VirtualKey.MENU => "Alt",
            VirtualKey.SHIFT => "Shift",
            _ => key.ToString().Replace("KEY_", string.Empty, StringComparison.Ordinal)
        };
    }

    private unsafe void MonitorCommandHints()
    {
        CommandHintWindow.IsOpen = false;

        if (!Configuration.CommandSuggestionEnabled)
            return;

        var module = RaptureAtkModule.Instance();
        if (module == null || !module->IsTextInputActive())
            return;

        var typed = module->TextInput.RawInputString.ToString();
        if (string.IsNullOrWhiteSpace(typed) || !typed.TrimStart().StartsWith("/clock", StringComparison.OrdinalIgnoreCase))
            return;

        // The input text comes from RaptureAtkModule's active text input state instead of an addon lookup.
        // That keeps the hint list independent from chat addon names while still only opening when the user is actually typing a /clock command.
        CommandHintWindow.Update(typed.TrimStart(), Vector2.Zero);
        CommandHintWindow.IsOpen = true;
    }

    // This only reads the currently focused chat text input so command suggestions can follow what the user is typing.
    // It should avoid addon-name lookups and doesn't write into the game UI; callers will still null-check the pointer before using it.
    private unsafe AtkComponentTextInput* GetActiveChatTextInput()
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
            return null;

        ref var textInput = ref module->TextInput;
        if (textInput.TargetTextInputEventInterface == null || !module->IsTextInputActive())
            return null;

        var stage = AtkStage.Instance();
        if (stage == null || stage->AtkInputManager == null)
            return null;

        var focusNode = stage->AtkInputManager->FocusedNode;
        if (focusNode == null || focusNode->GetNodeType() != NodeType.Text)
            return null;

        var node = focusNode->ParentNode;
        for (var i = 0; i < 6 && node != null; i++)
        {
            var input = node->GetAsAtkComponentTextInput();
            if (input != null)
                return input;

            node = node->ParentNode;
        }

        return null;
    }

    private bool ShouldHideClock()
    {
        if (!Configuration.HideDuringCutscenes)
            return false;

        if (pluginInterface.UiBuilder.CutsceneActive)
            return true;

        return condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78]
            || condition[ConditionFlag.OccupiedInCutSceneEvent];
    }

    private void CheckReminders()
    {
        var nowUtc = DateTime.UtcNow;

        if ((nowUtc - lastReminderCheckUtc).TotalSeconds < 1.0)
            return;

        lastReminderCheckUtc = nowUtc;

        var loggedIn = clientState.IsLoggedIn;
        var loginDetected = loggedIn && !wasLoggedIn;
        if (loginDetected)
        {
            loginEventSession++;
            loginEventNoticePending = true;
        }
        wasLoggedIn = loggedIn;

        CheckAllAlarms(nowUtc);
        UpdateMaintenanceDetection(nowUtc);
        CheckMaintenanceReminder(nowUtc);
        CheckEvents(nowUtc, loginDetected);
    }

    private void CheckAllAlarms(DateTime nowUtc)
    {
        if (Configuration.Alarms == null || Configuration.Alarms.Count == 0)
            return;

        bool changed = false;

        foreach (var alarm in Configuration.Alarms)
        {
            if (!alarm.Enabled)
                continue;

            var hasPendingSnooze = alarm.SnoozedUntilUtc > DateTime.MinValue && !alarm.SnoozeTriggered;
            if (alarm.HasTriggered && !hasPendingSnooze)
                continue;

            if (!AlarmConfigurationService.TryGetPendingTriggerUtc(alarm, out var alarmUtc))
                continue;

            if (nowUtc < alarmUtc)
            {
                recentAlarmIds.Remove(alarm.Id);
                continue;
            }

            if ((nowUtc - alarmUtc).TotalSeconds > 60)
            {
                if (alarm.RepeatMode != AlarmRepeatMode.None && AlarmConfigurationService.MoveRecurringForward(alarm, nowUtc))
                    changed = true;
                continue;
            }

            if (recentAlarmIds.Contains(alarm.Id))
                continue;

            recentAlarmIds.Add(alarm.Id);
            changed = true;

            var isSnoozeTrigger = hasPendingSnooze;
            var triggerMessage = alarm.BuildTriggerMessage(Configuration.TimeFormat, isSnoozeTrigger);
            StartAlarmOverlayVisual(alarm, isSnoozeTrigger ? alarm.SnoozedUntilUtc : alarmUtc, nowUtc);
            ShowAlarmWindowTriggerSession(alarm, alarmUtc);
            SendAlarmOutput(triggerMessage);

            if (isSnoozeTrigger)
            {
                alarm.SnoozedUntilUtc = DateTime.MinValue;
                alarm.SnoozeTriggered = true;
                alarm.HasTriggered = true;
                if (alarm.RepeatMode != AlarmRepeatMode.None)
                    AlarmConfigurationService.MoveRecurringForward(alarm, alarmUtc);
                continue;
            }

            alarm.HasTriggered = true;
            alarm.SnoozeTriggered = false;
            alarm.SnoozeCanceled = false;

            if (!AlarmConfigurationService.ScheduleSnooze(alarm, nowUtc))
            {
                alarm.SnoozedUntilUtc = DateTime.MinValue;
                if (alarm.RepeatMode != AlarmRepeatMode.None)
                    AlarmConfigurationService.MoveRecurringForward(alarm, alarmUtc);
            }
        }

        if (changed)
            Configuration.Save();
    }


    private void StartAlarmOverlayVisual(AlarmEntry alarm, DateTime utc, DateTime nowUtc)
    {
        if (!Configuration.AlarmAnimationsEnabled)
            return;

        alarmOverlayPreviewActive = false;
        alarmOverlayTriggerUtc = utc;
        alarmOverlayTimeZoneId = alarm.GetEffectiveTimeZoneId();
        alarmOverlayUntilUtc = nowUtc.AddSeconds(5.0);
    }

    private void ShowAlarmWindowTriggerSession(AlarmEntry alarm, DateTime utc)
    {
        if (!Configuration.OpenAlarmsOverlayOnAlarmTrigger)
            return;

        var alarmLocal = TimeZoneHelper.ConvertFromUtcForDisplay(utc, alarm.GetEffectiveTimeZoneId());
        var alarmTimeText = FormatAlarmSessionTime(alarmLocal, alarm.GetEffectiveTimeZoneId());
        AlarmOverlayWindow.ShowTriggeredAlarm(alarm.Id, alarm.Message, Configuration.AlarmSoundId, alarmTimeText, !alarm.SnoozeEnabled);
        AlarmOverlayWindow.IsOpen = true;
        if (Configuration.AlarmSoundRepeats)
            StartRepeatingAlarmSound(alarm.Id, Configuration.AlarmSoundId);
    }

    private string FormatAlarmSessionTime(DateTime local, string timeZoneId)
    {
        var hour = local.Hour % 12;
        if (hour == 0)
            hour = 12;

        var suffix = local.Hour >= 12 ? "P.M" : "A.M";
        var zone = TimeZoneHelper.ToShortText(timeZoneId);
        return $"{hour:00}:{local.Minute:00} {suffix} {zone}";
    }

    public void SnoozeAlarmFromOverlay(Guid alarmId, int minutes)
    {
        var alarm = Configuration.Alarms.FirstOrDefault(a => a.Id == alarmId);
        if (alarm == null)
            return;

        alarm.SnoozeEnabled = true;
        alarm.SnoozeMinutes = Math.Clamp(minutes, 1, 120);
        alarm.SnoozedUntilUtc = TimeZoneHelper.IsEorzeaTime(alarm.GetEffectiveTimeZoneId())
            ? TimeZoneHelper.AddEorzeaMinutes(DateTime.UtcNow, alarm.SnoozeMinutes)
            : DateTime.UtcNow.AddMinutes(alarm.SnoozeMinutes);
        alarm.SnoozeTriggered = false;
        alarm.SnoozeCanceled = false;
        alarm.HasTriggered = true;
        alarm.Enabled = true;
        recentAlarmIds.Remove(alarm.Id);
        StopRepeatingAlarmSound(alarmId);
        Configuration.Save();
        ShowAlarmToast(T("Alarm snoozed for the next 10 minutes"));
    }

    public void DismissAlarmOverlaySession(Guid alarmId)
    {
        StopRepeatingAlarmSound(alarmId);
    }

    public void StopAlarmOverlaySessionSound()
    {
        StopRepeatingAlarmSound(Guid.Empty, true);
    }

    private void StartRepeatingAlarmSound(Guid alarmId, int soundId)
    {
        if (soundId <= 0)
            return;

        repeatingAlarmSoundId = alarmId;
        repeatingAlarmSoundEffectId = Math.Clamp(soundId, MinAlarmSoundEffectId, MaxAlarmSoundEffectId);
        repeatingAlarmSoundNextUtc = DateTime.UtcNow.AddSeconds(1.0);
    }

    private void StopRepeatingAlarmSound(Guid alarmId, bool force = false)
    {
        if (!force && repeatingAlarmSoundId != alarmId)
            return;

        repeatingAlarmSoundId = Guid.Empty;
        repeatingAlarmSoundEffectId = 0;
        repeatingAlarmSoundNextUtc = DateTime.MinValue;
    }

    private void CheckRepeatingAlarmSound()
    {
        if (repeatingAlarmSoundId == Guid.Empty || repeatingAlarmSoundEffectId <= 0)
            return;

        // Repeating sounds are tied to an active triggered-session overlay; closing the session always stops the loop.
        if (!AlarmOverlayWindow.IsOpen || !AlarmOverlayWindow.HasTriggeredAlarmSession)
        {
            StopRepeatingAlarmSound(Guid.Empty, true);
            return;
        }

        var now = DateTime.UtcNow;
        if (now < repeatingAlarmSoundNextUtc)
            return;

        PlayAlarmSoundEffect(repeatingAlarmSoundEffectId);
        repeatingAlarmSoundNextUtc = now.AddSeconds(1.0);
    }

    private void ShowAlarmToast(string message)
    {
        toastGui.ShowQuest(message, new QuestToastOptions
        {
            PlaySound = false
        });
    }

    public void RunAlarmAnimationPreview()
    {
        alarmOverlayPreviewActive = true;
        alarmOverlayTriggerUtc = DateTime.UtcNow;
        alarmOverlayTimeZoneId = Configuration.SelectedTimeZoneId;
        alarmOverlayUntilUtc = DateTime.UtcNow.AddSeconds(5.0);
    }

    public bool TryGetAlarmOverlayVisual(out DateTime utc, out string timeZoneId, out float progress)
    {
        var nowUtc = DateTime.UtcNow;
        if (alarmOverlayUntilUtc <= nowUtc || alarmOverlayTriggerUtc <= DateTime.MinValue || string.IsNullOrWhiteSpace(alarmOverlayTimeZoneId))
        {
            utc = DateTime.MinValue;
            timeZoneId = string.Empty;
            progress = 1.0f;
            alarmOverlayPreviewActive = false;
            return false;
        }

        if (!alarmOverlayPreviewActive && !Configuration.AlarmAnimationsEnabled)
        {
            utc = DateTime.MinValue;
            timeZoneId = string.Empty;
            progress = 1.0f;
            return false;
        }

        utc = alarmOverlayTriggerUtc;
        timeZoneId = alarmOverlayTimeZoneId;
        progress = 1.0f - (float)Math.Clamp((alarmOverlayUntilUtc - nowUtc).TotalSeconds / 5.0, 0.0, 1.0);
        return true;
    }


    private void UpdateMaintenanceDetection(DateTime nowUtc)
    {
        if (maintenanceRefreshTask != null)
        {
            if (!maintenanceRefreshTask.IsCompleted)
                return;

            var wasManual = maintenanceManual;
            try
            {
                var maintenance = maintenanceRefreshTask.Result;
                var result = ApplyMaintenanceRefreshResult(maintenance);
                if (wasManual)
                {
                    Configuration.LastMaintenanceCheckStatus = result;
                    Configuration.Save();
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed checking Lodestone maintenance news.");
                if (wasManual)
                {
                    Configuration.LastMaintenanceCheckStatus = string.Format(CultureInfo.InvariantCulture, T("Maintenance check failed: {0}"), ex.Message);
                    Configuration.Save();
                }
            }
            finally
            {
                maintenanceRefreshTask = null;
                maintenanceManual = false;
                if (lodestoneCheckKind == LodestoneCheckKind.Maintenance)
                    lodestoneCheckKind = LodestoneCheckKind.None;
                StartNextQueuedLodestoneCheck(DateTime.UtcNow);
            }
        }

        if (!Configuration.MaintenanceReminderEnabled && !Configuration.ShowMaintenanceOnOverlay)
            return;

        if ((nowUtc - lastMaintenanceCheckUtc).TotalHours < 2 || maintenanceQueued)
            return;

        RequestMaintenanceRefresh(false);
    }

    private string ApplyMaintenanceRefreshResult(LodestoneMaintenanceInfo? maintenance)
    {
        Configuration.LastMaintenanceDetectionTimestampUtc = DateTime.UtcNow;

        if (maintenance == null)
        {
            Configuration.Save();
            return T("No active or upcoming maintenance notice was found.");
        }

        bool changed =
            !string.Equals(Configuration.LastMaintenanceNewsUrl, maintenance.Url, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Configuration.LastMaintenanceNewsTitle, maintenance.Title, StringComparison.Ordinal) ||
            !string.Equals(Configuration.DetectedMaintenanceDateTimeText, maintenance.LocalStartText, StringComparison.Ordinal) ||
            !string.Equals(Configuration.DetectedMaintenanceTimeZoneText, maintenance.TimeZoneText, StringComparison.Ordinal) ||
            !string.Equals(Configuration.LastDetectedMaintenanceMessage, maintenance.BuildSummary(), StringComparison.Ordinal) ||
            Configuration.DetectedMaintenanceStartUtc != maintenance.StartUtc ||
            !Configuration.HasDetectedMaintenanceTime;

        Configuration.LastMaintenanceNewsTitle = maintenance.Title;
        Configuration.LastMaintenanceNewsUrl = maintenance.Url;
        Configuration.LastDetectedMaintenanceMessage = maintenance.BuildSummary();
        Configuration.DetectedMaintenanceDateTimeText = maintenance.LocalStartText;
        Configuration.DetectedMaintenanceTimeZoneText = maintenance.TimeZoneText;
        Configuration.DetectedMaintenanceStartUtc = maintenance.StartUtc;
        Configuration.HasDetectedMaintenanceTime = true;
        EnsureMaintenanceReminderAlarms(DateTime.UtcNow, maintenance.StartUtc);
        Configuration.Save();

        return changed
            ? string.Format(CultureInfo.InvariantCulture, T("Maintenance notice found: {0}"), maintenance.Title)
            : string.Format(CultureInfo.InvariantCulture, T("Maintenance checked. Latest detected maintenance is already current: {0}."), $"{maintenance.LocalStartText} {maintenance.TimeZoneText}");
    }

    private void CheckMaintenanceReminder(DateTime nowUtc)
    {
        if (!Configuration.MaintenanceReminderEnabled)
            return;

        DateTime maintenanceUtc;

        if (Configuration.DetectedMaintenanceStartUtc > DateTime.MinValue)
        {
            maintenanceUtc = DateTime.SpecifyKind(Configuration.DetectedMaintenanceStartUtc, DateTimeKind.Utc);
        }
        else if (!string.IsNullOrWhiteSpace(Configuration.DetectedMaintenanceDateTimeText) &&
                 TimeZoneHelper.TryParseInZone(
                     Configuration.DetectedMaintenanceDateTimeText,
                     Configuration.SelectedTimeZoneId,
                     out var legacyMaintenanceUtc))
        {
            maintenanceUtc = legacyMaintenanceUtc;
        }
        else
        {
            return;
        }

        EnsureMaintenanceReminderAlarms(nowUtc, maintenanceUtc);
        CheckMaintenanceLead(nowUtc, maintenanceUtc, TimeSpan.FromHours(5), Configuration.MaintenanceRemind24Hours, T("5 hours"));
        CheckMaintenanceLead(nowUtc, maintenanceUtc, TimeSpan.FromHours(1), Configuration.MaintenanceRemind1Hour, T("1 hour"));
        CheckMaintenanceLead(nowUtc, maintenanceUtc, TimeSpan.FromMinutes(15), Configuration.MaintenanceRemind15Minutes, T("15 minutes"));
    }

    private void EnsureMaintenanceReminderAlarms(DateTime nowUtc, DateTime maintenanceUtc)
    {
        if (!Configuration.MaintenanceReminderEnabled)
            return;

        var changed = false;
        changed |= EnsureMaintenanceReminderAlarm(nowUtc, maintenanceUtc, TimeSpan.FromHours(5), Configuration.MaintenanceRemind24Hours, T("5 hours"));
        changed |= EnsureMaintenanceReminderAlarm(nowUtc, maintenanceUtc, TimeSpan.FromHours(1), Configuration.MaintenanceRemind1Hour, T("1 hour"));
        changed |= EnsureMaintenanceReminderAlarm(nowUtc, maintenanceUtc, TimeSpan.FromMinutes(15), Configuration.MaintenanceRemind15Minutes, T("15 minutes"));

        if (changed)
            Configuration.Save();
    }

    private bool EnsureMaintenanceReminderAlarm(DateTime nowUtc, DateTime maintenanceUtc, TimeSpan lead, bool enabled, string leadText)
    {
        if (!enabled)
            return false;

        var alarmUtc = maintenanceUtc - lead;
        if (alarmUtc < nowUtc.AddMinutes(-1))
            return false;

        var zoneId = TimeZoneHelper.NormalizeTimeZoneId(Configuration.SelectedTimeZoneId);
        var local = TimeZoneHelper.ConvertFromUtcForDisplay(alarmUtc, zoneId);
        var dateTimeText = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var message = string.Format(CultureInfo.InvariantCulture, T("Maintenance in {0}"), leadText);

        var exists = Configuration.Alarms.Any(alarm =>
            string.Equals(alarm.GetEffectiveTimeZoneId(), zoneId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(alarm.DateTimeText, dateTimeText, StringComparison.Ordinal) &&
            string.Equals(alarm.Message, message, StringComparison.Ordinal));

        if (exists)
            return false;

        Configuration.Alarms.Add(new AlarmEntry
        {
            DateTimeText = dateTimeText,
            Message = message,
            TimeZoneId = zoneId,
            Enabled = true,
            HasTriggered = false,
            SnoozedUntilUtc = DateTime.MinValue,
            SpecialTriggerUtc = DateTime.MinValue,
            SnoozeCanceled = false,
            SnoozeTriggered = false,
            SnoozeEnabled = false,
            SnoozeMinutes = 5,
            RepeatMode = AlarmRepeatMode.None
        });

        return true;
    }

    private void CheckMaintenanceLead(DateTime nowUtc, DateTime maintenanceUtc, TimeSpan lead, bool enabled, string leadText)
    {
        if (!enabled)
            return;

        var targetMoment = maintenanceUtc - lead;
        if (nowUtc < targetMoment || (nowUtc - targetMoment).TotalSeconds > 60)
            return;

        var key = $"{maintenanceUtc:O}:{lead.TotalMinutes}";
        if (maintenanceKeys.Contains(key))
            return;

        maintenanceKeys.Add(key);

        var zone = string.IsNullOrWhiteSpace(Configuration.DetectedMaintenanceTimeZoneText)
            ? TimeZoneHelper.ToShortText(Configuration.SelectedTimeZoneId)
            : Configuration.DetectedMaintenanceTimeZoneText;

        var whenText = $"{Configuration.DetectedMaintenanceDateTimeText} {zone}";
        var message = string.Format(CultureInfo.InvariantCulture, T("Scheduled maintenance starts in {0}. ({1})"), leadText, whenText);
        chatGui.Print(message, "Clock");
        toastGui.ShowQuest(message, new QuestToastOptions
        {
            PlaySound = false
        });
    }

    private void CheckEvents(DateTime nowUtc, bool loginDetected)
    {
        UpdateEventCheck(nowUtc, loginDetected);
        var sendLoginNotice = loginEventNoticePending && eventTask == null && !eventQueued;
        CheckEventNotices(nowUtc, sendLoginNotice);
        if (sendLoginNotice)
            loginEventNoticePending = false;
    }

    private void UpdateEventCheck(DateTime nowUtc, bool loginDetected)
    {
        if (eventTask != null)
        {
            if (!eventTask.IsCompleted)
                return;

            var wasManual = eventTaskManual;
            try
            {
                var events = eventTask.Result;
                var result = ApplyEventCheck(events);
                if (wasManual)
                {
                    Configuration.LastEventCheckStatus = result;
                    Configuration.Save();
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed checking Lodestone seasonal events.");
                if (wasManual)
                {
                    Configuration.LastEventCheckStatus = string.Format(CultureInfo.InvariantCulture, T("Seasonal event check failed: {0}"), ex.Message);
                    Configuration.Save();
                }
            }
            finally
            {
                eventTask = null;
                eventTaskManual = false;
                if (lodestoneCheckKind == LodestoneCheckKind.SeasonalEvents)
                    lodestoneCheckKind = LodestoneCheckKind.None;
                StartNextQueuedLodestoneCheck(DateTime.UtcNow);
            }
        }

        if (!Configuration.EventReminderEnabled)
            return;

        if ((!loginDetected && (nowUtc - lastEventCheckUtc).TotalHours < 6) || eventQueued)
            return;

        RequestEventCheck(false);
    }

    private string ApplyEventCheck(IReadOnlyList<EventInfo> events)
    {
        Configuration.LastEventCheckUtc = DateTime.UtcNow;
        Configuration.Events = events.Select(e => new EventRecord
        {
            Key = e.Key,
            Name = e.Name,
            Url = e.Url,
            StartUtc = e.StartUtc,
            EndUtc = e.EndUtc
        }).ToList();

        Configuration.LastEventMessage = Configuration.Events.Count == 0
            ? ""
            : string.Join("\n", Configuration.Events.Select(FormatEventSummary));

        MakeEventAlarms(DateTime.UtcNow);
        Configuration.Save();

        return Configuration.Events.Count == 0
            ? "No active seasonal events found."
            : string.Format(CultureInfo.InvariantCulture, T("{0} active seasonal event(s) found."), Configuration.Events.Count);
    }

    private void CheckEventNotices(DateTime nowUtc, bool loginDetected)
    {
        if (!Configuration.EventReminderEnabled || Configuration.Events.Count == 0)
            return;

        foreach (var ev in Configuration.Events.ToArray())
        {
            if (ev.StartUtc > nowUtc || ev.EndUtc <= nowUtc)
                continue;

            if (loginDetected && Configuration.EventNotifyOnLogin)
                SendEventNotice(ev, $"login:{loginEventSession}", false, nowUtc);

            if (Configuration.EventNotifyOneWeekBeforeEnd && nowUtc >= ev.EndUtc.AddDays(-7))
                SendEventNotice(ev, "week", false, nowUtc);

            if (Configuration.EventNotifyOneDayBeforeEnd && nowUtc >= ev.EndUtc.AddDays(-1))
                SendEventNotice(ev, "day", false, nowUtc);

            if (Configuration.EventNotifyEveryday)
                SendEventNotice(ev, $"daily:{TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local):yyyyMMdd}", true, nowUtc);
        }

        Configuration.Save();
    }

    private void SendEventNotice(EventRecord ev, string reason, bool daily, DateTime nowUtc, bool force = false)
    {
        var key = $"{ev.Key}:{reason}";
        if (!force)
        {
            if (Configuration.EventNoticeKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                return;

            Configuration.EventNoticeKeys.Add(key);
            if (Configuration.EventNoticeKeys.Count > 250)
                Configuration.EventNoticeKeys.RemoveAt(0);
        }

        var message = daily
            ? DailyEventMsg(ev, nowUtc)
            : string.Format(CultureInfo.InvariantCulture, T("Seasonal event: {0} ({1} - {2} {3})"), ev.Name, FormatEventTime(ev.StartUtc), FormatEventTime(ev.EndUtc), LocalZoneShort());

        chatGui.Print(BuildColoredChatNotice("[EVENT]", message));
        toastGui.ShowQuest(message, new QuestToastOptions
        {
            PlaySound = false
        });
    }

    private int MakeEventAlarms(DateTime nowUtc, bool recreate = false)
    {
        if (!Configuration.EventReminderEnabled || !Configuration.EventCreateAlarms)
            return 0;

        var messageCulture = GetEventAlarmMessageCulture();
        var created = 0;
        foreach (var ev in Configuration.Events)
        {
            var utc = DateTime.SpecifyKind(ev.EndUtc, DateTimeKind.Utc).AddDays(-1);
            if (utc <= nowUtc)
                continue;

            var key = BuildEventAlarmKey(ev, utc);
            if (!recreate && Configuration.EventAlarmKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;

            if (recreate)
            {
                Configuration.EventAlarmKeys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                RemoveMatchingEventAlarms(ev, utc);
            }

            Configuration.Alarms.Add(BuildEventAlarm(ev, utc, messageCulture));
            Configuration.EventAlarmKeys.Add(key);
            if (Configuration.EventAlarmKeys.Count > 250)
                Configuration.EventAlarmKeys.RemoveAt(0);

            created++;
        }

        return created;
    }

    public bool RecreateEventAlarms()
    {
        var created = MakeEventAlarms(DateTime.UtcNow, true);
        Configuration.LastEventCheckStatus = created switch
        {
            0 => T("No future seasonal event alarms could be recreated."),
            1 => T("Seasonal event alarm recreated."),
            _ => string.Format(CultureInfo.InvariantCulture, T("Seasonal event alarms recreated: {0}."), created)
        };
        Configuration.Save();
        return created > 0;
    }

    private static string BuildEventAlarmKey(EventRecord ev, DateTime utc)
    {
        return $"{ev.Key}:alarm:{utc:O}";
    }

    private AlarmEntry BuildEventAlarm(EventRecord ev, DateTime utc, string messageCulture)
    {
        var zoneId = TimeZoneHelper.NormalizeTimeZoneId(Configuration.SelectedTimeZoneId);
        var local = TimeZoneHelper.ConvertFromUtcForDisplay(utc, zoneId);
        var messageTemplate = ClockLocalizationService.Translate(messageCulture, "1 Day before {0} Ends");
        return new AlarmEntry
        {
            DateTimeText = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Message = string.Format(CultureInfo.InvariantCulture, messageTemplate, ev.Name),
            TimeZoneId = zoneId,
            Enabled = true,
            HasTriggered = false,
            SnoozedUntilUtc = DateTime.MinValue,
            SpecialTriggerUtc = DateTime.MinValue,
            SnoozeCanceled = false,
            SnoozeTriggered = false,
            SnoozeEnabled = false,
            SnoozeMinutes = 5,
            RepeatMode = AlarmRepeatMode.None
        };
    }

    private string GetEventAlarmMessageCulture()
    {
        var selected = string.IsNullOrWhiteSpace(Configuration.UiLanguageCultureName)
            ? "en-US"
            : Configuration.UiLanguageCultureName;

        if (!IsEnglishCulture(selected))
            return selected;

        var clientCulture = GetClientLanguageCulture();
        return !IsEnglishCulture(clientCulture) ? clientCulture : selected;
    }

    private string GetClientLanguageCulture()
    {
        return clientState.ClientLanguage.ToString() switch
        {
            "Japanese" or "Jp" or "Ja" => "ja-JP",
            "French" or "Fr" => "fr-FR",
            "German" or "De" => "de-DE",
            _ => "en-US"
        };
    }

    private static bool IsEnglishCulture(string cultureName)
    {
        return string.Equals(cultureName, "en-US", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cultureName, "en-GB", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cultureName, "en", StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveMatchingEventAlarms(EventRecord ev, DateTime utc)
    {
        Configuration.Alarms.RemoveAll(alarm =>
            alarm.Message.Contains(ev.Name, StringComparison.OrdinalIgnoreCase) &&
            alarm.TryGetStoredTriggerUtc(out var triggerUtc) &&
            DateTime.SpecifyKind(triggerUtc, DateTimeKind.Utc) == utc);
    }

    private string DailyEventMsg(EventRecord ev, DateTime nowUtc)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ev.StartUtc, DateTimeKind.Utc), TimeZoneInfo.Local);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ev.EndUtc, DateTimeKind.Utc), TimeZoneInfo.Local);
        var day = Math.Max(1, (int)Math.Floor((nowLocal.Date - startLocal.Date).TotalDays) + 1);
        var total = Math.Max(day, (int)Math.Ceiling((endLocal - startLocal).TotalDays));
        return string.Format(
            CultureInfo.InvariantCulture,
            T("{0} - Day {1} out of {2} ({3} - {4} {5} {6})"),
            ev.Name,
            day,
            total,
            startLocal.ToString("MMM dd", CultureInfo.InvariantCulture),
            endLocal.ToString("MMM dd", CultureInfo.InvariantCulture),
            endLocal.ToString("HH:mm", CultureInfo.InvariantCulture),
            LocalZoneShort());
    }

    public string FormatEventSummary(EventRecord ev)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            T("{0}: {1} to {2} ({3})"),
            ev.Name,
            FormatEventTime(ev.StartUtc),
            FormatEventTime(ev.EndUtc),
            LocalZoneShort());
    }

    private static string FormatEventTime(DateTime utc)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string LocalZoneShort()
    {
        return TimeZoneHelper.ToShortText(TimeZoneInfo.Local.Id);
    }

    private SeString ColoredEventMsg(string msg, EventRecord ev)
    {
        var startDate = LocalDate(ev.StartUtc);
        var endDate = LocalDate(ev.EndUtc);
        var endTime = LocalTime(ev.EndUtc);
        var zone = LocalZoneShort();
        var text = msg;
        var yellow = new List<(int Start, int End)>
        {
            FindRange(text, startDate),
            FindRange(text, endDate),
            FindRange(text, $"{endTime} {zone}")
        };

        var day = Regex.Match(text, @"Day\s+\d+", RegexOptions.IgnoreCase);
        if (day.Success)
            yellow.Add((day.Index, day.Index + day.Length));

        var total = Regex.Match(text, @"out\s+of\s+(?<count>\d+)", RegexOptions.IgnoreCase);
        if (total.Success)
        {
            var count = total.Groups["count"];
            yellow.Add((count.Index, count.Index + count.Length));
        }

        return RecolorEventMsg(text, yellow);
    }

    private SeString RecolorEventMsg(string text, IEnumerable<(int Start, int End)> yellow)
    {
        var builder = new SeStringBuilder();
        builder.Add(new UIForegroundPayload(45));

        var cursor = 0;
        foreach (var range in yellow.Where(r => r.Start >= 0 && r.End > r.Start).OrderBy(r => r.Start))
        {
            if (range.Start < cursor)
                continue;

            if (range.Start > cursor)
                builder.AddText(text[cursor..range.Start]);

            builder.Add(new UIForegroundPayload(559));
            builder.AddText(text[range.Start..range.End]);
            builder.Add(new UIForegroundPayload(45));
            cursor = range.End;
        }

        if (cursor < text.Length)
            builder.AddText(text[cursor..]);

        builder.Add(new UIForegroundPayload(0));
        return builder.BuiltString;
    }

    private static string LocalDate(DateTime utc)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("MMM dd", CultureInfo.InvariantCulture);
    }

    private static string LocalTime(DateTime utc)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static (int Start, int End) FindRange(string text, string value)
    {
        var index = text.IndexOf(value, StringComparison.Ordinal);
        return index < 0 ? (-1, -1) : (index, index + value.Length);
    }

    private void OnCommand(string command, string args)
    {
        args = args?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(args))
        {
            ToggleConfigUi();
            return;
        }

        if (HandleTimeZoneCompareCommand(args))
            return;

        var split = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = split[0].ToLowerInvariant();
        var rest = split.Length > 1 ? split[1] : string.Empty;

        switch (sub)
        {
            case "help":
                PrintHelp();
                return;

            case "on":
                SetMainUiOpen(true);
                chatGui.Print(T("Clock overlay opened."), "Clock");
                return;

            case "off":
                SetMainUiOpen(false);
                chatGui.Print(T("Clock overlay hidden."), "Clock");
                return;


            case "lock":
                Configuration.IsConfigWindowMovable = false;
                SaveAndNotify("Clock locked.");
                return;

            case "unlock":
                Configuration.IsConfigWindowMovable = true;
                SaveAndNotify("Clock unlocked.");
                return;

            case "timezone":
                HandleTimezoneCommand(rest);
                return;

            case "format":
                HandleFormatCommand(rest);
                return;

            case "colon":
                HandleColonCommand(rest);
                return;

            case "layout":
                HandleLayoutCommand(rest);
                return;

            case "preset":
                HandlePresetCommand(rest);
                return;

            case "profile":
                HandleProfileCommand(rest);
                return;


            default:
                PrintHelp();
                return;
        }
    }


    private bool HandleTimeZoneCompareCommand(string rawArgs)
    {
        var match = Regex.Match(rawArgs, @"^(.+?)\s+to\s+(.+?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var leftRaw = match.Groups[1].Value.Trim();
        var rightRaw = match.Groups[2].Value.Trim();
        if (!TimeZoneHelper.TryResolveTimeZone(leftRaw, out var leftId))
        {
            chatGui.PrintError(string.Format(CultureInfo.InvariantCulture, T("Invalid timezone: {0}"), leftRaw), "Clock");
            return true;
        }

        if (!TimeZoneHelper.TryResolveTimeZone(rightRaw, out var rightId))
        {
            chatGui.PrintError(string.Format(CultureInfo.InvariantCulture, T("Invalid timezone: {0}"), rightRaw), "Clock");
            return true;
        }

        var nowUtc = DateTime.UtcNow;
        var leftZone = TimeZoneHelper.GetTimeZone(leftId);
        var rightZone = TimeZoneHelper.GetTimeZone(rightId);
        var leftTime = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, leftZone);
        var rightTime = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, rightZone);
        var offsetDiff = rightZone.GetUtcOffset(nowUtc) - leftZone.GetUtcOffset(nowUtc);
        var diffText = BuildOffsetDifferenceText(offsetDiff);

        var leftLabel = BuildTypedTimeZoneLabel(leftRaw, leftId);
        var rightLabel = BuildTypedTimeZoneLabel(rightRaw, rightId);
        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("It is {0} {1} and {2} {3} {4}."), leftTime.ToString("HH:mm", CultureInfo.InvariantCulture), leftLabel, rightTime.ToString("HH:mm", CultureInfo.InvariantCulture), rightLabel, diffText), "Clock");
        return true;
    }

    private static string BuildTypedTimeZoneLabel(string raw, string resolvedId)
    {
        var trimmed = raw.Trim();
        if (Regex.IsMatch(trimmed, "^[A-Za-z]{2,5}$", RegexOptions.CultureInvariant))
            return trimmed.ToUpperInvariant();

        return TimeZoneHelper.ToShortText(resolvedId);
    }

    private string BuildOffsetDifferenceText(TimeSpan difference)
    {
        var totalMinutes = (int)Math.Round(Math.Abs(difference.TotalMinutes));
        if (totalMinutes == 0)
            return T("with no time difference");

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        var hourText = hours > 0 ? string.Format(CultureInfo.InvariantCulture, T(hours == 1 ? "{0} hour" : "{0} hours"), hours) : string.Empty;
        var minuteText = minutes > 0 ? string.Format(CultureInfo.InvariantCulture, T(minutes == 1 ? "{0} minute" : "{0} minutes"), minutes) : string.Empty;
        var combined = string.IsNullOrWhiteSpace(hourText) ? minuteText : string.IsNullOrWhiteSpace(minuteText) ? hourText : $"{hourText} {minuteText}";
        return string.Format(CultureInfo.InvariantCulture, T(difference.TotalMinutes > 0 ? "with {0} ahead difference" : "with {0} behind difference"), combined);
    }

    private void OnAlarmsCommand(string command, string args)
    {
        ToggleAlarmOverlay();
    }

    private void HandleTimezoneCommand(string rest)
    {
        if (!TimeZoneHelper.TryResolveTimeZone(rest, out var timeZoneId))
        {
            chatGui.PrintError(T("Invalid timezone. Use a valid TimeZoneInfo ID like \"Eastern Standard Time\" or \"America/New_York\"."), "Clock");
            return;
        }

        Configuration.SelectedTimeZoneId = timeZoneId;
        Configuration.Save();

        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Timezone set to {0}."), TimeZoneHelper.GetComboLabel(timeZoneId)), "Clock");
    }

    private void HandleFormatCommand(string rest)
    {
        rest = rest.Trim().ToLowerInvariant();

        switch (rest)
        {
            case "12":
            case "12h":
                Configuration.TimeFormat = ClockTimeFormat.TwelveHour;
                NormalizeAlarmEditorHourForNewFormat();
                SaveAndNotify("Time format set to 12h.");
                return;

            case "24":
            case "24h":
                Configuration.TimeFormat = ClockTimeFormat.TwentyFourHour;
                NormalizeAlarmEditorHourForNewFormat();
                SaveAndNotify("Time format set to 24h.");
                return;

            case "12s":
            case "12sec":
            case "12seconds":
                Configuration.TimeFormat = ClockTimeFormat.TwelveHourSeconds;
                NormalizeAlarmEditorHourForNewFormat();
                SaveAndNotify("Time format set to 12h with seconds.");
                return;

            case "24s":
            case "24sec":
            case "24seconds":
                Configuration.TimeFormat = ClockTimeFormat.TwentyFourHourSeconds;
                NormalizeAlarmEditorHourForNewFormat();
                SaveAndNotify("Time format set to 24h with seconds.");
                return;

            case "weekday":
            case "day":
                Configuration.TimeFormat = ClockTimeFormat.WeekdayTwentyFourHour;
                NormalizeAlarmEditorHourForNewFormat();
                SaveAndNotify("Time format set to weekday + 24h.");
                return;

            case "date":
                Configuration.TimeFormat = ClockTimeFormat.DateTwentyFourHour;
                NormalizeAlarmEditorHourForNewFormat();
                SaveAndNotify("Time format set to date + 24h.");
                return;

            default:
                chatGui.PrintError(T("Invalid format. Use 12, 24, 12s, 24s, weekday or date."), "Clock");
                return;
        }
    }

    private void NormalizeAlarmEditorHourForNewFormat()
    {
        if (TimeFormatHelper.UsesTwelveHourClock(Configuration.TimeFormat))
        {
            int sourceHour24;

            if (Configuration.AlarmEditorHour >= 0 && Configuration.AlarmEditorHour <= 23)
            {
                sourceHour24 = Configuration.AlarmEditorHour;
            }
            else
            {
                var nowInZone = TimeZoneHelper.ConvertFromUtc(DateTime.UtcNow, Configuration.SelectedTimeZoneId);
                sourceHour24 = nowInZone.Hour;
            }

            Configuration.AlarmEditorIsPm = sourceHour24 >= 12;
            var hour12 = sourceHour24 % 12;
            Configuration.AlarmEditorHour = hour12 == 0 ? 12 : hour12;
        }
        else
        {
            var selectedHour12 = Math.Clamp(Configuration.AlarmEditorHour, 1, 12);
            var hour24 = selectedHour12 % 12;
            if (Configuration.AlarmEditorIsPm)
                hour24 += 12;

            Configuration.AlarmEditorHour = Math.Clamp(hour24, 0, 23);
        }

        Configuration.Save();
    }

    private void HandleColonCommand(string rest)
    {
        rest = rest.Trim().ToLowerInvariant();

        Configuration.ColonAnimation = rest switch
        {
            "default" or "blink" => ColonAnimationMode.Blink,
            "always" => ColonAnimationMode.AlwaysVisible,
            "hidden" or "off" => ColonAnimationMode.Hidden,
            "slow" => ColonAnimationMode.SlowBlink,
            "fast" => ColonAnimationMode.FastBlink,
            _ => Configuration.ColonAnimation
        };

        if (rest is not ("default" or "blink" or "always" or "hidden" or "off" or "slow" or "fast"))
        {
            chatGui.PrintError(T("Invalid colon mode. Use default, always, hidden, slow or fast."), "Clock");
            return;
        }

        Configuration.Save();
        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Colon animation set to {0}."), Configuration.ColonAnimation), "Clock");
    }

    private void HandleLayoutCommand(string rest)
    {
        rest = rest.Trim().ToLowerInvariant();

        var profile = Configuration.GetActiveProfile();

        switch (rest)
        {
            case "horizontal":
                profile.LayoutMode = ClockLayoutMode.Horizontal;
                break;

            case "vertical":
                profile.LayoutMode = ClockLayoutMode.Vertical;
                break;

            default:
                chatGui.PrintError(T("Invalid layout. Use horizontal or vertical."), "Clock");
                return;
        }

        Configuration.Save();
        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Layout set to {0}."), profile.LayoutMode), "Clock");
    }

    private void HandlePresetCommand(string rest)
    {
        rest = rest.Trim().ToLowerInvariant();

        var preset = rest switch
        {
            "classic" => ClockPreset.Classic,
            "minimal" => ClockPreset.Minimal,
            "gold" => ClockPreset.GoldHud,
            "retro" => ClockPreset.RetroPanel,
            "blue" or "crystal" or "crystalblue" => ClockPreset.CrystalBlue,
            "dark" or "dalamud" or "dalamuddark" => ClockPreset.DalamudDark,
            "white" or "clean" or "cleanwhite" => ClockPreset.CleanWhite,
            "purple" or "neon" or "neonpurple" => ClockPreset.NeonPurple,
            "casino" or "casinogold" => ClockPreset.CasinoGold,
            "transparent" or "compact" or "compacttransparent" => ClockPreset.CompactTransparent,
            "raid" or "raidminimal" => ClockPreset.RaidMinimal,
            "tech" or "technology" => ClockPreset.Tech,
            "digital" => ClockPreset.Digital,
            "countdown" or "flip" or "flap" => ClockPreset.Countdown,
            _ => ClockPreset.Classic
        };

        if (rest is not ("classic" or "minimal" or "gold" or "retro" or "blue" or "crystal" or "crystalblue" or "dark" or "dalamud" or "dalamuddark" or "white" or "clean" or "cleanwhite" or "purple" or "neon" or "neonpurple" or "casino" or "casinogold" or "transparent" or "compact" or "compacttransparent" or "raid" or "raidminimal"))
        {
            chatGui.PrintError(T("Invalid preset. Use classic, minimal, gold, retro, blue, dark, white, purple, casino, transparent or raid."), "Clock");
            return;
        }

        Configuration.PreviewPresetSelection = preset;
        Configuration.Save();
        chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Preset selected: {0}."), preset), "Clock");
    }

    private void HandleProfileCommand(string rest)
    {
        var split = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = split.Length > 0 ? split[0].ToLowerInvariant() : string.Empty;
        var value = split.Length > 1 ? split[1] : string.Empty;

        switch (sub)
        {
            case "next":
                Configuration.ActiveProfileIndex = (Configuration.ActiveProfileIndex + 1) % Configuration.Profiles.Count;
                Configuration.Save();
                chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Active profile: {0}"), Configuration.GetActiveProfile().Name), "Clock");
                return;

            case "list":
                var list = string.Join(", ", Configuration.Profiles.Select((p, i) => $"{i + 1}:{p.Name}"));
                chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Profiles: {0}"), list), "Clock");
                return;

            case "set":
                if (int.TryParse(value, out var idx) && idx >= 1 && idx <= Configuration.Profiles.Count)
                {
                    Configuration.ActiveProfileIndex = idx - 1;
                    Configuration.Save();
                    chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Active profile: {0}"), Configuration.GetActiveProfile().Name), "Clock");
                }
                else
                {
                    chatGui.PrintError(T("Invalid profile index."), "Clock");
                }

                return;

            case "add":
                {
                    var name = string.IsNullOrWhiteSpace(value)
                        ? $"Profile {Configuration.Profiles.Count + 1}"
                        : value.Trim();

                    Configuration.AddProfile(name);
                    Configuration.Save();
                    chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Profile \"{0}\" created."), Configuration.GetActiveProfile().Name), "Clock");
                    return;
                }

            case "rename":
                if (string.IsNullOrWhiteSpace(value))
                {
                    chatGui.PrintError(T("Provide a new profile name."), "Clock");
                    return;
                }

                Configuration.GetActiveProfile().Name = value.Trim();
                Configuration.Save();
                chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Profile renamed to \"{0}\"."), Configuration.GetActiveProfile().Name), "Clock");
                return;

            case "delete":
                if (Configuration.Profiles.Count <= 1)
                {
                    chatGui.PrintError(T("At least one profile must remain."), "Clock");
                    return;
                }

                var removed = Configuration.GetActiveProfile().Name;
                Configuration.DeleteActiveProfile();
                Configuration.Save();
                chatGui.Print(string.Format(CultureInfo.InvariantCulture, T("Profile \"{0}\" deleted."), removed), "Clock");
                return;

            default:
                chatGui.PrintError(T("Use: /clock profile next|list|set <n>|add <name>|rename <name>|delete"), "Clock");
                return;
        }
    }

    private void PrintHelp()
    {
        chatGui.Print(T("/clock - open settings"), "Clock");
        chatGui.Print(T("/clock on - show clock overlay"), "Clock");
        chatGui.Print(T("/clock off - hide clock overlay"), "Clock");
        chatGui.Print(T("/alarms - open alarm overlay"), "Clock");
        chatGui.Print(T("/clock timezone <TimeZoneInfo ID or alias>"), "Clock");
        chatGui.Print(T("/clock format 12|24|12s|24s|weekday|date"), "Clock");
        chatGui.Print(T("/clock colon default|always|hidden|slow|fast"), "Clock");
        chatGui.Print(T("/clock layout horizontal|vertical"), "Clock");
        chatGui.Print(T("/clock <timezone1> to <timezone2> - compare current time between two timezones"), "Clock");
        chatGui.Print(T("/clock lock | /clock unlock"), "Clock");
        chatGui.Print(T("/clock profile next|list|set <n>|add <name>|rename <name>|delete"), "Clock");
    }

    private void SaveAndNotify(string message)
    {
        Configuration.Save();
        chatGui.Print(T(message), "Clock");
    }

    public void SendAlarmOutput(string message)
    {
        chatGui.Print(BuildColoredAlarmMessage(message));
        ShowAlarmToast(message);
        PlayAlarmSoundEffect(Configuration.AlarmSoundId);
    }

    public void TestAlarmOutput(string message)
    {
        SendAlarmOutput(message);
    }

    public void PlaySelectedAlarmSoundOnly()
    {
        PlayAlarmSoundEffect(Configuration.AlarmSoundId);
    }

    // Uses the game's normal chat sound helper for alarm previews/triggers. The id is clamped to the known chat-sound range
    // before calling into the client, so bad config values cannot request arbitrary sound ids.
    private unsafe void PlayAlarmSoundEffect(int soundId)
    {
        if (soundId <= 0)
            return;

        try
        {
            UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(soundId, MinAlarmSoundEffectId, MaxAlarmSoundEffectId));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to play Clock alarm sound effect.");
        }
    }

    private SeString BuildColoredAlarmMessage(string message)
    {
        return BuildColoredChatNotice("[ALARM]", message);
    }

    private SeString BuildColoredChatNotice(string title, string message)
    {
        var builder = new SeStringBuilder();

        builder.Add(new UIForegroundPayload(559));
        builder.AddText(title);
        builder.Add(new UIForegroundPayload(0));
        builder.AddText("  ");

        builder.Add(new UIForegroundPayload(45));
        builder.AddText(message);
        builder.Add(new UIForegroundPayload(0));

        return builder.BuiltString;
    }

    public bool TryExportConfiguration(string path, out string error)
    {
        try
        {
            Configuration.EnsureInitialized();
            ConfigurationFileService.Export(Configuration, path);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to export Clock configuration.");
            error = ex.Message;
            return false;
        }
    }

    public bool TryImportConfiguration(string path, out string error)
    {
        try
        {
            var importedConfiguration = ConfigurationFileService.Import(path);
            importedConfiguration.Initialize(pluginInterface);
            importedConfiguration.EnsureInitialized();

            Configuration.CopyPublicStateFrom(importedConfiguration);
            Configuration.Initialize(pluginInterface);
            Configuration.EnsureInitialized();
            Configuration.Save();

            maintenanceKeys.Clear();
            recentAlarmIds.Clear();
            pluginInterface.UiBuilder.DisableCutsceneUiHide = !Configuration.HideDuringCutscenes;

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to import Clock configuration.");
            error = ex.Message;
            return false;
        }
    }

    public string T(string text)
    {
        return ClockLocalizationService.Translate(Configuration.UiLanguageCultureName, text);
    }

    public void TestEventNotice()
    {
        var nowUtc = DateTime.UtcNow;
        var ev = Configuration.Events
            .Where(e => e.StartUtc <= nowUtc && e.EndUtc > nowUtc)
            .OrderBy(e => e.EndUtc)
            .FirstOrDefault();

        if (ev != null)
        {
            SendSafeTestEventNotice(ev, nowUtc);
            Configuration.Save();
            return;
        }

        var sampleEnd = nowUtc.AddDays(14).Date.AddHours(23).AddMinutes(59);
        var sample = new EventRecord
        {
            Key = $"test-{nowUtc:yyyyMMddHHmmss}",
            Name = T("Test Event"),
            Url = "",
            StartUtc = nowUtc.AddDays(-12),
            EndUtc = sampleEnd
        };
        SendSafeTestEventNotice(sample, nowUtc);
    }

    private void SendSafeTestEventNotice(EventRecord ev, DateTime nowUtc)
    {
        var message = DailyEventMsg(ev, nowUtc);
        chatGui.Print(BuildColoredChatNotice("[EVENT]", message));
        toastGui.ShowQuest(message, new QuestToastOptions
        {
            PlaySound = false
        });
    }

    public bool IsMaintenanceRefreshRunning => maintenanceRefreshTask is { IsCompleted: false };

    public bool IsMaintenanceCheckQueued => maintenanceQueued;

    public bool IsEventCheckRunning => eventTask is { IsCompleted: false };

    public bool IsEventCheckQueued => eventQueued;

    public bool IsLodestoneCheckRunning => IsMaintenanceRefreshRunning || IsEventCheckRunning;

    public bool RequestEventCheck(bool forceRefresh)
    {
        if (IsEventCheckRunning || eventQueued)
            return false;

        var nowUtc = DateTime.UtcNow;
        if (IsMaintenanceRefreshRunning)
        {
            QueueEventCheck(nowUtc, forceRefresh);
            return true;
        }

        StartEventCheck(nowUtc, forceRefresh);
        return true;
    }

    private void QueueEventCheck(DateTime nowUtc, bool manual)
    {
        lastEventCheckUtc = nowUtc;
        eventQueued = true;
        eventQueuedManual = manual;
        eventQueueOrder = ++lodestoneQueueOrder;
        Configuration.LastEventCheckUtc = nowUtc;
        if (manual)
            Configuration.LastEventCheckStatus = T("Seasonal event check queued.");
        Configuration.Save();
    }

    private void StartEventCheck(DateTime nowUtc, bool manual)
    {
        lastEventCheckUtc = nowUtc;
        eventQueued = false;
        eventQueuedManual = false;
        eventQueueOrder = 0;
        eventTaskManual = manual;
        lodestoneCheckKind = LodestoneCheckKind.SeasonalEvents;
        Configuration.LastEventCheckUtc = nowUtc;
        Configuration.LastEventCheckStatus = manual ? T("Checking Lodestone seasonal events...") : Configuration.LastEventCheckStatus;
        Configuration.Save();
        eventTask = eventService.GetActiveEventsAsync();
    }

    public bool RequestMaintenanceRefresh(bool forceRefresh)
    {
        if (IsMaintenanceRefreshRunning || maintenanceQueued)
            return false;

        var nowUtc = DateTime.UtcNow;
        if (IsEventCheckRunning)
        {
            QueueMaintenanceRefresh(nowUtc, forceRefresh);
            return true;
        }

        StartMaintenanceRefresh(nowUtc, forceRefresh);
        return true;
    }

    private void QueueMaintenanceRefresh(DateTime nowUtc, bool manual)
    {
        lastMaintenanceCheckUtc = nowUtc;
        maintenanceQueued = true;
        maintenanceQueuedManual = manual;
        maintenanceQueueOrder = ++lodestoneQueueOrder;
        Configuration.LastMaintenanceDetectionTimestampUtc = nowUtc;
        if (manual)
            Configuration.LastMaintenanceCheckStatus = T("Maintenance check queued.");
        Configuration.Save();
    }

    private void StartMaintenanceRefresh(DateTime nowUtc, bool manual)
    {
        lastMaintenanceCheckUtc = nowUtc;
        maintenanceQueued = false;
        maintenanceQueuedManual = false;
        maintenanceQueueOrder = 0;
        maintenanceManual = manual;
        lodestoneCheckKind = LodestoneCheckKind.Maintenance;
        Configuration.LastMaintenanceDetectionTimestampUtc = nowUtc;
        Configuration.LastMaintenanceCheckStatus = manual ? T("Checking Lodestone maintenance notices...") : Configuration.LastMaintenanceCheckStatus;
        Configuration.Save();

        maintenanceRefreshTask = maintenanceService.GetLatestMaintenanceAsync(
            Configuration.MaintenanceLanguage,
            Configuration.LastMaintenanceNewsUrl,
            Configuration.DetectedMaintenanceStartUtc,
            manual);
    }

    private void StartNextQueuedLodestoneCheck(DateTime nowUtc)
    {
        if (IsLodestoneCheckRunning)
            return;

        if (maintenanceQueued && (!eventQueued || maintenanceQueueOrder <= eventQueueOrder))
        {
            StartMaintenanceRefresh(nowUtc, maintenanceQueuedManual);
            return;
        }

        if (eventQueued)
            StartEventCheck(nowUtc, eventQueuedManual);
    }

    public void ClearRecentlyTriggeredAlarm(Guid alarmId)
    {
        recentAlarmIds.Remove(alarmId);
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void OpenConfigUiAtAlarms()
    {
        ConfigWindow.OpenToAlarmsTab();
    }

    public void ToggleAlarmOverlay() => AlarmOverlayWindow.Toggle();

    // Single entry point for showing /alarms so chat-created alarms, command handling and UI shortcuts all open the same overlay instance.
    public void OpenAlarmOverlay() => AlarmOverlayWindow.IsOpen = true;

    public void ToggleMainUi()
    {
        wantedMainWindowOpen = !wantedMainWindowOpen;
        MainWindow.IsOpen = wantedMainWindowOpen && !ShouldHideClock();
    }

    private void SetMainUiOpen(bool open)
    {
        wantedMainWindowOpen = open;
        MainWindow.IsOpen = wantedMainWindowOpen && !ShouldHideClock();
    }
}
