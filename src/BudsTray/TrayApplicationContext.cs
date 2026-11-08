using System.Drawing;
using System.Windows.Forms;
using BudsTray.Buds;
using Microsoft.Win32;

namespace BudsTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>
    /// The buds answer a ManagerInfo handshake with a status frame, so polling that
    /// once every 15 s keeps the numbers current while staying far below the cost of
    /// a reconnect. Status frames pushed by the buds are still processed immediately.
    /// </summary>
    private const int PollIntervalMs = 15_000;

    private readonly AppConfig _config;
    private readonly BudsClient _client;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenu _menu;
    private readonly Control _marshal;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _appWindowTimer;
    private readonly UserPreferenceChangedEventHandler _preferenceHandler;

    private string? _lastIconKey;
    private string? _lastTooltip;
    private bool _galaxyBudsWindowOpen;

    public TrayApplicationContext()
    {
        _config = AppConfig.Load();
        _client = new BudsClient(_config);

        _marshal = new Control();
        _ = _marshal.Handle; // force the handle so BeginInvoke works from client threads

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Icon = TrayIconRenderer.Render(null, null, leftCharging: false, rightCharging: false,
                connected: false, showPercentage: false, lightTheme: ThemeUtil.IsLightTheme(),
                windows11: ThemeUtil.IsWindows11),
            Text = "BudsTray · starting…"
        };
        _menu = new TrayMenu(
            () => _client.State == ConnectionState.Connected,
            () => _client.LastStatus,
            () => _client.NoiseMode,
            () => _client.DeviceName,
            mode => _ = _client.SetNoiseControlAsync(mode),
            _config,
            ExitApplication);
        _notifyIcon.ContextMenuStrip = _menu;

        _client.BatteryUpdated += _ => Post(RefreshUi);
        _client.NoiseControlChanged += _ => Post(RefreshUi);
        _client.StateChanged += (state, error) => Post(() =>
        {
            RefreshUi();
            if (state == ConnectionState.Connected) _ = _client.RequestStatusAsync();
        });

        _preferenceHandler = (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                _lastIconKey = null;
                Post(RefreshUi);
            }
        };
        SystemEvents.UserPreferenceChanged += _preferenceHandler;

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += (_, _) => _ = _client.RequestStatusAsync();
        _pollTimer.Start();

        // The official app is the other client of the exclusive SPP channel.
        // While its window is open, BudsTray steps aside so the user can use it;
        // the connection resumes when the window closes.
        _appWindowTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _appWindowTimer.Tick += (_, _) => SyncGalaxyBudsWindow();
        _appWindowTimer.Start();

        // Check before starting, so the tray does not fight a window that was
        // already open when it launched.
        SyncGalaxyBudsWindow();

        _client.Start();
        RefreshUi();
    }

    private void Post(Action action)
    {
        if (_marshal.IsDisposed) return;

        try
        {
            _marshal.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        catch (InvalidOperationException)
        {
            // Handle was destroyed between the check and the call.
        }
    }

    private void RefreshUi()
    {
        var light = ThemeUtil.IsLightTheme();
        var windows11 = ThemeUtil.IsWindows11;
        var connected = _client.State == ConnectionState.Connected;
        var status = _client.LastStatus;

        var left = connected ? status?.Left : null;
        var right = connected ? status?.Right : null;
        var leftCharging = connected && status?.LeftCharging == true;
        var rightCharging = connected && status?.RightCharging == true;
        var leftPresent = connected && status?.LeftPresent == true;
        var rightPresent = connected && status?.RightPresent == true;
        var iconSize = SystemInformation.SmallIconSize.Width;

        var iconKey =
            $"{connected}|{left}|{right}|{leftCharging}|{rightCharging}|" +
            $"{leftPresent}|{rightPresent}|" +
            $"{_config.ShowPercentage}|{light}|{iconSize}|{windows11}";

        if (iconKey != _lastIconKey)
        {
            _lastIconKey = iconKey;
            var icon = TrayIconRenderer.Render(
                left, right, leftCharging, rightCharging, connected,
                _config.ShowPercentage, light, windows11: windows11,
                leftPresent: leftPresent, rightPresent: rightPresent);
            var previous = _notifyIcon.Icon;
            _notifyIcon.Icon = icon;
            previous?.Dispose();
        }

        var tooltip = BuildTooltip(connected, status);
        if (tooltip != _lastTooltip)
        {
            _lastTooltip = tooltip;
            _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        }

        if (_menu.Visible) _menu.RefreshValues();
    }

    private string BuildTooltip(bool connected, BatteryStatus? status)
    {
        var name = _client.DeviceName ?? _config.DeviceName ?? "Galaxy Buds";

        // The handoff states win over "connecting…"/"disconnected" so the
        // tooltip explains why the tray is not showing fresh numbers.
        if (_client.PausedForGalaxyBudsApp) return $"{name} · Galaxy Buds app is open";
        if (_client.ChannelBusy) return $"{name} · Galaxy Buds app has it";
        if (_client.State == ConnectionState.Connecting) return $"{name} · connecting…";
        if (!connected) return $"{name} · disconnected";
        if (status == null) return $"{name} · waiting for battery…";

        return status.ToTooltip(name);
    }

    /// <summary>
    /// Hands the exclusive SPP channel over when the official Galaxy Buds window
    /// is opened, and takes it back when it closes. Samsung's app and BudsTray
    /// cannot be connected simultaneously; this makes them cooperate instead of
    /// closing each other.
    /// </summary>
    private void SyncGalaxyBudsWindow()
    {
        var open = GalaxyBudsApp.IsWindowOpen();
        if (open == _galaxyBudsWindowOpen) return;

        _galaxyBudsWindowOpen = open;

        if (open)
        {
            Log.Info("The Galaxy Buds app window is open; releasing the Bluetooth channel to it.");
            _ = PauseForGalaxyBudsAppAsync();
        }
        else
        {
            Log.Info("The Galaxy Buds app window closed; reconnecting.");
            _ = ResumeAfterGalaxyBudsAppAsync();
        }
    }

    private async Task PauseForGalaxyBudsAppAsync()
    {
        try
        {
            await _client.PauseAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Could not pause BudsTray for the Galaxy Buds app", ex);
        }

        Post(RefreshUi);
    }

    private async Task ResumeAfterGalaxyBudsAppAsync()
    {
        // Let the official app finish its own disconnect before asking for the
        // channel again; if the window was reopened meanwhile, stay paused.
        await Task.Delay(1500);

        if (_galaxyBudsWindowOpen) return;

        _client.Resume();
        Post(RefreshUi);
    }

    private void ExitApplication()
    {
        _pollTimer.Stop();
        _appWindowTimer.Stop();
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= _preferenceHandler;
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _appWindowTimer.Stop();
            _appWindowTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _marshal.Dispose();
            _client.Dispose();
            _config.Save();
        }

        base.Dispose(disposing);
    }
}
