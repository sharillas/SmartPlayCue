using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace StagePlayout.App;

/// <summary>ViewModel de uma layer na lista dinâmica de layers (até 4).</summary>
public sealed class LayerVm : INotifyPropertyChanged
{
    public LayerState State { get; }
    public int Index { get; } // 0-based; slot do compositor = 2 + Index

    public LayerVm(LayerState state, int index)
    {
        State = state;
        Index = index;
    }

    public string Label => $"L{Index + 1}";

    public string Name { get; private set; } = "(vazio)";
    public bool HasFile { get; private set; }
    public string ToggleText => State.Visible ? "OCULTAR" : "MOSTRAR";
    public string BlendText => State.BlendAdd ? "BLEND: ADD" : "BLEND: NORMAL";
    public bool SoundOn => !State.Muted;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public void SetFile(string? file)
    {
        State.File = file;
        Name = string.IsNullOrEmpty(file) ? "(vazio)" : Path.GetFileName(file);
        HasFile = !string.IsNullOrEmpty(file);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HasFile));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(BlendText));
        OnPropertyChanged(nameof(SoundOn));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HasFile));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
