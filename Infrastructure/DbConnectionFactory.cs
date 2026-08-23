using InventarioProVisual.Data;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Threading.Tasks;

namespace FacturixWeb.Infrastructure;

public interface IDbConnectionFactory
{
    Task<SqliteConnection> CreateConnectionAsync();
}

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly ITenantProvider _tenantProvider;

    public DbConnectionFactory(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    public async Task<SqliteConnection> CreateConnectionAsync()
    {
        var dbName = _tenantProvider.GetCurrentTenantDbName();
        // Garantizar que la base de datos esté inicializada antes de entregar la conexión.
        // Como estamos quitando esto del método Open() estático de Db, lo hacemos aquí.
        Db.InitializeDatabaseSchema(dbName);
        
        var path = Path.Combine(Db.StorageRootPath, dbName);
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        
        // Habilitar Foreign Keys por defecto en SQLite
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        await cmd.ExecuteNonQueryAsync();
        
        return conn;
    }
}
