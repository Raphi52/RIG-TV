using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

/// <summary>
/// Chip de filtre toggleable. Quand <see cref="IsActive"/> change, déclenche le
/// callback <see cref="ToggleListener"/> qui pilote le rafraîchissement de la
/// CollectionView associée côté MainVM.
/// </summary>
public sealed partial class TagChipViewModel : ObservableObject
{
    public TagChipViewModel(string tag, int itemCount)
    {
        Tag = tag;
        this.itemCount = itemCount;
    }

    public string Tag { get; }
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private int itemCount;

    public Action? ToggleListener { get; set; }

    partial void OnIsActiveChanged(bool value) => ToggleListener?.Invoke();

    public string DisplayText => $"{Tag} ({ItemCount})";
}
