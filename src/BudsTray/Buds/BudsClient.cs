using System.Globalization;
using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace BudsTray.Buds;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

/// <summary>One SPP service UUID and the frame layout that belongs to it.</summary>
public sealed record BudsService(Guid Uuid, SppTransport Transport);

/// <summary>
/// Owns the RFCOMM/SPP connection to the earbuds, reconnects with backoff and
/// raises battery updates parsed from status frames.
/// </summary>
public sealed class BudsClient : IDisposable
{
    /// <summary>New SPP UUID: Buds2, Buds2 Pro, Buds FE, Buds Core, Buds3/3 Pro/3 FE, Buds4/4 Pro.</summary>
    public static readonly Guid SppServiceUuid = new("2e73a4ad-332d-41fc-90e2-16bef06523f2");

    /// <summary>Standard SPP UUID: Buds+, Buds Live and Buds Pro.</summary>
    public static readonly Guid SppStandardServiceUuid = new("00001101-0000-1000-8000-00805f9b34fb");

    /// <summary>Legacy UUID used by the original Galaxy Buds (2019).</summary>
    public static readonly Guid SppLegacyServiceUuid = new("00001102-0000-1000-8000-00805f9b34fd");

    /// <summary>Some Bluetooth stacks expose the 0x1102 service on the standard base UUID.</summary>
    public static readonly Guid SppLegacyStandardServiceUuid = new("00001102-0000-1000-8000-00805f9b34fb");

    /// <summary>
    /// Every service a Galaxy Buds generation may advertise, in the order they
    /// are preferred. The frame layout differs only for the original Buds.
    /// </summary>
    public static readonly IReadOnlyList<BudsService> ServiceCandidates = new[]
    {
        new BudsService(SppServiceUuid, SppTransport.Modern),
        new BudsService(SppStandardServiceUuid, SppTransport.Modern),
        new BudsService(SppLegacyServiceUuid, SppTransport.Legacy),
        new BudsService(SppLegacyStandardServiceUuid, SppTransport.Legacy)
    };

    private readonly AppConfig _config;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private StreamSocket? _socket;
    private DataWriter? _writer;
    private Stream? _readStream;
    private RfcommDeviceService? _service;
    private SppTransport _transport = SppTransport.Modern;

    public event Action<BatteryStatus>? BatteryUpdated;
    public event Action<ConnectionState, string?>? StateChanged;
    public event Action<NoiseControlMode>? NoiseControlChanged;

    /// <summary>Every decoded frame, including ones this app does not use (diagnostics).</summary>
    public event Action<SppFrame>? FrameReceived;

    public BatteryStatus? LastStatus { get; private set; }

    /// <summary>Last known noise-control mode, or null until the buds report one.</summary>
    public NoiseControlMode? NoiseMode { get; private set; }
    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// True while the SPP channel is held by another app (normally the official
    /// Samsung Galaxy Buds app). BudsTray then waits politely and retries often,
    /// so it takes over on its own once the other app lets go.
    /// </summary>
    public bool ChannelBusy { get; private set; }

    /// <summary>
    /// True while the user has the official Samsung Galaxy Buds app open.
    /// BudsTray then releases the channel instead of competing for it, and
    /// reconnects automatically when that window closes.
    /// </summary>
    public bool PausedForGalaxyBudsApp { get; private set; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public BudsClient(AppConfig config) => _config = config;

    public void Start()
    {
        lock (_gate)
        {
            if (PausedForGalaxyBudsApp) return;
            if (_loop != null) return;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => RunAsync(cts.Token));
        }
    }

