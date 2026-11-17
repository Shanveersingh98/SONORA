namespace AudioEnhancer.UI;

/// <summary>One slider on the screen: a label plus a 0-100 whole-number value.</summary>
public sealed class SliderControl : ViewModelBase
{
    private double _value;

    public SliderControl(string label, double value)
    {
        Label = label;
        _value = value;
    }

    public string Label { get; }

    public double Value
    {
        get => _value;
        set => SetProperty(ref _value, Math.Clamp(Math.Round(value), 0, 100));
    }
}
