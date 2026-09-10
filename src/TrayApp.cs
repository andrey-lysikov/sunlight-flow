//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Microsoft.Win32;

namespace SunlightFlow;

// No menus and no windows: a click toggles control, a double click on the grey
// icon quits. Settings live in a file.
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly LightEngine _engine = new();
    private readonly SynchronizationContext _ui;

    private readonly System.Windows.Forms.Timer _clickTimer;
    private readonly System.Windows.Forms.Timer _tooltipTimer;
    private readonly System.Windows.Forms.Timer _recoverTimer;

    private string _recoverReason = "";

    private readonly TrayIcons _icons = new();

    private bool _shuttingDown;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _icon = new NotifyIcon { Visible = true, Text = "Sunlight-Flow" };

        // A single click also precedes a double one, so it is deferred by the
        // double-click interval and cancelled when the second click arrives.
        _clickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime + 50 };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); _ = ToggleAsync(); };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _clickTimer.Stop();
            _clickTimer.Start();
        };

        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _clickTimer.Stop();

            if (_engine.IsEnabled) _ = ToggleAsync();
            else Shutdown();
        };

        _engine.StateChanged += () => _ui.Post(_ => UpdateIcon(), null);

        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _tooltipTimer.Tick += (_, _) => UpdateIcon();
        _tooltipTimer.Start();

        // Devices come back with a lag, and switches often arrive in pairs,
        // so recovery is debounced instead of firing on every event.
        _recoverTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _recoverTimer.Tick += (_, _) => { _recoverTimer.Stop(); Recover(); };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Diagnostics.Log($"start, version {typeof(TrayApp).Assembly.GetName().Version}, " +
                        $"package identity = {Startup.HasPackageIdentity}");

        _engine.StartLoop();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await Startup.EnsureEnabledAsync();

        // The app is meant to run in the background, so it takes control at once.
        await _engine.EnableAsync();
        UpdateIcon();

        await Updates.CheckAsync();
    }

    private async Task ToggleAsync()
    {
        try
        {
            await _engine.ToggleAsync();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"toggle failed: {e.Message}");
        }

        UpdateIcon();
    }

    private void UpdateIcon()
    {
        if (_shuttingDown) return;

        double daylight = 1.0 - _engine.SunFactor;
        bool sunUp = _engine.SunElevation > 0;
        _icon.Icon = _icons.Get(daylight, sunUp, _engine.IsEnabled);

        // By day the figure is how much daylight is left, at night it is how
        // much of the Moon is lit. The Moon never drives the lighting itself.
        string body = sunUp
            ? $"sun {daylight * 100:F0}%"
            : $"moon {Moon.Illumination(Moon.Phase(DateTime.UtcNow)) * 100:F0}%";

        if (_engine.IsBloodMoon) body = "blood moon";

        string text = _engine.IsEnabled
            ? $"Current brightness {_engine.DisplayLevel * 100:F0}%, {body}"
            : $"Off. {char.ToUpper(body[0])}{body[1..]}";

        // The tray tooltip is cut off at 63 characters.
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        ScheduleRecovery("resume from sleep");
    }

    // An RDP connection moves the console session aside and the lamps go with
    // it. Coming back has to reclaim them, and nothing else reports that.
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Diagnostics.Log($"session switch: {e.Reason}");

        bool regained = e.Reason
            is SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.RemoteDisconnect
            or SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.SessionLogon;

        if (regained) ScheduleRecovery($"session switch {e.Reason}");
    }

    // Switching the taskbar between light and dark flips the glyph colour.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Window)) return;

        _ui.Post(_ => { _icons.Invalidate(); UpdateIcon(); }, null);
    }

    private void ScheduleRecovery(string reason)
    {
        _ui.Post(_ =>
        {
            if (_shuttingDown) return;

            _recoverReason = reason;
            _recoverTimer.Stop();
            _recoverTimer.Start();
        }, null);
    }

    private void Recover()
    {
        try { _engine.Rediscover(_recoverReason); }
        catch (Exception e) { Diagnostics.Log($"recovery failed: {e.Message}"); }
    }

    private async void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        Diagnostics.Log("quitting on double click");

        _tooltipTimer.Stop();
        _clickTimer.Stop();
        _recoverTimer.Stop();

        try { await _engine.DisposeAsync(); }
        catch (Exception e) { Diagnostics.Log($"failed to release the lamps: {e.Message}"); }

        _icon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _tooltipTimer.Dispose();
            _clickTimer.Dispose();
            _recoverTimer.Dispose();
            _icon.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
