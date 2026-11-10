using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using JSONConverterPro.Core.Helpers;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Models;

/// <summary>One row of the Columns view: what the column looks like in the data, plus the user's include / rename / mask choices.</summary>
public partial class ColumnRuleVm : ObservableObject
{
    private readonly Action? _changed;

    public ColumnRuleVm(ColumnInfo info, ColumnRule? rule, Action changed)
    {
        Name = info.Name;
        Type = info.Type;
        Depth = info.Depth;
        FilledText = $"{info.FilledPercent:0.#}%";
        Sample = info.Sample;
        _include = rule?.Include ?? true;
        _renameTo = rule?.RenameTo ?? "";
        _maskIndex = (int)(rule?.Mask ?? MaskMode.None);
        _changed = changed;   // assigned last so initial values don't raise change notifications
    }

    public string Name { get; }
    public string Type { get; }
    public int Depth { get; }
    public string FilledText { get; }
    public string Sample { get; }
    public Thickness Indent => new(Math.Max(0, Depth - 1) * 12, 0, 0, 0);

    [ObservableProperty] private bool _include;
    [ObservableProperty] private string _renameTo;
    [ObservableProperty] private int _maskIndex;

    partial void OnIncludeChanged(bool value) => _changed?.Invoke();
    partial void OnRenameToChanged(string value) => _changed?.Invoke();
    partial void OnMaskIndexChanged(int value) => _changed?.Invoke();

    public ColumnRule ToRule() => new()
    {
        Name = Name, Include = Include, RenameTo = string.IsNullOrWhiteSpace(RenameTo) ? null : RenameTo.Trim(), Mask = (MaskMode)Math.Clamp(MaskIndex, 0, 2)
    };
}
