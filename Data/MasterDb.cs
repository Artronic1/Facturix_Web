using System;
using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace InventarioProVisual.Data;

public static class MasterDb
{
    public static string StorageRootPath => Db.StorageRootPath;
    private const string MasterDbName = "facturix_master.db";
    public static string DbPath => Path.Combine(StorageRootPath, MasterDbName);
    public static string ConnString => $"Data Source={DbPath}";

    // SuperAdmin config from user request
    private const string MasterUser = "c.rosario";
    private const string MasterPass = "!Maxis01";
    
    // Hash parameters matching Database.cs
    private const int PasswordIterations = 25000;
    private const string PasswordPrefix = "PBKDF2$SHA256";

    public static DbConnection CreateConnection()
    {
        var supabaseConnStr = FacturixWeb.Infrastructure.DbConnectionFactory.FormatConnectionString(Environment.GetEnvironmentVariable("SUPABASE_CONNECTION_STRING"));
        if (!string.IsNullOrEmpty(supabaseConnStr))
        {
            return new Npgsql.NpgsqlConnection(supabaseConnStr);
        }
        return new Microsoft.Data.Sqlite.SqliteConnection(ConnString);
    }

    public static void Initialize()
    {
        var supabaseConnStr = FacturixWeb.Infrastructure.DbConnectionFactory.FormatConnectionString(Environment.GetEnvironmentVariable("SUPABASE_CONNECTION_STRING"));
        if (string.IsNullOrEmpty(supabaseConnStr))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        }

        using var conn = CreateConnection();
        conn.Open();

        if (conn is Npgsql.NpgsqlConnection)
        {
            // Dialecto PostgreSQL
            conn.Execute(
                """
                CREATE TABLE IF NOT EXISTS Empresas (
                    Id SERIAL PRIMARY KEY,
                    Nombre TEXT NOT NULL,
                    Activa INTEGER NOT NULL DEFAULT 1,
                    DbFileName TEXT NOT NULL UNIQUE,
                    FechaRegistro TEXT NOT NULL,
                    Rnc TEXT DEFAULT '',
                    Telefono TEXT DEFAULT '',
                    Direccion TEXT DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS UsuariosGlobales (
                    Id SERIAL PRIMARY KEY,
                    NombreUsuario TEXT NOT NULL UNIQUE,
                    DbFileName TEXT NOT NULL,
                    EmpresaId INTEGER NOT NULL,
                    FOREIGN KEY(EmpresaId) REFERENCES Empresas(Id)
                );

                CREATE TABLE IF NOT EXISTS SuperAdmins (
                    Id SERIAL PRIMARY KEY,
                    NombreUsuario TEXT NOT NULL UNIQUE,
                    PasswordHash TEXT NOT NULL
                );
                """);
        }
        else
        {
            // Dialecto SQLite
            conn.Execute(
                """
                CREATE TABLE IF NOT EXISTS Empresas (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Nombre TEXT NOT NULL,
                    Activa INTEGER NOT NULL DEFAULT 1,
                    DbFileName TEXT NOT NULL UNIQUE,
                    FechaRegistro TEXT NOT NULL,
                    Rnc TEXT DEFAULT '',
                    Telefono TEXT DEFAULT '',
                    Direccion TEXT DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS UsuariosGlobales (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NombreUsuario TEXT NOT NULL UNIQUE,
                    DbFileName TEXT NOT NULL,
                    EmpresaId INTEGER NOT NULL,
                    FOREIGN KEY(EmpresaId) REFERENCES Empresas(Id)
                );

                CREATE TABLE IF NOT EXISTS SuperAdmins (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NombreUsuario TEXT NOT NULL UNIQUE,
                    PasswordHash TEXT NOT NULL
                );
                """);
        }

        EnsureMasterSchema(conn);

        // Ensure SuperAdmin exists
        var exists = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SuperAdmins WHERE NombreUsuario = @user", new { user = MasterUser });
        if (exists == 0)
        {
            var hash = HashPassword(MasterPass);
            conn.Execute("INSERT INTO SuperAdmins (NombreUsuario, PasswordHash) VALUES (@user, @hash)", new { user = MasterUser, hash });
        }

        // Migración de base de datos legada (sólo para SQLite local)
        if (conn is not Npgsql.NpgsqlConnection)
        {
            EnsureLegacyDbRegistered(conn);
        }
    }

    private static void EnsureLegacyDbRegistered(DbConnection conn)
    {
        var exists = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Empresas WHERE DbFileName = 'facturix.db'");
        if (exists == 0)
        {
            var facturixDbPath = Path.Combine(StorageRootPath, "FacturixWeb", "facturix.db");
            if (File.Exists(facturixDbPath))
            {
                // Register default company
                var fecha = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                conn.Execute("INSERT INTO Empresas (Nombre, Activa, DbFileName, FechaRegistro) VALUES ('Empresa Principal (Heredada)', 1, 'facturix.db', @fecha)", new { fecha });
                
                var empresaId = conn.ExecuteScalar<int>("SELECT Id FROM Empresas WHERE DbFileName = 'facturix.db'");
                
                // Read users from facturix.db and register them in master.db
                using var legacyConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={facturixDbPath}");
                legacyConn.Open();
                var usuarios = legacyConn.Query<string>("SELECT NombreUsuario FROM Usuarios");
                foreach(var user in usuarios)
                {
                    conn.Execute(
                        "INSERT OR IGNORE INTO UsuariosGlobales (NombreUsuario, DbFileName, EmpresaId) VALUES (@user, 'facturix.db', @empresaId)",
                        new { user, empresaId }
                    );
                }
            }
        }
    }

    private static void EnsureMasterSchema(DbConnection conn)
    {
        try
        {
            conn.Execute("ALTER TABLE Empresas ADD COLUMN Rnc TEXT DEFAULT ''");
        }
        catch { }
        try
        {
            conn.Execute("ALTER TABLE Empresas ADD COLUMN Telefono TEXT DEFAULT ''");
        }
        catch { }
        try
        {
            conn.Execute("ALTER TABLE Empresas ADD COLUMN Direccion TEXT DEFAULT ''");
        }
        catch { }
    }

    public static string HashPassword(string password)
    {
        byte[] salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"{PasswordPrefix}${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[0] != "PBKDF2" || parts[1] != "SHA256") return false;

        var iterations = int.Parse(parts[2]);
        var salt = Convert.FromBase64String(parts[3]);
        var expectedHash = Convert.FromBase64String(parts[4]);

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
