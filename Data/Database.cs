using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Dapper;
using InventarioProVisual.Models;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using System.Linq;

namespace InventarioProVisual.Data;

public static class Db
{
    public readonly record struct ResumenVentasHoy(double TotalVendido, long NumeroVentas);
    public static readonly string StorageRootPath = ResolveStorageRootPath();
    private const string LegacyDbName = "fausto_empanada.db";
    public const string CurrentDbName = "facturix.db";
    private const string BackupFilePrefix = "facturix_backup";
    internal const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string BackupDateFormat = "yyyy-MM-dd";
    private const int BackupRetentionDays = 30;
    private const int PasswordIterations = 120000;
    private const string PasswordPrefix = "PBKDF2$SHA256";
    private const string BackupFolderConfigKey = "CARPETA_BACKUP";
    public const string LogoEmpresaIcoConfigKey = "LOGO_EMPRESA_ICO_BASE64";

    private static IHttpContextAccessor? _httpContextAccessor;

    public static string CurrentTenantDbName
    {
        get
        {
            var context = _httpContextAccessor?.HttpContext;
            if (context?.User?.Identity?.IsAuthenticated == true)
            {
                var tenantClaim = context.User.Claims.FirstOrDefault(c => c.Type == "TenantDb");
                if (tenantClaim != null && !string.IsNullOrEmpty(tenantClaim.Value))
                {
                    return tenantClaim.Value;
                }
            }
            return CurrentDbName;
        }
    }

    public static string DbPath => Path.Combine(StorageRootPath, CurrentTenantDbName);
    public static string ConnString => $"Data Source={DbPath}";
    public static string DefaultBackupFolderPath => Path.Combine(StorageRootPath, "Backups");

