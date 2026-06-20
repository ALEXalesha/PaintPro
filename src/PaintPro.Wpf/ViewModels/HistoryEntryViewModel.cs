using CommunityToolkit.Mvvm.ComponentModel;

namespace PaintPro.ViewModels;

/// <summary>
/// One row in the History panel. <see cref="Target"/> is the history cursor position this
/// row represents (number of commands applied) — clicking it asks the HistoryManager to
/// jump there. <see cref="IsCurrent"/> marks the active position; <see cref="IsFuture"/>
/// marks redoable rows so the UI can dim them.
/// </summary>
public partial class HistoryEntryViewModel : ObservableObject
{
    public int Target { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isFuture;

    public HistoryEntryViewModel(int target, string label)
    {
        Target = target;
        Label = label;
    }
}
