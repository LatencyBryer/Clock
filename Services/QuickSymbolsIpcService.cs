using System;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Clock.Services;

public sealed class QuickSymbolsIpcService : IDisposable
{
    private const string OwnerKey = "Clock.AlarmLabel";
    private const string IpcGetVersion = "QuickSymbols.GetVersion";
    private const string IpcOpenPicker = "QuickSymbols.OpenPicker";
    private const string IpcClosePicker = "QuickSymbols.ClosePicker";
    private const string IpcGetHotkey = "QuickSymbols.GetHotkey";
    private const string IpcSymbolSelected = "QuickSymbols.SymbolSelected";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IKeyState keyState;

    private ICallGateSubscriber<int>? getVersion;
    private ICallGateSubscriber<string, bool>? openPicker;
    private ICallGateSubscriber<string, bool>? closePicker;
    private ICallGateSubscriber<int[]>? getHotkey;
    private ICallGateSubscriber<string, string, object?>? symbolSelected;
    private Action<string, string>? symbolSelectedHandler;

    private bool keyWasDown;
    private bool unavailableLogged;
    private int inputGraceFrames;
    private int[] quickSymbolsHotkey = [];
    private int hotkeyRefreshFrames;
    private string queuedAlarmLabelText = string.Empty;
    private int focusAlarmLabelFrames;
    private int alarmLabelCaretFrames;

    public QuickSymbolsIpcService(IDalamudPluginInterface pluginInterface, IPluginLog log, IKeyState keyState)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.keyState = keyState;
        this.RegisterSubscribers();
    }

    public void Dispose()
    {
        if (this.symbolSelectedHandler != null)
        {
            this.symbolSelected?.Unsubscribe(this.symbolSelectedHandler);
            this.symbolSelectedHandler = null;
        }
    }

    public void WatchAlarmLabelInput(bool inputActive)
    {
        if (this.alarmLabelCaretFrames > 0)
        {
            this.alarmLabelCaretFrames--;
        }

        if (inputActive)
        {
            this.inputGraceFrames = 6;
        }
        else if (this.inputGraceFrames > 0)
        {
            this.inputGraceFrames--;
        }

        if (this.inputGraceFrames <= 0)
        {
            this.keyWasDown = false;
            return;
        }

        if (!this.IsQuickSymbolsHotkeyPressed())
        {
            this.keyWasDown = false;
            return;
        }

        if (this.keyWasDown)
        {
            return;
        }

        this.keyWasDown = true;
        this.ConsumeQuickSymbolsHotkey();
        this.OpenAlarmLabelPicker();
    }

    public string ConsumeAlarmLabelText()
    {
        var text = this.queuedAlarmLabelText;
        this.queuedAlarmLabelText = string.Empty;
        return text;
    }

    public bool ConsumeAlarmLabelFocusRequest()
    {
        if (this.focusAlarmLabelFrames <= 0)
        {
            return false;
        }

        this.focusAlarmLabelFrames--;
        return true;
    }

    public bool ShouldDrawAlarmLabelCaret()
    {
        return this.alarmLabelCaretFrames > 0;
    }

    private void RegisterSubscribers()
    {
        try
        {
            this.getVersion = this.pluginInterface.GetIpcSubscriber<int>(IpcGetVersion);
            this.openPicker = this.pluginInterface.GetIpcSubscriber<string, bool>(IpcOpenPicker);
            this.closePicker = this.pluginInterface.GetIpcSubscriber<string, bool>(IpcClosePicker);
            this.getHotkey = this.pluginInterface.GetIpcSubscriber<int[]>(IpcGetHotkey);
            this.symbolSelected = this.pluginInterface.GetIpcSubscriber<string, string, object?>(IpcSymbolSelected);

            this.symbolSelectedHandler = this.OnSymbolSelected;
            this.symbolSelected.Subscribe(this.symbolSelectedHandler);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "QuickSymbols IPC subscribers could not be created yet.");
        }
    }

    private void OpenAlarmLabelPicker()
    {
        try
        {
            this.RegisterSubscribersIfNeeded();
            _ = this.getVersion?.InvokeFunc();
            if (this.openPicker?.InvokeFunc(OwnerKey) == true)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            if (!this.unavailableLogged)
            {
                this.log.Debug(ex, "QuickSymbols IPC is not available. Alarm label symbol picker was not opened.");
                this.unavailableLogged = true;
            }
        }
    }

    private void RegisterSubscribersIfNeeded()
    {
        if (this.openPicker != null && this.symbolSelected != null && this.getHotkey != null)
        {
            return;
        }

        this.RegisterSubscribers();
    }

    private void OnSymbolSelected(string owner, string symbol)
    {
        if (!string.Equals(owner, OwnerKey, StringComparison.Ordinal) || string.IsNullOrEmpty(symbol))
        {
            return;
        }

        this.queuedAlarmLabelText += symbol;
        this.focusAlarmLabelFrames = 4;
        this.alarmLabelCaretFrames = 90;
    }

    private bool IsQuickSymbolsHotkeyPressed()
    {
        var keys = this.GetQuickSymbolsHotkey();
        if (keys.Length == 0)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (!IsKeyDown(key))
            {
                return false;
            }
        }

        return true;
    }

    private int[] GetQuickSymbolsHotkey()
    {
        if (this.hotkeyRefreshFrames > 0 && this.quickSymbolsHotkey.Length > 0)
        {
            this.hotkeyRefreshFrames--;
            return this.quickSymbolsHotkey;
        }

        try
        {
            this.RegisterSubscribersIfNeeded();
            var keys = this.getHotkey?.InvokeFunc() ?? [];
            this.quickSymbolsHotkey = keys.Where(key => key > 0).Distinct().ToArray();
            this.hotkeyRefreshFrames = 60;
            return this.quickSymbolsHotkey;
        }
        catch (Exception ex)
        {
            if (!this.unavailableLogged)
            {
                this.log.Debug(ex, "QuickSymbols hotkey IPC is not available.");
                this.unavailableLogged = true;
            }

            this.quickSymbolsHotkey = [];
            this.hotkeyRefreshFrames = 15;
            return this.quickSymbolsHotkey;
        }
    }

    private void ConsumeQuickSymbolsHotkey()
    {
        foreach (var key in this.quickSymbolsHotkey)
        {
            try
            {
                this.keyState[(VirtualKey)key] = false;
            }
            catch
            {
                // Raw key detection is what matters here. Clearing KeyState is best-effort only.
            }
        }
    }

    private static bool IsKeyDown(int key)
    {
        try
        {
            return (GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