    /// <summary>
    /// Releases the SPP channel for the official app and stops retrying. The app
    /// and BudsTray cannot hold the channel at the same time, so this is the
    /// cooperative half of the handoff: the user can switch to the Galaxy Buds
    /// window without either app being closed.
    /// </summary>
    public async Task PauseAsync()
    {
        PausedForGalaxyBudsApp = true;
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Resumes the connection loop after the official app window closes.</summary>
    public void Resume()
    {
        PausedForGalaxyBudsApp = false;
        Start();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        Start();
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        cts?.Cancel();
        CloseConnection();

        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); }
            catch { /* shutdown */ }
        }

        cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            var attemptStarted = DateTimeOffset.UtcNow;
            var freedChannel = false;
            try
            {
                SetState(ConnectionState.Connecting, null);
                await ConnectAndReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var message = Describe(ex);
                var busy = HResultOf(ex) == 0x80072740;
                LastError = message;

                // A channel held by the Samsung app is a normal coexistence
                // state, not a failure: log the transition once and stay quiet
                // while the retries continue.
                if (busy)
                {
                    if (!ChannelBusy)
                        Log.Info("The Galaxy Buds app is holding the Bluetooth channel.");

                    // QuickControls.exe owns the channel and restarts
                    // BudsManager.exe through the package's app service, so both
                    // have to go before the socket can be opened. Never do this
                    // while the user has the official window open: the tray
                    // watcher pauses us instead and gives that app the channel.
                    if (_config.TakeOverFromGalaxyBudsApp && !GalaxyBudsApp.IsWindowOpen())
                    {
                        var stopped = GalaxyBudsApp.Stop();
                        if (stopped.Count > 0)
                        {
                            freedChannel = true;
                            Log.Info($"Closed the official Galaxy Buds app ({string.Join(", ", stopped)}) to free the channel.");
                        }
                    }
                }
                else
                {
                    Log.Error("Connection error", ex);
                }

                ChannelBusy = busy;
                SetState(ConnectionState.Disconnected, message);
            }
            finally
            {
                CloseConnection();
            }

            if (ct.IsCancellationRequested) break;

            // While the Samsung app owns the channel, retry quickly so BudsTray
            // takes over the moment it is closed. Other failures keep the usual
            // 2–30 s backoff.
            if (ChannelBusy)
            {
                delay = freedChannel ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
            }
            else if (DateTimeOffset.UtcNow - attemptStarted > TimeSpan.FromSeconds(30))
            {
                delay = TimeSpan.FromSeconds(2);
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (!ChannelBusy)
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }

        SetState(ConnectionState.Disconnected, null);
    }

    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        var device = await FindDeviceAsync(ct).ConfigureAwait(false);
        DeviceName = device.Name;
        RememberDevice(device);
        Log.Info($"Connecting to {device.Name} ({device.BluetoothAddress:X12})");

        var match = await FindSppServiceAsync(device, BluetoothCacheMode.Uncached, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "No Galaxy Buds SPP service was found. Open the case, make sure the Buds are awake and paired, then click Reconnect.");

        _service = match.Service;
        _transport = match.Transport;
        Log.Info($"Using SPP service {match.Uuid} ({_transport} frame format)");

        _socket = new StreamSocket();
        await WithTimeout(
                _socket.ConnectAsync(_service.ConnectionHostName, _service.ConnectionServiceName).AsTask(),
                15, "RFCOMM connect", ct)
            .ConfigureAwait(false);

        _writer = new DataWriter(_socket.OutputStream);
        _readStream = _socket.InputStream.AsStreamForRead();

        LastError = null;
        ChannelBusy = false;
        SetState(ConnectionState.Connected, null);
        Log.Info("Connected");

        await SendManagerInfoAsync().ConfigureAwait(false);

        var parser = new SppProtocol.StreamParser(_transport);
        var buffer = new byte[4096];
        var managerSentOnStatus = false;

        while (!ct.IsCancellationRequested)
        {
            var read = await _readStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
                throw new IOException("The earbuds closed the connection.");

            foreach (var frame in parser.Feed(buffer, read))
            {
                FrameReceived?.Invoke(frame);

                // Mirror GalaxyBudsClient: reply with manager info once the buds
                // announce themselves with an extended status frame.
                if (!managerSentOnStatus && frame.Id == MsgIds.ExtendedStatusUpdated)
                {
                    managerSentOnStatus = true;
                    await SendManagerInfoAsync().ConfigureAwait(false);
                }

                var status = BatteryStatus.FromFrame(frame, LastStatus?.EarType)
                             ?? BatteryStatus.FromDebugData(frame, LastStatus);
                if (status != null)
                {
                    LastStatus = status;

                    // Persist the model so the sound-mode menu is correct on the
                    // next start even before the first status frame arrives.
                    if (status.EarType is { } earType && _config.LastEarType != earType)
                    {
                        _config.LastEarType = earType;
                        _config.Save();
                    }

                    Log.Info(status.ToTooltip(DeviceName ?? "Buds"));
                    BatteryUpdated?.Invoke(status);
                }

                // The extended status frame also carries the current sound mode;
                // mode changes arrive later as id 119/129/155 updates.
                var noiseMode = NoiseControl.FromStatusFrame(
                                     frame, status?.EarType ?? LastStatus?.EarType)
                                 ?? NoiseControl.FromUpdateFrame(frame);
                if (noiseMode is { } mode) SetNoiseMode(mode);
            }
        }
    }

    private async Task<BluetoothDevice> FindDeviceAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.DeviceAddress) &&
            ulong.TryParse(_config.DeviceAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) &&
            address != 0)
        {
            try
            {
                var remembered = await WithTimeout(
                    BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(), 5, "Remembered device lookup", ct)
                    .ConfigureAwait(false);
                if (remembered != null)
                {
                    Log.Info($"Using remembered device {remembered.Name} ({remembered.BluetoothAddress:X12})");
                    return remembered;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Remembered device lookup failed", ex);
            }
        }

        var devices = await WithTimeout(
            DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)).AsTask(),
            10, "Paired device enumeration", ct).ConfigureAwait(false);

        var candidates = new List<BluetoothDevice>();
        foreach (var info in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var device = await WithTimeout(
                    BluetoothDevice.FromIdAsync(info.Id).AsTask(), 5, "Device lookup", ct).ConfigureAwait(false);
                if (device?.Name is { Length: > 0 }) candidates.Add(device);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not open device info {info.Id}", ex);
            }
        }

        Log.Info("Paired devices: " + string.Join(", ", candidates.Select(d => $"{d.Name} ({d.ConnectionStatus})")));

        // The earbuds may have been renamed (e.g. "TOYOTA Corolla"), so the name is
        // not reliable. A Galaxy Buds device is identified by Samsung's SPP service.
        // Connected devices are checked first; that is the normal case.
        foreach (var device in candidates.OrderByDescending(d => d.ConnectionStatus == BluetoothConnectionStatus.Connected))
        {
            ct.ThrowIfCancellationRequested();

            var match = await FindSppServiceAsync(device, BluetoothCacheMode.Cached, ct).ConfigureAwait(false);
            match ??= await FindSppServiceAsync(device, BluetoothCacheMode.Uncached, ct).ConfigureAwait(false);

            Log.Info($"SPP check on {device.Name}: {(match is null ? "none" : match.Value.Uuid.ToString())}");

            if (match is { } found)
            {
                // This lookup only decides whether the device is a pair of Buds;
                // ConnectAndReadAsync opens its own service for the real connection.
                found.Service.Dispose();
                Log.Info($"Selected {device.Name} ({device.BluetoothAddress:X12})");
                return device;
            }
        }

        // Last resort: name-based match, in case the SPP service is not advertised
        // until the earbuds are awake.
        return candidates.FirstOrDefault(d => d.Name.Contains("Buds", StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   "No paired Galaxy Buds found. Pair them in Windows Settings > Bluetooth, then click Reconnect.");
    }

    /// <summary>
    /// Looks for any known Galaxy Buds SPP service on the device. All generations
    /// expose one of these UUIDs, and the matched UUID also selects the frame
    /// layout used by the original Buds.
    ///
    /// Every service that is not handed back to the caller is disposed here:
    /// Bluetooth discovery runs again on every reconnect attempt, and leaving the
    /// WinRT wrappers to the finalizer would keep native handles around while the
    /// buds are asleep.
    /// </summary>
    private static async Task<(RfcommDeviceService Service, Guid Uuid, SppTransport Transport)?> FindSppServiceAsync(
        BluetoothDevice device,
        BluetoothCacheMode cacheMode,
        CancellationToken ct)
    {
        var query = device.GetRfcommServicesAsync(cacheMode).AsTask();
        RfcommDeviceServicesResult result;
        try
        {
            result = await WithTimeout(query, 5, $"SPP service query ({cacheMode})", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DisposeWhenFinished(query);
            throw;
        }
        catch
        {
            // The device may be asleep; the caller retries or falls back to the name.
            // A query that already started can still complete, so release its result.
            DisposeWhenFinished(query);
            return null;
        }

        (RfcommDeviceService Service, Guid Uuid, SppTransport Transport)? match = null;
        foreach (var service in result.Services)
        {
            if (match is null)
            {
                try
                {
                    var uuid = service.ServiceId.Uuid;
                    var candidate = ServiceCandidates.FirstOrDefault(c => c.Uuid == uuid);
                    if (candidate != null)
                    {
                        match = (service, candidate.Uuid, candidate.Transport);
                        continue;
                    }
                }
                catch
                {
                    // An unreadable service id is not a Buds service.
                }
            }

            try { service.Dispose(); } catch { }
        }

        return match;
    }

    /// <summary>Releases the services of a discovery query that finishes after it was abandoned.</summary>
    private static void DisposeWhenFinished(Task<RfcommDeviceServicesResult> query)
    {
        _ = query.ContinueWith(
            finished =>
            {
                try
                {
                    if (finished.Status != TaskStatus.RanToCompletion) return;

                    foreach (var service in finished.Result.Services)
                    {
                        try { service.Dispose(); } catch { }
                    }
                }
                catch
                {
                    // Nothing left to do with a result nobody is waiting for.
                }
                finally
                {
                    _ = finished.Exception; // observe; the caller already handled the failure
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds, string operation, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{operation} timed out after {seconds}s.");
        }
    }

    private static async Task WithTimeout(Task task, int seconds, string operation, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{operation} timed out after {seconds}s.");
        }
    }

    private void RememberDevice(BluetoothDevice device)
    {
        var address = device.BluetoothAddress.ToString("X12");
        if (_config.DeviceAddress == address && _config.DeviceName == device.Name) return;

        _config.DeviceAddress = address;
        _config.DeviceName = device.Name;
        _config.Save();
    }

    private async Task SendManagerInfoAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SendManagerInfoCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Asks the earbuds for fresh battery data. Buds2 and newer answer
    /// <c>DEBUG_GET_ALL_DATA</c> with a reply that contains the live earbud SOC
    /// values; the original Buds only understands the manager handshake, which
    /// makes them push a status frame. Polling every 15 s keeps the tray current
    /// without touching the connection. Pushed status frames still arrive
    /// immediately.
    /// </summary>
    public async Task RequestStatusAsync()
    {
        // The 15 s poll keeps ticking while the app is connecting, disconnected
        // or paused for the official app; there is nothing to ask then.
        if (State != ConnectionState.Connected) return;

        try
        {
            if (_transport == SppTransport.Legacy)
                await SendManagerInfoAsync().ConfigureAwait(false);
            else
                await SendMessageAsync(MsgIds.DebugGetAllData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The connection can drop between the check and the write.
            Log.Error("Could not poll the battery", ex);
        }
    }

    /// <summary>
    /// Switches the buds between ANC, Ambient Sound, Adaptive and Off. Models
    /// with the modern NOISE_CONTROLS message take the mode byte directly; the
    /// original Buds/Buds+ only toggle Ambient Sound, and Buds Live only toggles
    /// ANC.
    /// </summary>
    public async Task<bool> SetNoiseControlAsync(NoiseControlMode mode)
    {
        if (State != ConnectionState.Connected) return false;

        var command = NoiseControl.CommandFor(
            mode, LastStatus?.EarType ?? _config.LastEarType, DeviceName);
        if (command is not { } message) return false;

        var sent = await SendMessageAsync(message.Id, message.Payload).ConfigureAwait(false);

        // The buds usually confirm asynchronously, but updating immediately keeps
        // the menu responsive when the acknowledgement is delayed.
        if (sent) SetNoiseMode(mode);
        return sent;
    }

    /// <summary>Sends one protocol message, serialized against the connect handshake.</summary>
    public async Task<bool> SendMessageAsync(byte id, params byte[] payload)
    {
        if (State != ConnectionState.Connected || _writer == null) return false;

        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var writer = _writer ?? throw new InvalidOperationException("Not connected.");
                writer.WriteBytes(SppProtocol.Encode(id, payload, transport: _transport));
                await writer.StoreAsync().AsTask().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Sending message {id} failed", ex);
            return false;
        }
    }

    private async Task SendManagerInfoCoreAsync()
    {
        // Same handshake as GalaxyBudsClient: version, client type (Samsung), Android SDK.
        var frame = SppProtocol.Encode(
            MsgIds.ManagerInfo, new byte[] { 1, 1, 34 }, transport: _transport);
        var writer = _writer ?? throw new InvalidOperationException("Not connected.");
        writer.WriteBytes(frame);
        await writer.StoreAsync().AsTask().ConfigureAwait(false);
    }

    private void SetState(ConnectionState state, string? error)
    {
        State = state;

        // Do not carry a mode over to the next pair if the connection drops.
        if (state != ConnectionState.Connected) NoiseMode = null;

        StateChanged?.Invoke(state, error);
    }

    private void SetNoiseMode(NoiseControlMode mode)
    {
        if (NoiseMode == mode) return;

        NoiseMode = mode;
        Log.Info($"Noise control: {mode}");
        NoiseControlChanged?.Invoke(mode);
    }

    private void CloseConnection()
    {
        try { _readStream?.Dispose(); } catch { }
        try { _writer?.DetachStream(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _socket?.Dispose(); } catch { }
        try { _service?.Dispose(); } catch { }

        _readStream = null;
        _writer = null;
        _socket = null;
        _service = null;
    }

    private static string Describe(Exception ex)
    {
        var hr = HResultOf(ex);

        if (ex is UnauthorizedAccessException)
            return "Bluetooth access denied. Check Settings > Privacy > Other devices.";

        return hr switch
        {
            0x80072740 => "The Galaxy Buds app is using the connection. BudsTray will reconnect when the channel is free.",
            0x80070490 => "The Buds' SPP server is unavailable. Put both buds in the case, close it, then reopen and reconnect.",
            _ => ex.Message
        };
    }

    private static uint HResultOf(Exception ex)
    {
        var current = ex;
        while (current.InnerException != null) current = current.InnerException;
        return (uint)current.HResult;
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
        }

        cts?.Cancel();
        CloseConnection();
        cts?.Dispose();
        _writeGate.Dispose();
    }
}
