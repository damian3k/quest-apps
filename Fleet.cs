using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace QuestApps;

internal static class FleetBrushes
{
    public static readonly IBrush Info = new ImmutableSolidColorBrush(Color.Parse("#FF8B8CA0"));
    public static readonly IBrush Ok = new ImmutableSolidColorBrush(Color.Parse("#FF57D98A"));
    public static readonly IBrush Warn = new ImmutableSolidColorBrush(Color.Parse("#FFE8B44C"));
    public static readonly IBrush Err = new ImmutableSolidColorBrush(Color.Parse("#FFFF6B6B"));
}

// One headset row in the multi-headset window.
public sealed class FleetHeadset : INotifyPropertyChanged
{
    public string Id { get; }
    public string Model { get; set; }
    public string? Ip { get; set; }

    public FleetHeadset(string id, string model)
    {
        Id = id;
        Model = model;
    }

    // installed user apps (package -> APK path); empty while offline
    public Dictionary<string, string> Packages { get; set; } = new(StringComparer.Ordinal);

    private string? _name;
    public string? Name
    {
        get => _name;
        set { _name = value; Raise(nameof(Name)); Raise(nameof(Label)); }
    }

    public string Label => string.IsNullOrWhiteSpace(Name)
        ? $"{Model.Replace('_', ' ')} •{(Id.Length > 4 ? Id[^4..] : Id)}"
        : Name!;

    private string? _serial;
    public string? Serial
    {
        get => _serial;
        set
        {
            if (_serial == value) return;
            _serial = value;
            Raise(nameof(Serial));
            Raise(nameof(IsOnline));
            Raise(nameof(ConnectionText));
        }
    }

    public bool IsOnline => Serial != null;

    private string _offlineText = "offline";
    public string OfflineText
    {
        get => _offlineText;
        set
        {
            if (_offlineText == value) return;
            _offlineText = value;
            Raise(nameof(ConnectionText));
        }
    }

    public string ConnectionText => Serial == null
        ? OfflineText
        : AdbService.IsWifiSerial(Serial) ? $"Wi-Fi {Ip}" : "USB";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    private string _batteryText = "";
    public string BatteryText
    {
        get => _batteryText;
        set { _batteryText = value; Raise(nameof(BatteryText)); }
    }

    private string _nowText = "";
    public string NowText
    {
        get => _nowText;
        set { _nowText = value; Raise(nameof(NowText)); }
    }

    private string _kioskText = "";
    public string KioskText
    {
        get => _kioskText;
        set { _kioskText = value; Raise(nameof(KioskText)); }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; Raise(nameof(StatusText)); }
    }

    private IBrush _statusBrush = FleetBrushes.Info;
    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; Raise(nameof(StatusBrush)); }
    }

    public void SetStatus(string text, IBrush brush)
    {
        StatusText = $"{DateTime.Now:HH:mm:ss}  {text}";
        StatusBrush = brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// One app in the shared app list: how many of the selected headsets have it.
public sealed class FleetApp : INotifyPropertyChanged
{
    public string Package { get; }
    public FleetApp(string package) => Package = package;

    private string? _title;
    public string? Title
    {
        get => _title;
        set { _title = value; Raise(nameof(Title)); Raise(nameof(DisplayName)); }
    }

    public string DisplayName => Title ?? Package;

    private IImage? _icon;
    public IImage? Icon
    {
        get => _icon;
        set { _icon = value; Raise(nameof(Icon)); }
    }

    private int _count;
    private int _total;
    public void SetCount(int count, int total)
    {
        _count = count;
        _total = total;
        Raise(nameof(CountText));
        Raise(nameof(CountBrush));
    }

    public int Count => _count;
    public string CountText => _total == 0 ? "" : _count == _total ? $"on all {_total}" : $"on {_count} of {_total}";
    public IBrush CountBrush => _count == _total && _total > 0 ? FleetBrushes.Ok : FleetBrushes.Warn;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
