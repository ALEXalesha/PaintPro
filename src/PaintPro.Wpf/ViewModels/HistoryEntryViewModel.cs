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
///
/// Строка живёт, пока жива её запись: лента не пересоздаёт строки на каждую правку, а
/// правит их на месте (MainViewModel.SyncHistoryItems, 1.34.0). Поэтому всё, что может
/// поменяться без смены записи, - наблюдаемое: номер позиции сдвигается, когда лента
/// выбрасывает самые старые записи, а выключатель пропадает, когда поверх легла правка,
/// пишущая картинку целиком.
/// </summary>
public partial class HistoryEntryViewModel : ObservableObject
{
    /// <summary>Запись, которой соответствует строка. У строки «Исходное состояние» её нет.</summary>
    public IDocumentCommand? Command { get; }

    [ObservableProperty] private int _target;
    [ObservableProperty] private string _label;

    /// <summary>Есть ли у строки выключатель.</summary>
    [ObservableProperty] private bool _canToggle;

    /// <summary>Что сказать при наведении: как пользоваться выключателем или почему его нет.</summary>
    [ObservableProperty] private string _toggleHint;

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isFuture;

    /// <summary>Применяется ли эта правка. Привязка односторонняя: переключает команда.</summary>
    [ObservableProperty] private bool _enabled = true;

    public HistoryEntryViewModel(int target, string label)
    {
        _target = target;
        _label = label;
        _toggleHint = "";
    }

    public HistoryEntryViewModel(int target, string label, IDocumentCommand command,
                                 bool enabled, bool canToggle, string toggleHint)
    {
        _target = target;
        _label = label;
        Command = command;
        _enabled = enabled;
        _canToggle = canToggle;
        _toggleHint = toggleHint;
    }

    /// <summary>
    /// Напомнить привязке нынешнее Enabled. Щелчок по галочке переворачивает её на экране
    /// ещё до команды; если история отказала, Enabled не поменялся, и без этого галочка
    /// осталась бы перевёрнутой (раньше строку просто пересоздавали).
    /// </summary>
    public void ReassertEnabled() => OnPropertyChanged(nameof(Enabled));
}
