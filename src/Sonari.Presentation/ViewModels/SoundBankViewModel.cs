using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonari.Infrastructure.Soundboard;

namespace Sonari.Presentation.ViewModels;

/// <summary>Un banco (pestaña) del panel de sonidos.</summary>
public sealed partial class SoundBankViewModel : ObservableObject
{
    private readonly Action _onEdited;

    [ObservableProperty]
    private string _name;

    public SoundBankViewModel(SoundBank bank, Action onEdited, Action<SoundItemViewModel> onSoundEdited)
    {
        Id = bank.Id;
        _name = bank.Name;
        _onEdited = onEdited;
        foreach (SoundEntry entry in bank.Sounds)
        {
            Sounds.Add(new SoundItemViewModel(entry, onSoundEdited));
        }
    }

    public Guid Id { get; }

    public ObservableCollection<SoundItemViewModel> Sounds { get; } = [];

    public SoundBank ToBank() => new(Id, Name, Sounds.Select(s => s.ToEntry()).ToList());

    partial void OnNameChanged(string value) => _onEdited();
}
