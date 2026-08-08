using System.Collections.Generic;
using System.Windows;

namespace TclControlWin;

public partial class DeviceListWindow : Window
{
    public sealed record Entry(string Name, ulong Address, short Rssi)
    {
        public override string ToString() => $"{Name} ({TclSoundbarBle.FormatAddress(Address)}) rssi={Rssi}";
    }

    public Entry? Selected { get; private set; }

    private readonly List<Entry> _entries = new();

    public DeviceListWindow()
    {
        InitializeComponent();
    }

    public void AddOrUpdate(string name, ulong address, short rssi)
    {
        var existingIndex = _entries.FindIndex(e => e.Address == address);
        var entry = new Entry(name, address, rssi);
        if (existingIndex >= 0)
        {
            _entries[existingIndex] = entry;
            ListDevices.Items[existingIndex] = entry;
        }
        else
        {
            _entries.Add(entry);
            ListDevices.Items.Add(entry);
        }
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e) => Accept();

    private void ListDevices_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (ListDevices.SelectedItem is Entry entry)
        {
            Selected = entry;
            DialogResult = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
