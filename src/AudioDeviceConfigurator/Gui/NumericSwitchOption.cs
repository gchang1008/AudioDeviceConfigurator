using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioDeviceConfigurator.Gui;

public sealed class NumericSwitchOption : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _isSelected;

    public NumericSwitchOption(int value, string displayText, string automationId)
    {
        Value = value;
        DisplayText = displayText;
        AutomationId = automationId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Value { get; }

    public string DisplayText { get; }

    public string AutomationId { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => Set(ref _isEnabled, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