    public static string GetSecureTempPath(string fileName)
    {
        var tempDir = Path.Combine(StorageRootPath, "Temp");
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, fileName);
    }

    private static readonly HashSet<string> _initializedTenants = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        MigrateLegacyDatabaseIfNeeded();
        InitializeDatabaseSchema(CurrentDbName);
    }

    public static void InitializeDatabaseSchema(string dbFileName)
    {
        _initializedTenants.Add(dbFileName);
        var path = Path.Combine(StorageRootPath, dbFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Usuarios (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                NombreUsuario TEXT NOT NULL UNIQUE,
                PasswordHash TEXT NOT NULL,
                NombreCompleto TEXT,
                Rol TEXT NOT NULL,
                Activo INTEGER NOT NULL DEFAULT 1,
                FechaCreacion TEXT NOT NULL,
                UltimoAcceso TEXT,
                Permisos TEXT
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Productos (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Nombre TEXT NOT NULL,
                Precio REAL NOT NULL,
                Stock INTEGER NOT NULL DEFAULT 0
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Caja (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UsuarioId INTEGER NOT NULL,
                Apertura TEXT NOT NULL,
                Cierre TEXT,
                SaldoInicial REAL NOT NULL,
                SaldoFinal REAL NOT NULL DEFAULT 0,
                Estado TEXT NOT NULL DEFAULT 'CERRADA'
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Facturas (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClienteId INTEGER,
                CajaId INTEGER NOT NULL,
                Total REAL NOT NULL,
                MetodoPago TEXT NOT NULL DEFAULT 'EFECTIVO',
                Fecha TEXT NOT NULL,
                Ncf TEXT,
                UsuarioId INTEGER NOT NULL,
                Estado TEXT NOT NULL DEFAULT 'Pagada'
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Ventas (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FacturaId INTEGER,
                ProductoId INTEGER NOT NULL,
                CajaId INTEGER NOT NULL,
                Cantidad INTEGER NOT NULL,
                PrecioUnitario REAL NOT NULL,
                Total REAL NOT NULL,
                Fecha TEXT NOT NULL,
                FOREIGN KEY(FacturaId) REFERENCES Facturas(Id) ON DELETE CASCADE
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Cotizaciones (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Cliente TEXT NOT NULL,
                Fecha TEXT NOT NULL,
                FechaVencimiento TEXT NOT NULL,
                DescuentoPorcentaje REAL NOT NULL DEFAULT 0,
                DescuentoMonto REAL NOT NULL DEFAULT 0,
                Total REAL NOT NULL DEFAULT 0,
                Estado TEXT NOT NULL DEFAULT 'Pendiente'
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS DetallesCotizacion (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CotizacionId INTEGER NOT NULL,
                ProductoId INTEGER NOT NULL,
                NombreProducto TEXT NOT NULL DEFAULT '',
                Cantidad INTEGER NOT NULL,
                PrecioUnitario REAL NOT NULL DEFAULT 0
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Configuracion (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Clave TEXT NOT NULL UNIQUE,
                Valor TEXT NOT NULL
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Auditoria (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UsuarioId INTEGER,
                Usuario TEXT NOT NULL,
                Rol TEXT NOT NULL,
                Modulo TEXT NOT NULL,
                Accion TEXT NOT NULL,
                Detalle TEXT NOT NULL DEFAULT '',
                FechaHora TEXT NOT NULL,
                Equipo TEXT NOT NULL DEFAULT ''
            )
            """);

        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Auditoria_FechaHora ON Auditoria(FechaHora DESC)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Auditoria_Modulo ON Auditoria(Modulo)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Auditoria_Usuario ON Auditoria(Usuario)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Ventas_Fecha ON Ventas(Fecha)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Ventas_CajaId ON Ventas(CajaId)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Caja_Estado_Id ON Caja(Estado, Id DESC)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Productos_Nombre ON Productos(Nombre)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Cotizaciones_Estado_Id ON Cotizaciones(Estado, Id DESC)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Cotizaciones_Cliente ON Cotizaciones(Cliente)");

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Gastos (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Concepto TEXT NOT NULL,
                Monto REAL NOT NULL,
                Fecha TEXT NOT NULL,
                Categoria TEXT NOT NULL,
                UsuarioId INTEGER
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Empleados (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Nombre TEXT NOT NULL,
                Cargo TEXT,
                Salario REAL NOT NULL DEFAULT 0,
                FechaIngreso TEXT NOT NULL,
                Activo INTEGER NOT NULL DEFAULT 1
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS PagosNomina (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EmpleadoId INTEGER NOT NULL,
                Monto REAL NOT NULL,
                FechaPago TEXT NOT NULL,
                Periodo TEXT NOT NULL,
                FOREIGN KEY(EmpleadoId) REFERENCES Empleados(Id)
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Clientes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Nombre TEXT NOT NULL,
                Telefono TEXT,
                Direccion TEXT,
                Rnc TEXT
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS MovimientosInventario (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProductoId INTEGER NOT NULL,
                Tipo TEXT NOT NULL,
                Cantidad INTEGER NOT NULL,
                Motivo TEXT NOT NULL,
                Fecha TEXT NOT NULL,
                UsuarioId INTEGER NOT NULL
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Combos (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ComboId INTEGER NOT NULL,
                ProductoId INTEGER NOT NULL,
                Cantidad INTEGER NOT NULL,
                FOREIGN KEY(ComboId) REFERENCES Productos(Id),
                FOREIGN KEY(ProductoId) REFERENCES Productos(Id)
            )
            """);

        EnsureColumn(conn, "Productos", "CodigoBarras", "TEXT");
        EnsureColumn(conn, "Ventas", "ClienteId", "INTEGER");
        EnsureColumn(conn, "Ventas", "MetodoPago", "TEXT NOT NULL DEFAULT 'EFECTIVO'");
        EnsureColumn(conn, "Ventas", "FacturaId", "INTEGER");
        EnsureColumn(conn, "Cotizaciones", "ClienteId", "INTEGER");
        EnsureColumn(conn, "Usuarios", "Permisos", "TEXT");
        
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS CuentasPorCobrar (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClienteId INTEGER NOT NULL UNIQUE,
                DeudaTotal REAL NOT NULL DEFAULT 0,
                UltimaActualizacion TEXT NOT NULL,
                FOREIGN KEY(ClienteId) REFERENCES Clientes(Id)
            )
            """);

        conn.Execute("""
            CREATE TABLE IF NOT EXISTS PagosCuentas (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClienteId INTEGER NOT NULL,
                Monto REAL NOT NULL,
                MetodoPago TEXT NOT NULL,
                Referencia TEXT,
                Fecha TEXT NOT NULL,
                UsuarioId INTEGER NOT NULL,
                FOREIGN KEY(ClienteId) REFERENCES Clientes(Id)
            )
            """);
            
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Productos_CodigoBarras ON Productos(CodigoBarras)");
        conn.Execute("CREATE INDEX IF NOT EXISTS IX_Facturas_Fecha ON Facturas(Fecha)");

        conn.Execute("DROP TABLE IF EXISTS Insumos");

        EnsureSchema(conn);
        SeedDefaults(conn);
    }

    internal static SqliteConnection OpenInternal()
    {
        var dbName = CurrentTenantDbName;
        if (!_initializedTenants.Contains(dbName))
        {
            InitializeDatabaseSchema(dbName);
        }

        var conn = new SqliteConnection(ConnString);
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON;");
        return conn;
    }

    public static string HashPassword(string pwd)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(pwd, salt, PasswordIterations, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(32);
        return $"{PasswordPrefix}${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        if (storedHash.StartsWith($"{PasswordPrefix}$", StringComparison.Ordinal))
        {
            var parts = storedHash.Split('$');
            if (parts.Length != 5 || !int.TryParse(parts[2], out var iterations))
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[3]);
                var expected = Convert.FromBase64String(parts[4]);
                using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
                var actual = pbkdf2.GetBytes(expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }

        return string.Equals(HashPasswordLegacy(password), storedHash, StringComparison.Ordinal);
    }

    public static void RegistrarAuditoria(
        int? usuarioId,
        string usuario,
        string rol,
        string modulo,
        string accion,
        string detalle = "",
        System.Data.IDbTransaction? tran = null)
    {
        try
        {
            const string sql = """
                INSERT INTO Auditoria (UsuarioId, Usuario, Rol, Modulo, Accion, Detalle, FechaHora, Equipo)
                VALUES (@usuarioId, @usuario, @rol, @modulo, @accion, @detalle, @fechaHora, @equipo)
                """;
            var param = new
            {
                usuarioId,
                usuario = LimpiarTexto(usuario, 50, "sistema"),
                rol = LimpiarTexto(rol, 30, "Desconocido"),
                modulo = LimpiarTexto(modulo, 60, "General"),
                accion = LimpiarTexto(accion, 120, "Sin acción"),
                detalle = LimpiarTexto(detalle, 1000, string.Empty),
                fechaHora = DateTime.Now.ToString(DateTimeFormat),
                equipo = LimpiarTexto(Environment.MachineName, 80, string.Empty)
            };

            if (tran != null) {
                tran.Connection?.Execute(sql, param, tran);
            } else {
                using var conn = OpenInternal();
                conn.Execute(sql, param);
            }
        }
        catch
        {
            Trace.TraceWarning("Db.RegistrarAuditoria no pudo registrar la acción.");
        }
    }

    public static IReadOnlyList<AuditoriaRegistro> ObtenerAuditoria(
        string filtro = "",
        string modulo = "Todos",
        DateTime? fechaDesde = null,
        DateTime? fechaHasta = null,
        int limite = 700)
    {
        using var conn = OpenInternal();

        var moduloFiltro = string.Equals(modulo, "Todos", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : LimpiarTexto(modulo, 60, string.Empty);

        var filtroNormalizado = LimpiarTexto(filtro, 120, string.Empty);
        var like = $"%{filtroNormalizado}%";

        var desde = (fechaDesde ?? DateTime.Today.AddDays(-7)).Date;
        var hasta = (fechaHasta ?? DateTime.Today).Date.AddDays(1).AddSeconds(-1);
        if (hasta < desde)
        {
            (desde, hasta) = (hasta.Date, desde.Date.AddDays(1).AddSeconds(-1));
        }

        var maxRows = Math.Clamp(limite, 50, 2000);

        return conn.Query<AuditoriaRegistro>(
            """
            SELECT
                a.Id,
                a.UsuarioId,
                a.Usuario,
                COALESCE(NULLIF(TRIM(u.NombreCompleto), ''), NULLIF(TRIM(uAlt.NombreCompleto), ''), '') AS NombreCompleto,
                a.Rol,
                a.Modulo,
                a.Accion,
                a.Detalle,
                a.FechaHora,
                a.Equipo
            FROM Auditoria a
            LEFT JOIN Usuarios u ON u.Id = a.UsuarioId
            LEFT JOIN Usuarios uAlt ON uAlt.NombreUsuario = a.Usuario AND a.UsuarioId IS NULL
            WHERE
                (@modulo = '' OR a.Modulo = @modulo)
                AND (@filtro = '' OR a.Usuario LIKE @like OR a.Rol LIKE @like OR a.Modulo LIKE @like OR a.Accion LIKE @like OR a.Detalle LIKE @like
                    OR u.NombreCompleto LIKE @like OR uAlt.NombreCompleto LIKE @like)
                AND datetime(a.FechaHora) BETWEEN datetime(@desde) AND datetime(@hasta)
            ORDER BY a.Id DESC
            LIMIT @maxRows
            """,
            new
            {
                modulo = moduloFiltro,
                filtro = filtroNormalizado,
                like,
                desde = desde.ToString(DateTimeFormat),
                hasta = hasta.ToString(DateTimeFormat),
                maxRows
            }).ToList();
    }

    public static string GetProjectRootPath()
    {
        return StorageRootPath;
    }

    public static string GetBackupFolderPath()
    {
        var defaultPath = DefaultBackupFolderPath;

        try
        {
            using var conn = OpenInternal();
            var configured = conn.ExecuteScalar<string>("SELECT Valor FROM Configuracion WHERE Clave = @key", new { key = BackupFolderConfigKey }) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return defaultPath;
            }

            var clean = configured.Trim();
            if (!Path.IsPathRooted(clean))
            {
                clean = Path.Combine(StorageRootPath, clean);
            }

            return Path.GetFullPath(clean);
        }
        catch
        {
            return defaultPath;
        }
    }

    public static bool EnsureDailyBackup()
    {
        var today = DateTime.Now.ToString(BackupDateFormat);
        using var conn = OpenInternal();
        var ultimoBackup = conn.ExecuteScalar<string>("SELECT Valor FROM Configuracion WHERE Clave = 'ULTIMO_BACKUP_FECHA'") ?? string.Empty;
        if (string.Equals(ultimoBackup, today, StringComparison.Ordinal))
        {
            return false;
        }

        CreateBackupNow();
        return true;
    }

    public static string CreateBackupNow()
    {
        var backupFolder = GetBackupFolderPath();
        Directory.CreateDirectory(backupFolder);

        var backupPath = Path.Combine(backupFolder, $"{BackupFilePrefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
        using (var conn = OpenInternal())
        {
            conn.Execute("PRAGMA busy_timeout = 5000;");
            var escapedPath = backupPath.Replace("'", "''");
            conn.Execute($"VACUUM INTO '{escapedPath}'");
        }

        var today = DateTime.Now.ToString(BackupDateFormat);
        using (var conn = OpenInternal())
        {
            conn.Execute("INSERT INTO Configuracion (Clave, Valor) VALUES ('ULTIMO_BACKUP_FECHA', @today) ON CONFLICT(Clave) DO UPDATE SET Valor = excluded.Valor", new { today });
        }
        LimpiarBackupsAntiguos(backupFolder);
        return backupPath;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        EnsureColumn(conn, "Usuarios", "NombreCompleto", "TEXT");
        EnsureColumn(conn, "Usuarios", "UltimoAcceso", "TEXT");
        EnsureColumn(conn, "Usuarios", "FechaCreacion", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Cotizaciones", "Estado", "TEXT NOT NULL DEFAULT 'Pendiente'");
        EnsureColumn(conn, "Cotizaciones", "Total", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(conn, "Cotizaciones", "FechaVencimiento", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Cotizaciones", "DescuentoPorcentaje", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(conn, "Cotizaciones", "DescuentoMonto", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(conn, "DetallesCotizacion", "PrecioUnitario", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(conn, "DetallesCotizacion", "NombreProducto", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Auditoria", "UsuarioId", "INTEGER");
        EnsureColumn(conn, "Auditoria", "Usuario", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Auditoria", "Rol", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Auditoria", "Modulo", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Auditoria", "Accion", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Auditoria", "Detalle", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Auditoria", "FechaHora", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Auditoria", "Equipo", "TEXT NOT NULL DEFAULT ''");

        conn.Execute("UPDATE Cotizaciones SET FechaVencimiento = COALESCE(NULLIF(FechaVencimiento, ''), datetime(Fecha, '+7 days'))");
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        var schema = conn.Query<TableInfoRow>($"PRAGMA table_info({table})").ToList();
        if (schema.Count == 0)
        {
            return;
        }

        foreach (var item in schema)
        {
            if (string.Equals(item.name, column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        conn.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void SeedDefaults(SqliteConnection conn)
    {
        var now = DateTime.Now.ToString(DateTimeFormat);

        if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Configuracion") == 0)
        {
            var config = new Dictionary<string, string>
            {
                ["NOMBRE_NEGOCIO"] = "Facturix",
                ["RNC"] = "123-456789-1",
                ["TELEFONO"] = "809-555-1234",
                ["DIRECCION"] = "Calle Principal",
                ["MODO_OSCURO"] = "false",
                [BackupFolderConfigKey] = string.Empty,
                ["ULTIMO_BACKUP_FECHA"] = string.Empty,
                [LogoEmpresaIcoConfigKey] = string.Empty
            };

            foreach (var item in config)
            {
                conn.Execute(
                    "INSERT INTO Configuracion (Clave, Valor) VALUES (@Clave, @Valor)",
                    new { Clave = item.Key, Valor = item.Value });
            }
        }
        else
        {
            EnsureConfig(conn, "NOMBRE_NEGOCIO", "Facturix");
            EnsureConfig(conn, "RNC", "123-456789-1");
            EnsureConfig(conn, "TELEFONO", "809-555-1234");
            EnsureConfig(conn, "DIRECCION", "Calle Principal");
            EnsureConfig(conn, "MODO_OSCURO", "false");
            EnsureConfig(conn, BackupFolderConfigKey, string.Empty);
            EnsureConfig(conn, "ULTIMO_BACKUP_FECHA", string.Empty);
            EnsureConfig(conn, LogoEmpresaIcoConfigKey, string.Empty);

            conn.Execute("UPDATE Configuracion SET Valor = 'Facturix' WHERE Clave = 'NOMBRE_NEGOCIO' AND Valor = 'Fausto Empanada'");
        }

        conn.Execute("DELETE FROM Configuracion WHERE Clave = 'ITBIS'");
    }

    private static string HashPasswordLegacy(string pwd)
    {
        return Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pwd)));
    }

    private static string ResolveStorageRootPath()
    {
        var envPath = Environment.GetEnvironmentVariable("FACTURIX_DATA_DIR") ?? Environment.GetEnvironmentVariable("DATA_DIR");
        if (!string.IsNullOrEmpty(envPath))
        {
            Directory.CreateDirectory(envPath);
            return envPath;
        }

        var persistentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FacturixData");
        
        var localDb = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CurrentDbName);
        if (File.Exists(localDb) && !Directory.Exists(persistentPath))
        {
            Directory.CreateDirectory(persistentPath);
            File.Move(localDb, Path.Combine(persistentPath, CurrentDbName));
        }

        return persistentPath;
    }

    private static void MigrateLegacyDatabaseIfNeeded()
    {
        if (File.Exists(DbPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LegacyDbName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CurrentDbName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Facturix", LegacyDbName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Facturix", CurrentDbName)
        };

        foreach (var legacyPath in candidates)
        {
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            File.Copy(legacyPath, DbPath, overwrite: false);
            break;
        }
    }

    private static void LimpiarBackupsAntiguos(string backupFolder)
    {
        var limite = DateTime.Now.Date.AddDays(-BackupRetentionDays);
        foreach (var file in Directory.GetFiles(backupFolder, "*.db"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime.Date < limite)
                {
                    info.Delete();
                }
            }
            catch
            {
                Trace.TraceWarning("Db.LimpiarBackupsAntiguos no pudo eliminar un archivo.");
            }
        }
    }

    private static void EnsureConfig(SqliteConnection conn, string key, string value)
    {
        if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Configuracion WHERE Clave = @key", new { key }) == 0)
        {
            conn.Execute("INSERT INTO Configuracion (Clave, Valor) VALUES (@key, @value)", new { key, value });
        }
    }

    private static string LimpiarTexto(string value, int maxLength, string fallback)
    {
        var clean = (value ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return fallback;
        }

        if (clean.Length > maxLength)
        {
            return clean[..maxLength];
        }

        return clean;
    }

    private sealed class TableInfoRow
    {
        public int cid { get; set; }
        public string name { get; set; } = string.Empty;
    }
}
