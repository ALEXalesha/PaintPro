using CommunityToolkit.Mvvm.ComponentModel;
using PaintPro.Commands;

namespace PaintPro.ViewModels;

/// <summary>
/// One row in the History panel. <see cref="Target"/> is the history cursor position this
/// row represents (number of commands applied) — clicking it asks the HistoryManager to
/// jump there. <see cref="IsCurrent"/> marks the active position; <see cref="IsFuture"/>
/// marks redoable rows so the UI can dim them.
///
/// Кроме прыжка по ленте у строки есть выключатель: снятая галочка означает, что правка
/// не применяется, а идущие после неё - применяются по-прежнему. Есть он не у всех - см.
/// <see cref="CanToggle"/> и <see cref="Services.HistoryManager.CanToggle"/>.
/// </summary>
public partial class HistoryEntryViewModel : ObservableObject
{
    public int Target { get; }
    public string Label { get; }

    /// <summary>Запись, которой соответствует строка. У строки «Исходное состояние» её нет.</summary>
    public IDocumentCommand? Command { get; }

    /// <summary>Есть ли у строки выключатель.</summary>
    public bool CanToggle { get; }

    /// <summary>Что сказать при наведении: как пользоваться выключателем или почему его нет.</summary>
    public string ToggleHint { get; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isFuture;

    /// <summary>Применяется ли эта правка. Привязка односторонняя: переключает команда.</summary>
    [ObservableProperty] private bool _enabled = true;

    public HistoryEntryViewModel(int target, string label)
    {
        Target = target;
        Label = label;
        ToggleHint = "";
    }

    public HistoryEntryViewModel(int target, string label, IDocumentCommand command,
                                 bool enabled, bool canToggle, string toggleHint)
    {
        Target = target;
        Label = label;
        Command = command;
        Enabled = enabled;
        CanToggle = canToggle;
        ToggleHint = toggleHint;
    }
}
