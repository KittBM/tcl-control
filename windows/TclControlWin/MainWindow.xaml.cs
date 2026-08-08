using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TclControlWin;

public partial class MainWindow : Window
{
    private readonly TclSoundbarBle _ble = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LinkedList<string> _logLines = new();
    private const int MaxLogLines = 200;

    private int _currentVolume = 20;
    private bool _suppressSliderEvents;
    private bool _suppressToggleEvents;
    private ulong? _lastAttemptedAddress;
    private DeviceListWindow? _scanDialog;
    private CancellationTokenSource? _autoConnectCts;

    public MainWindow()
    {
        InitializeComponent();

        _ble.ConnectionStateChanged += OnConnectionStateChanged;
        _ble.StatusReceived += OnStatusReceived;
        _ble.Log += AppendLog;
        _ble.DeviceDiscovered += OnDeviceDiscovered;

        TxtAddress.Text = _settings.Address;
        BtnReconnect.Content = $"Reconnect: {_settings.Name}";
        InitializeControlsFromSettings();

        Closing += (_, _) =>
        {
            _autoConnectCts?.Cancel();
            _ble.Dispose();
        };

        StartAutoConnect();
    }

    /// <summary>Pre-fills volume/toggles/source/mode/bass/treble from the last confirmed device state
    /// so the UI doesn't reset to hardcoded defaults every launch. Purely local/cosmetic until the
    /// soundbar itself confirms these values again via a status echo.</summary>
    private void InitializeControlsFromSettings()
    {
        _currentVolume = _settings.Volume;
        TxtVolume.Text = _settings.Volume.ToString();

        _suppressToggleEvents = true;
        ChkMute.IsChecked = _settings.Mute;
        ChkAtmos.IsChecked = _settings.Atmos;
        _suppressToggleEvents = false;

        if (_settings.Source is >= 1 and <= 3) CheckToggleGroup(_settings.Source, BtnSource1, BtnSource2, BtnSource3);
        if (_settings.SoundMode is >= 1 and <= 3) CheckToggleGroup(_settings.SoundMode, BtnMode1, BtnMode2, BtnMode3);

        _suppressSliderEvents = true;
        SliderBass.Value = Math.Clamp(_settings.Bass, -6, 6);
        TxtBassValue.Text = _settings.Bass.ToString();
        SliderTreble.Value = Math.Clamp(_settings.Treble, -6, 6);
        TxtTrebleValue.Text = _settings.Treble.ToString();
        _suppressSliderEvents = false;
    }

    /// <summary>
    /// Background auto-connect: repeatedly BLE-scans for an advertisement named <see cref="AppSettings.Name"/>
    /// and connects as soon as one is seen. Scans by name rather than the saved address because the soundbar's
    /// address is a rotating random address that Windows can't resolve without OS-level pairing (see PROGRESS.md) -
    /// scanning by name sidesteps that entirely. Stops once connected; restarts on disconnect.
    /// </summary>
    private void StartAutoConnect()
    {
        if (_autoConnectCts != null) return;
        _autoConnectCts = new CancellationTokenSource();
        _ = AutoConnectLoopAsync(_autoConnectCts.Token);
    }

    private void StopAutoConnect()
    {
        _autoConnectCts?.Cancel();
        _autoConnectCts = null;
    }

    private async Task AutoConnectLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                AppendLog($"auto-connect: scanning for \"{_settings.Name}\"...");
                var address = await _ble.ScanForDeviceNameAsync(_settings.Name, TimeSpan.FromSeconds(15), token);
                if (address == null) continue; // timed out, scan again

