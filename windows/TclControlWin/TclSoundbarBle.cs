using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace TclControlWin;

/// <summary>
/// GATT connection manager for the TCL S45H soundbar, using the WinRT Bluetooth LE APIs.
/// Mirrors the connect/send/notify flow of the Android TclSoundbarBle.kt.
/// </summary>
public sealed class TclSoundbarBle : IDisposable
{
    public event Action<bool>? ConnectionStateChanged;
    public event Action<TclProtocol.Incoming>? StatusReceived;
    public event Action<string>? Log;
    public event Action<string, ulong, short>? DeviceDiscovered; // name, address, rssi

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private BluetoothLEAdvertisementWatcher? _watcher;

    public async Task ConnectAsync(ulong bluetoothAddress)
    {
        Disconnect();

        Log?.Invoke($"connecting to {FormatAddress(bluetoothAddress)}...");
        BluetoothLEDevice? device;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"connect failed: {ex.Message}");
            ConnectionStateChanged?.Invoke(false);
            return;
        }

        if (device == null)
        {
            Log?.Invoke("could not open device (not paired / out of range?)");
            ConnectionStateChanged?.Invoke(false);
            return;
        }
        _device = device;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        var pairing = device.DeviceInformation.Pairing;
        Log?.Invoke($"LE pairing: isPaired={pairing.IsPaired} canPair={pairing.CanPair} protectionLevel={pairing.ProtectionLevel}");
        if (!pairing.IsPaired && pairing.CanPair)
        {
            Log?.Invoke("not bonded at the LE level yet, requesting pairing...");
            var pairResult = await pairing.PairAsync();
            Log?.Invoke($"pair result: {pairResult.Status}");
        }

        // GetGattServicesForUuidAsync/GetCharacteristicsForUuidAsync (the UUID-filtered overloads) have been
        // observed to spuriously miss services/characteristics that a plain, unfiltered enumeration finds
        // moments later - a known flaky spot in the WinRT BLE stack right after connecting. Always fetch
        // everything and filter client-side instead of relying on the filtered overloads.
        var allServices = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (allServices.Status != GattCommunicationStatus.Success)
        {
            Log?.Invoke($"service discovery failed (status={allServices.Status})");
            ConnectionStateChanged?.Invoke(false);
            return;
        }
        var service = allServices.Services.FirstOrDefault(s => s.Uuid == TclProtocol.ServiceUuid);
        if (service == null)
        {
            var uuids = allServices.Services.Select(s => s.Uuid.ToString());
            Log?.Invoke($"service {TclProtocol.ServiceUuid} not found among {allServices.Services.Count} services: {string.Join(", ", uuids)}");
            ConnectionStateChanged?.Invoke(false);
            return;
        }

        var allChars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        _writeChar = allChars.Characteristics.FirstOrDefault(c => c.Uuid == TclProtocol.WriteCharUuid);
        _notifyChar = allChars.Characteristics.FirstOrDefault(c => c.Uuid == TclProtocol.NotifyCharUuid);

        if (_notifyChar != null)
        {
            _notifyChar.ValueChanged += OnNotifyValueChanged;
            var cccdStatus = await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            Log?.Invoke($"notify subscribe status={cccdStatus}");
        }

        Log?.Invoke($"ready (write={_writeChar != null} notify={_notifyChar != null})");
        ConnectionStateChanged?.Invoke(true);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            Log?.Invoke("disconnected");
            ConnectionStateChanged?.Invoke(false);
        }
    }

    public void Disconnect()
    {
        if (_notifyChar != null)
        {
            _notifyChar.ValueChanged -= OnNotifyValueChanged;
            _notifyChar = null;
        }
        _writeChar = null;
        if (_device != null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
    }

    public async void SendCommand(int attrId, int value)
    {
        var writeChar = _writeChar;
        if (writeChar == null)
        {
            Log?.Invoke($"not connected, cannot send attr={attrId}");
            return;
        }
        var frame = TclProtocol.BuildSetCommand(attrId, value);
        var status = await writeChar.WriteValueAsync(frame.AsBuffer(), GattWriteOption.WriteWithoutResponse);
        Log?.Invoke($"TX {TclProtocol.AttrName(attrId)}={value}  {ToHex(frame)} ({status})");
    }

    public void RefreshStatus() => SendCommand(TclProtocol.Attr.GetStatus, 0);

    private void OnNotifyValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToArray();
        var parsed = TclProtocol.ParseIncoming(bytes);
        switch (parsed.Kind)
        {
            case TclProtocol.IncomingKind.Status:
                StatusReceived?.Invoke(parsed);
                break;
            case TclProtocol.IncomingKind.Ack:
                break;
            default:
                Log?.Invoke($"RX {ToHex(bytes)}");
                break;
        }
    }

    public void StartScan(TimeSpan duration)
    {
        StopScan();
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnAdvertisementReceived;
        Log?.Invoke($"scanning for {duration.TotalSeconds:0}s...");
        _watcher.Start();
        _ = Task.Delay(duration).ContinueWith(_ => StopScan());
    }

    public void StopScan()
    {
        if (_watcher != null)
        {
            _watcher.Received -= OnAdvertisementReceived;
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
            _watcher = null;
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName;
        if (string.IsNullOrEmpty(name)) return;
        DeviceDiscovered?.Invoke(name, args.BluetoothAddress, args.RawSignalStrengthInDBm);
    }

    /// <summary>
    /// Scans until an advertisement whose local name contains <paramref name="targetName"/> is seen, or
    /// <paramref name="timeout"/> elapses. Substring match (not exact) since the advertised name has been
    /// observed to vary slightly between sessions/firmware. Returns null on a plain timeout (caller should
    /// retry); throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is the
    /// one that fired (caller should stop retrying). Used for background auto-connect-by-name, since the
    /// soundbar's Bluetooth address isn't stable/resolvable without OS-level pairing - see PROGRESS.md.
    /// </summary>
    public async Task<ulong?> ScanForDeviceNameAsync(string targetName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        StopScan();
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName;
            if (!string.IsNullOrEmpty(name) && name.Contains(targetName, StringComparison.Ordinal))
            {
                tcs.TrySetResult(args.BluetoothAddress);
            }
        }

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher = watcher;
        watcher.Received += OnReceived;
        watcher.Start();

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            using var reg = linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token));

            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null; // plain timeout, not caller-requested cancellation
            }
        }
        finally
        {
            watcher.Received -= OnReceived;
            StopScan();
        }
    }

    public static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address).Take(6).Reverse();
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    public static bool TryParseAddress(string mac, out ulong address)
    {
        address = 0;
        var parts = mac.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return false;
        ulong result = 0;
        foreach (var part in parts)
        {
            if (!byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var b)) return false;
            result = (result << 8) | b;
        }
        address = result;
        return true;
    }

    private static string ToHex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("x2")));

    public void Dispose()
    {
        StopScan();
        Disconnect();
    }
}
