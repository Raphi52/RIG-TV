using System;

namespace Rig.Wpf.Mvvm.Mapping;

/// <summary>
/// Contrat de mapping entre une entité <typeparamref name="TEntity"/> (typiquement
/// issue de RigMetier) et un <typeparamref name="TViewModel"/> WPF. Sépare clairement
/// la persistance de la présentation, à la différence du <c>Ult.Link(IDB)</c> legacy
/// qui mêlait UI et data layer.
/// </summary>
public interface IDbObjectMapper<TEntity, TViewModel>
    where TViewModel : ViewModelBase
{
    void Load(TEntity source, TViewModel target);
    void Save(TViewModel source, TEntity target);
}

public abstract class DbObjectMapper<TEntity, TViewModel> : IDbObjectMapper<TEntity, TViewModel>
    where TViewModel : ViewModelBase
{
    public void Load(TEntity source, TViewModel target)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (target is null) throw new ArgumentNullException(nameof(target));
        DoLoad(source, target);
    }

    public void Save(TViewModel source, TEntity target)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (target is null) throw new ArgumentNullException(nameof(target));
        DoSave(source, target);
    }

    protected abstract void DoLoad(TEntity source, TViewModel target);
    protected abstract void DoSave(TViewModel source, TEntity target);
}