                await ConnectTo(address.Value, _settings.Name);
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped intentionally (connected, or a manual action took over)
        }
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        StopAutoConnect();
        if (!TclSoundbarBle.TryParseAddress(TxtAddress.Text.Trim(), out var address))
        {
            AppendLog($"invalid address: {TxtAddress.Text}");
            return;
        }
        await ConnectTo(address, TxtAddress.Text.Trim());
    }

    private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
    {
        StopAutoConnect();
        if (!TclSoundbarBle.TryParseAddress(_settings.Address, out var address))
        {
            AppendLog($"invalid saved address: {_settings.Address}");
            return;
        }
        await ConnectTo(address, _settings.Name);
    }

    private async Task ConnectTo(ulong address, string label)
    {
        _lastAttemptedAddress = address;
        AppendLog($"connecting to {label} ({TclSoundbarBle.FormatAddress(address)})");
        await _ble.ConnectAsync(address);
    }

    private void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        StopAutoConnect();
        _scanDialog = new DeviceListWindow { Owner = this };
        _ble.StartScan(TimeSpan.FromSeconds(8));
        var result = _scanDialog.ShowDialog();
        _ble.StopScan();
        if (result == true && _scanDialog.Selected is { } entry)
        {
            TxtAddress.Text = TclSoundbarBle.FormatAddress(entry.Address);
            _ = ConnectTo(entry.Address, entry.Name);
        }
        _scanDialog = null;
    }

    private void OnDeviceDiscovered(string name, ulong address, short rssi)
    {
        Dispatcher.Invoke(() => _scanDialog?.AddOrUpdate(name, address, rssi));
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => _ble.RefreshStatus();

    private void BtnVolUp_Click(object sender, RoutedEventArgs e) => _ble.SendCommand(TclProtocol.Attr.Volume, _currentVolume + 1);

    private void BtnVolDown_Click(object sender, RoutedEventArgs e) => _ble.SendCommand(TclProtocol.Attr.Volume, _currentVolume - 1);

    private void ChkMute_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents) return;
        _ble.SendCommand(TclProtocol.Attr.Mute, ChkMute.IsChecked == true ? 1 : 0);
    }

    private void ChkAtmos_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents) return;
        _ble.SendCommand(TclProtocol.Attr.AtmosDts, ChkAtmos.IsChecked == true ? 1 : 0);
    }

    private void BtnSource1_Click(object sender, RoutedEventArgs e) => SelectSource(1);
    private void BtnSource2_Click(object sender, RoutedEventArgs e) => SelectSource(2);
    private void BtnSource3_Click(object sender, RoutedEventArgs e) => SelectSource(3);

    private void SelectSource(int value)
    {
        _ble.SendCommand(TclProtocol.Attr.Source, value);
        CheckToggleGroup(value, BtnSource1, BtnSource2, BtnSource3);
    }

    private void BtnMode1_Click(object sender, RoutedEventArgs e) => SelectMode(1);
    private void BtnMode2_Click(object sender, RoutedEventArgs e) => SelectMode(2);
    private void BtnMode3_Click(object sender, RoutedEventArgs e) => SelectMode(3);

    private void SelectMode(int value)
    {
        _ble.SendCommand(TclProtocol.Attr.SoundMode, value);
        CheckToggleGroup(value, BtnMode1, BtnMode2, BtnMode3);
    }

    private static void CheckToggleGroup(int value, ToggleButton b1, ToggleButton b2, ToggleButton b3)
    {
        b1.IsChecked = value == 1;
        b2.IsChecked = value == 2;
        b3.IsChecked = value == 3;
    }

    private void SliderBass_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)e.NewValue;
        TxtBassValue.Text = value.ToString();
        if (!_suppressSliderEvents) _ble.SendCommand(TclProtocol.Attr.Bass, value);
    }

    private void SliderTreble_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)e.NewValue;
        TxtTrebleValue.Text = value.ToString();
        if (!_suppressSliderEvents) _ble.SendCommand(TclProtocol.Attr.Treble, value);
    }

    private void OnConnectionStateChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = connected ? "Connected" : "Not connected";
            StatusDot.Fill = new SolidColorBrush(connected ? Colors.LimeGreen : Colors.Red);

            if (connected && _lastAttemptedAddress is { } address)
            {
                _settings.Address = TclSoundbarBle.FormatAddress(address);
                _settings.Save();
                BtnReconnect.Content = $"Reconnect: {_settings.Name}";
            }

            if (connected)
            {
                StopAutoConnect();
            }
            else
            {
                StartAutoConnect();
            }
        });
    }

    private void OnStatusReceived(TclProtocol.Incoming status)
    {
        Dispatcher.Invoke(() =>
        {
            AppendLog($"STATUS {TclProtocol.AttrName(status.SetAttrId)} = {status.Value}");
            switch (status.SetAttrId)
            {
                case TclProtocol.Attr.Volume:
                    _currentVolume = status.Value;
                    TxtVolume.Text = status.Value.ToString();
                    _settings.Volume = status.Value;
                    break;
                case TclProtocol.Attr.Mute:
                    _suppressToggleEvents = true;
                    ChkMute.IsChecked = status.Value == 1;
                    _suppressToggleEvents = false;
                    _settings.Mute = status.Value == 1;
                    break;
                case TclProtocol.Attr.AtmosDts:
                    _suppressToggleEvents = true;
                    ChkAtmos.IsChecked = status.Value == 1;
                    _suppressToggleEvents = false;
                    _settings.Atmos = status.Value == 1;
                    break;
                case TclProtocol.Attr.Bass:
                    _suppressSliderEvents = true;
                    SliderBass.Value = Math.Clamp(status.Value, -6, 6);
                    TxtBassValue.Text = status.Value.ToString();
                    _suppressSliderEvents = false;
                    _settings.Bass = status.Value;
                    break;
                case TclProtocol.Attr.Treble:
                    _suppressSliderEvents = true;
                    SliderTreble.Value = Math.Clamp(status.Value, -6, 6);
                    TxtTrebleValue.Text = status.Value.ToString();
                    _suppressSliderEvents = false;
                    _settings.Treble = status.Value;
                    break;
                case TclProtocol.Attr.Source:
                    CheckToggleGroup(status.Value, BtnSource1, BtnSource2, BtnSource3);
                    _settings.Source = status.Value;
                    break;
                case TclProtocol.Attr.SoundMode:
                    CheckToggleGroup(status.Value, BtnMode1, BtnMode2, BtnMode3);
                    _settings.SoundMode = status.Value;
                    break;
                default:
                    return;
            }
            _settings.Save();
        });
    }

    private void AppendLog(string line)
    {
        void Do()
        {
            _logLines.AddFirst(line);
            while (_logLines.Count > MaxLogLines) _logLines.RemoveLast();
            ListLog.ItemsSource = null;
            ListLog.ItemsSource = _logLines;
        }

        if (Dispatcher.CheckAccess()) Do();
        else Dispatcher.Invoke(Do);
    }
}
