using InventarioProVisual.Data;
using System;
using System.Data.Common;
using System.IO;
using System.Threading.Tasks;

namespace FacturixWeb.Infrastructure;

public interface IDbConnectionFactory
{
    Task<DbConnection> CreateConnectionAsync();
}

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly ITenantProvider _tenantProvider;

    public DbConnectionFactory(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    public async Task<DbConnection> CreateConnectionAsync()
    {
        var dbName = _tenantProvider.GetCurrentTenantDbName();
        // Garantizar que la base de datos esté inicializada antes de entregar la conexión.
        Db.InitializeDatabaseSchema(dbName);
        
        var supabaseConnStr = Environment.GetEnvironmentVariable("SUPABASE_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(supabaseConnStr))
        {
            var conn = new Npgsql.NpgsqlConnection(supabaseConnStr);
            await conn.OpenAsync();
            
            // Para PostgreSQL en Supabase, usamos esquemas para separar los inquilinos
            var schemaName = dbName.Replace(".db", "").Replace("-", "_").ToLower();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {schemaName}; SET search_path TO {schemaName};";
            await cmd.ExecuteNonQueryAsync();
            
            return conn;
        }
        else
        {
            var path = Path.Combine(Db.StorageRootPath, dbName);
            var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await conn.OpenAsync();
            
            // Habilitar Foreign Keys por defecto en SQLite
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            await cmd.ExecuteNonQueryAsync();
            
            return conn;
        }
    }
}
