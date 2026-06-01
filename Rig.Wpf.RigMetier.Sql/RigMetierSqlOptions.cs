namespace Rig.Wpf.RigMetier.Sql;

/// <summary>
/// Options de la couche SQL directe : chaîne de connexion ADO.NET vers la base
/// RIG (ex : <c>Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;...</c>).
/// </summary>
public sealed class RigMetierSqlOptions
{
    public string ConnectionString { get; set; } = "";
}
