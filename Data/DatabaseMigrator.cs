using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace InventarioProVisual.Data;

public static class DatabaseMigrator
{
    public static void MigrateToSupabase(string supabaseConnStr)
    {
        if (string.IsNullOrWhiteSpace(supabaseConnStr))
        {
            throw new ArgumentException("La cadena de conexión de Supabase no es válida.");
        }

        // 1. Migrar Master DB (public schema)
        using (var localMaster = new SqliteConnection(MasterDb.ConnString))
        {
            localMaster.Open();

            using (var supabaseMaster = new NpgsqlConnection(supabaseConnStr))
            {
                supabaseMaster.Open();

                // Inicializar tablas maestras en Supabase public schema
                supabaseMaster.Execute(
                    """
                    CREATE TABLE IF NOT EXISTS Empresas (
                        Id SERIAL PRIMARY KEY,
                        Nombre TEXT NOT NULL,
                        Activa INTEGER NOT NULL DEFAULT 1,
                        DbFileName TEXT NOT NULL UNIQUE,
                        FechaRegistro TEXT NOT NULL
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

                // Copiar Empresas (eliminando datos previos en Supabase para evitar conflictos de clave primaria)
                supabaseMaster.Execute("TRUNCATE TABLE UsuariosGlobales CASCADE");
                supabaseMaster.Execute("TRUNCATE TABLE Empresas CASCADE");
                supabaseMaster.Execute("TRUNCATE TABLE SuperAdmins CASCADE");

                var empresas = localMaster.Query("SELECT * FROM Empresas").ToList();
                foreach (var emp in empresas)
                {
                    supabaseMaster.Execute(
                        "INSERT INTO Empresas (Id, Nombre, Activa, DbFileName, FechaRegistro) VALUES (@Id, @Nombre, @Activa, @DbFileName, @FechaRegistro)",
                        (object)emp);
                }
                supabaseMaster.Execute("SELECT setval('empresas_id_seq', COALESCE((SELECT MAX(Id) FROM Empresas), 1))");

                // Copiar UsuariosGlobales
                var usrGlob = localMaster.Query("SELECT * FROM UsuariosGlobales").ToList();
                foreach (var usr in usrGlob)
                {
                    supabaseMaster.Execute(
                        "INSERT INTO UsuariosGlobales (Id, NombreUsuario, DbFileName, EmpresaId) VALUES (@Id, @NombreUsuario, @DbFileName, @EmpresaId)",
                        (object)usr);
                }
                supabaseMaster.Execute("SELECT setval('usuariosglobales_id_seq', COALESCE((SELECT MAX(Id) FROM UsuariosGlobales), 1))");

                // Copiar SuperAdmins
                var superAdmins = localMaster.Query("SELECT * FROM SuperAdmins").ToList();
                foreach (var sa in superAdmins)
                {
                    supabaseMaster.Execute(
                        "INSERT INTO SuperAdmins (Id, NombreUsuario, PasswordHash) VALUES (@Id, @NombreUsuario, @PasswordHash)",
                        (object)sa);
                }
                supabaseMaster.Execute("SELECT setval('superadmins_id_seq', COALESCE((SELECT MAX(Id) FROM SuperAdmins), 1))");

                // 2. Migrar cada inquilino (Empresa)
                foreach (var emp in empresas)
                {
                    var dbFileName = (string)emp.DbFileName;
                    var localDbPath = Path.Combine(MasterDb.StorageRootPath, dbFileName);

                    if (!File.Exists(localDbPath))
                    {
                        continue; // Si no existe el archivo físico, saltamos
                    }

                    var schemaName = dbFileName.Replace(".db", "").Replace("-", "_").ToLower();

                    // Inicializar el esquema y las tablas del inquilino en Supabase
                    using (var connTemp = new NpgsqlConnection(supabaseConnStr))
                    {
                        connTemp.Open();
                        using (var schemaCmd = connTemp.CreateCommand())
                        {
                            schemaCmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {schemaName}; SET search_path TO {schemaName};";
                            schemaCmd.ExecuteNonQuery();
                        }

                        // Inicializar tablas en este esquema
                        Db.InitializeDatabaseSchema(dbFileName);
                    }

                    // Copiar datos de las tablas
                    using (var localTenant = new SqliteConnection($"Data Source={localDbPath}"))
                    {
                        localTenant.Open();

                        using (var supabaseTenant = new NpgsqlConnection(supabaseConnStr))
                        {
                            supabaseTenant.Open();
                            using (var searchCmd = supabaseTenant.CreateCommand())
                            {
                                searchCmd.CommandText = $"SET search_path TO {schemaName};";
                                searchCmd.ExecuteNonQuery();
                            }

                            // Limpiar tablas para evitar duplicados
                            string[] tables = {
                                "PagosCuentas", "CuentasPorCobrar", "Combos", "MovimientosInventario", "Clientes",
                                "PagosNomina", "Empleados", "Gastos", "Auditoria", "Configuracion", "DetallesCotizacion",
                                "Cotizaciones", "Ventas", "Facturas", "Caja", "Productos", "Usuarios"
                            };

                            foreach (var tbl in tables)
                            {
                                try
                                {
                                    supabaseTenant.Execute($"TRUNCATE TABLE {tbl} CASCADE");
                                }
                                catch { /* Puede no existir aún */ }
                            }

                            // Copiar Usuarios
                            CopyTable(localTenant, supabaseTenant, "Usuarios",
                                "INSERT INTO Usuarios (Id, NombreUsuario, PasswordHash, NombreCompleto, Rol, Activo, FechaCreacion, UltimoAcceso, Permisos) VALUES (@Id, @NombreUsuario, @PasswordHash, @NombreCompleto, @Rol, @Activo, @FechaCreacion, @UltimoAcceso, @Permisos)",
                                "usuarios_id_seq");

                            // Copiar Productos
                            CopyTable(localTenant, supabaseTenant, "Productos",
                                "INSERT INTO Productos (Id, Nombre, Precio, Stock, CodigoBarras) VALUES (@Id, @Nombre, @Precio, @Stock, @CodigoBarras)",
                                "productos_id_seq");

                            // Copiar Caja
                            CopyTable(localTenant, supabaseTenant, "Caja",
                                "INSERT INTO Caja (Id, UsuarioId, Apertura, Cierre, SaldoInicial, SaldoFinal, Estado) VALUES (@Id, @UsuarioId, @Apertura, @Cierre, @SaldoInicial, @SaldoFinal, @Estado)",
                                "caja_id_seq");

                            // Copiar Facturas
                            CopyTable(localTenant, supabaseTenant, "Facturas",
                                "INSERT INTO Facturas (Id, ClienteId, CajaId, Total, MetodoPago, Fecha, Ncf, UsuarioId, Estado) VALUES (@Id, @ClienteId, @CajaId, @Total, @MetodoPago, @Fecha, @Ncf, @UsuarioId, @Estado)",
                                "facturas_id_seq");

                            // Copiar Ventas
                            CopyTable(localTenant, supabaseTenant, "Ventas",
                                "INSERT INTO Ventas (Id, FacturaId, ProductoId, CajaId, Cantidad, PrecioUnitario, Total, Fecha, ClienteId, MetodoPago) VALUES (@Id, @FacturaId, @ProductoId, @CajaId, @Cantidad, @PrecioUnitario, @Total, @Fecha, @ClienteId, @MetodoPago)",
                                "ventas_id_seq");

                            // Copiar Cotizaciones
                            CopyTable(localTenant, supabaseTenant, "Cotizaciones",
                                "INSERT INTO Cotizaciones (Id, Cliente, Fecha, FechaVencimiento, DescuentoPorcentaje, DescuentoMonto, Total, Estado, ClienteId) VALUES (@Id, @Cliente, @Fecha, @FechaVencimiento, @DescuentoPorcentaje, @DescuentoMonto, @Total, @Estado, @ClienteId)",
                                "cotizaciones_id_seq");

                            // Copiar DetallesCotizacion
                            CopyTable(localTenant, supabaseTenant, "DetallesCotizacion",
                                "INSERT INTO DetallesCotizacion (Id, CotizacionId, ProductoId, NombreProducto, Cantidad, PrecioUnitario) VALUES (@Id, @CotizacionId, @ProductoId, @NombreProducto, @Cantidad, @PrecioUnitario)",
                                "detallescotizacion_id_seq");

                            // Copiar Configuracion
                            CopyTable(localTenant, supabaseTenant, "Configuracion",
                                "INSERT INTO Configuracion (Id, Clave, Valor) VALUES (@Id, @Clave, @Valor)",
                                "configuracion_id_seq");

                            // Copiar Auditoria
                            CopyTable(localTenant, supabaseTenant, "Auditoria",
                                "INSERT INTO Auditoria (Id, UsuarioId, Usuario, Rol, Modulo, Accion, Detalle, FechaHora, Equipo) VALUES (@Id, @UsuarioId, @Usuario, @Rol, @Modulo, @Accion, @Detalle, @FechaHora, @Equipo)",
                                "auditoria_id_seq");

                            // Copiar Gastos
                            CopyTable(localTenant, supabaseTenant, "Gastos",
                                "INSERT INTO Gastos (Id, Concepto, Monto, Fecha, Categoria, UsuarioId) VALUES (@Id, @Concepto, @Monto, @Fecha, @Categoria, @UsuarioId)",
                                "gastos_id_seq");

                            // Copiar Empleados
                            CopyTable(localTenant, supabaseTenant, "Empleados",
                                "INSERT INTO Empleados (Id, Nombre, Cargo, Salario, FechaIngreso, Activo) VALUES (@Id, @Nombre, @Cargo, @Salario, @FechaIngreso, @Activo)",
                                "empleados_id_seq");

                            // Copiar PagosNomina
                            CopyTable(localTenant, supabaseTenant, "PagosNomina",
                                "INSERT INTO PagosNomina (Id, EmpleadoId, Monto, FechaPago, Periodo) VALUES (@Id, @EmpleadoId, @Monto, @FechaPago, @Periodo)",
                                "pagosnomina_id_seq");

                            // Copiar Clientes
                            CopyTable(localTenant, supabaseTenant, "Clientes",
                                "INSERT INTO Clientes (Id, Nombre, Telefono, Direccion, Rnc) VALUES (@Id, @Nombre, @Telefono, @Direccion, @Rnc)",
                                "clientes_id_seq");

                            // Copiar MovimientosInventario
                            CopyTable(localTenant, supabaseTenant, "MovimientosInventario",
                                "INSERT INTO MovimientosInventario (Id, ProductoId, Tipo, Cantidad, Motivo, Fecha, UsuarioId) VALUES (@Id, @ProductoId, @Tipo, @Cantidad, @Motivo, @Fecha, @UsuarioId)",
                                "movimientosinventario_id_seq");

                            // Copiar Combos
                            CopyTable(localTenant, supabaseTenant, "Combos",
                                "INSERT INTO Combos (Id, ComboId, ProductoId, Cantidad) VALUES (@Id, @ComboId, @ProductoId, @Cantidad)",
                                "combos_id_seq");

                            // Copiar CuentasPorCobrar
                            CopyTable(localTenant, supabaseTenant, "CuentasPorCobrar",
                                "INSERT INTO CuentasPorCobrar (Id, ClienteId, DeudaTotal, UltimaActualizacion) VALUES (@Id, @ClienteId, @DeudaTotal, @UltimaActualizacion)",
                                "cuentasporcobrar_id_seq");

                            // Copiar PagosCuentas
                            CopyTable(localTenant, supabaseTenant, "PagosCuentas",
                                "INSERT INTO PagosCuentas (Id, ClienteId, Monto, MetodoPago, Referencia, Fecha, UsuarioId) VALUES (@Id, @ClienteId, @Monto, @MetodoPago, @Referencia, @Fecha, @UsuarioId)",
                                "pagoscuentas_id_seq");
                        }
                    }
                }
            }
        }
    }

    private static void CopyTable(SqliteConnection source, NpgsqlConnection dest, string tableName, string insertQuery, string seqName)
    {
        try
        {
            var rows = source.Query($"SELECT * FROM {tableName}").ToList();
            foreach (var row in rows)
            {
                dest.Execute(insertQuery, (object)row);
            }
            // Reset serial sequence counter
            dest.Execute($"SELECT setval('{seqName}', COALESCE((SELECT MAX(Id) FROM {tableName}), 1))");
        }
        catch (Exception ex)
        {
            // Si la tabla no existe en la BD de origen por alguna razón, se ignora
            System.Diagnostics.Trace.TraceWarning($"No se pudo migrar la tabla {tableName}: {ex.Message}");
        }
    }
}
