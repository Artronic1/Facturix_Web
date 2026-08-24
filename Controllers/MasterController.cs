using System;
using System.IO;
using System.Linq;
using Dapper;
using InventarioProVisual.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

[Authorize(Roles = "SuperAdmin")]
public class MasterController : Controller
{
    public IActionResult Index()
    {
        using var conn = MasterDb.CreateConnection();
        conn.Open();
        var query = @"
            SELECT e.Id, e.Nombre, e.DbFileName, e.FechaRegistro, e.Activa, e.Rnc, e.Telefono, e.Direccion, u.NombreUsuario as AdminUser
            FROM Empresas e
            LEFT JOIN UsuariosGlobales u ON e.Id = u.EmpresaId
            ORDER BY e.Id DESC";
            
        var empresas = conn.Query<EmpresaViewModel>(query).ToList();
        return View(empresas);
    }
    
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CrearEmpresa(string nombreEmpresa, string adminUser, string adminPass, string rnc, string telefono, string direccion)
    {
        if (string.IsNullOrWhiteSpace(nombreEmpresa) || string.IsNullOrWhiteSpace(adminUser) || string.IsNullOrWhiteSpace(adminPass))
        {
            TempData["Error"] = "Todos los campos son obligatorios.";
            return RedirectToAction(nameof(Index));
        }

        nombreEmpresa = nombreEmpresa.Trim();
        adminUser = adminUser.Trim();
        adminPass = adminPass.Trim();
        rnc = rnc?.Trim() ?? "";
        telefono = telefono?.Trim() ?? "";
        direccion = direccion?.Trim() ?? "";

        using var masterConn = MasterDb.CreateConnection();
        masterConn.Open();
        
        var userExists = masterConn.ExecuteScalar<int>("SELECT COUNT(*) FROM UsuariosGlobales WHERE LOWER(NombreUsuario) = LOWER(@adminUser)", new { adminUser });
        if (userExists > 0)
        {
            TempData["Error"] = $"El nombre de usuario '{adminUser}' ya está en uso por otra empresa. Por favor elige otro.";
            return RedirectToAction(nameof(Index));
        }
        
        var uuid = Guid.NewGuid().ToString("N");
        var dbFileName = $"facturix_{uuid}.db";
        var fecha = DateTime.Now.ToString(Db.DateTimeFormat);
        
        using var tran = masterConn.BeginTransaction();
        try
        {
            int empresaId;
            if (masterConn is Npgsql.NpgsqlConnection)
            {
                empresaId = masterConn.ExecuteScalar<int>(
                    "INSERT INTO Empresas (Nombre, Activa, DbFileName, FechaRegistro, Rnc, Telefono, Direccion) VALUES (@nombreEmpresa, 1, @dbFileName, @fecha, @rnc, @telefono, @direccion) RETURNING Id", 
                    new { nombreEmpresa, dbFileName, fecha, rnc, telefono, direccion }, tran);
            }
            else
            {
                masterConn.Execute("INSERT INTO Empresas (Nombre, Activa, DbFileName, FechaRegistro, Rnc, Telefono, Direccion) VALUES (@nombreEmpresa, 1, @dbFileName, @fecha, @rnc, @telefono, @direccion)", new { nombreEmpresa, dbFileName, fecha, rnc, telefono, direccion }, tran);
                empresaId = masterConn.ExecuteScalar<int>("SELECT last_insert_rowid()", null, tran);
            }
            
            masterConn.Execute("INSERT INTO UsuariosGlobales (NombreUsuario, DbFileName, EmpresaId) VALUES (@adminUser, @dbFileName, @empresaId)", new { adminUser, dbFileName, empresaId }, tran);
            tran.Commit();
        }
        catch (Exception ex)
        {
            tran.Rollback();
            TempData["Error"] = $"Hubo un error registrando la empresa en la base maestra: {ex.Message}";
            return RedirectToAction(nameof(Index));
        }
        
        // Initialize new DB schema (creates file and runs all CREATE TABLEs)
        Db.InitializeDatabaseSchema(dbFileName);
        
        // Insert admin user into the newly created tenant DB
        using var tenantConn = Db.CreateConnection(dbFileName);
        var hash = Db.HashPassword(adminPass);
        tenantConn.Execute("INSERT INTO Usuarios (NombreUsuario, PasswordHash, NombreCompleto, Rol, Activo, FechaCreacion, Permisos, DebeCambiarPassword) VALUES (@adminUser, @hash, 'Administrador Principal', 'Admin', 1, @fecha, 'TODO', 1)", new { adminUser, hash, fecha });
        
        // Inject business details into tenant configuration
        tenantConn.Execute("UPDATE Configuracion SET Valor = @nombreEmpresa WHERE Clave = 'NOMBRE_NEGOCIO'", new { nombreEmpresa });
        tenantConn.Execute("UPDATE Configuracion SET Valor = @rnc WHERE Clave = 'RNC'", new { rnc });
        tenantConn.Execute("UPDATE Configuracion SET Valor = @telefono WHERE Clave = 'TELEFONO'", new { telefono });
        tenantConn.Execute("UPDATE Configuracion SET Valor = @direccion WHERE Clave = 'DIRECCION'", new { direccion });
        
        TempData["Success"] = $"Empresa '{nombreEmpresa}' creada exitosamente. Administrador: {adminUser}";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult AlternarEstado(int id)
    {
        using var masterConn = MasterDb.CreateConnection();
        masterConn.Open();
        var estadoActual = masterConn.ExecuteScalar<int>("SELECT Activa FROM Empresas WHERE Id = @id", new { id });
        var nuevoEstado = estadoActual == 1 ? 0 : 1;
        masterConn.Execute("UPDATE Empresas SET Activa = @nuevoEstado WHERE Id = @id", new { nuevoEstado, id });
        
        TempData["Success"] = nuevoEstado == 1 ? "La empresa ha sido habilitada." : "La empresa ha sido deshabilitada.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EditarEmpresa(int id, string nuevoNombre, string oldAdminUser, string newAdminUser, string newPassword, string rnc, string telefono, string direccion)
    {
        if (string.IsNullOrWhiteSpace(nuevoNombre) || string.IsNullOrWhiteSpace(newAdminUser))
        {
            TempData["Error"] = "El nombre de la empresa y el nombre de usuario son obligatorios.";
            return RedirectToAction(nameof(Index));
        }

        nuevoNombre = nuevoNombre.Trim();
        newAdminUser = newAdminUser.Trim();
        oldAdminUser = oldAdminUser?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(newPassword)) newPassword = newPassword.Trim();
        rnc = rnc?.Trim() ?? "";
        telefono = telefono?.Trim() ?? "";
        direccion = direccion?.Trim() ?? "";

        using var masterConn = MasterDb.CreateConnection();
        masterConn.Open();

        // Si cambió el usuario, verificar que el nuevo no esté en uso
        if (!string.Equals(oldAdminUser, newAdminUser, StringComparison.OrdinalIgnoreCase))
        {
            var userExists = masterConn.ExecuteScalar<int>("SELECT COUNT(*) FROM UsuariosGlobales WHERE LOWER(NombreUsuario) = LOWER(@newAdminUser)", new { newAdminUser });
            if (userExists > 0)
            {
                TempData["Error"] = $"El usuario '{newAdminUser}' ya está en uso.";
                return RedirectToAction(nameof(Index));
            }
        }

        var dbFileName = masterConn.ExecuteScalar<string>("SELECT DbFileName FROM Empresas WHERE Id = @id", new { id });
        if (string.IsNullOrEmpty(dbFileName))
        {
            TempData["Error"] = "Empresa no encontrada.";
            return RedirectToAction(nameof(Index));
        }

        using var tran = masterConn.BeginTransaction();
        try
        {
            masterConn.Execute("UPDATE Empresas SET Nombre = @nuevoNombre, Rnc = @rnc, Telefono = @telefono, Direccion = @direccion WHERE Id = @id", new { nuevoNombre, rnc, telefono, direccion, id }, tran);
            
            if (!string.Equals(oldAdminUser, newAdminUser, StringComparison.OrdinalIgnoreCase))
            {
                masterConn.Execute("UPDATE UsuariosGlobales SET NombreUsuario = @newAdminUser WHERE EmpresaId = @id AND NombreUsuario = @oldAdminUser", new { newAdminUser, id, oldAdminUser }, tran);
            }
            tran.Commit();
        }
        catch (Exception ex)
        {
            tran.Rollback();
            TempData["Error"] = $"Hubo un error actualizando los datos de la empresa en la base maestra: {ex.Message}";
            return RedirectToAction(nameof(Index));
        }

        // Sincronización cruzada: Actualizar en la BD del Inquilino
        try
        {
            var tenantDbPath = Path.Combine(Db.StorageRootPath, dbFileName);
            var supabaseActive = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUPABASE_CONNECTION_STRING"));
            if (supabaseActive || System.IO.File.Exists(tenantDbPath))
            {
                using var tenantConn = Db.CreateConnection(dbFileName);
                
                if (!string.Equals(oldAdminUser, newAdminUser, StringComparison.OrdinalIgnoreCase))
                {
                    tenantConn.Execute("UPDATE Usuarios SET NombreUsuario = @newAdminUser WHERE NombreUsuario = @oldAdminUser", new { newAdminUser, oldAdminUser });
                }

                if (!string.IsNullOrWhiteSpace(newPassword))
                {
                    var hash = Db.HashPassword(newPassword);
                    tenantConn.Execute("UPDATE Usuarios SET PasswordHash = @hash, DebeCambiarPassword = 1 WHERE NombreUsuario = @newAdminUser", new { hash, newAdminUser });
                }

                // Actualizar configuración del negocio
                tenantConn.Execute("UPDATE Configuracion SET Valor = @nuevoNombre WHERE Clave = 'NOMBRE_NEGOCIO'", new { nuevoNombre });
                tenantConn.Execute("UPDATE Configuracion SET Valor = @rnc WHERE Clave = 'RNC'", new { rnc });
                tenantConn.Execute("UPDATE Configuracion SET Valor = @telefono WHERE Clave = 'TELEFONO'", new { telefono });
                tenantConn.Execute("UPDATE Configuracion SET Valor = @direccion WHERE Clave = 'DIRECCION'", new { direccion });
            }
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Se actualizó la empresa, pero falló la sincronización con el inquilino: {ex.Message}";
            return RedirectToAction(nameof(Index));
        }
        
        TempData["Success"] = "La empresa ha sido actualizada exitosamente.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EliminarEmpresa(int id)
    {
        using var masterConn = MasterDb.CreateConnection();
        masterConn.Open();
        
        var empresa = masterConn.QueryFirstOrDefault<EmpresaViewModel>("SELECT DbFileName, Nombre FROM Empresas WHERE Id = @id", new { id });
        if (empresa == null || string.IsNullOrWhiteSpace(empresa.DbFileName))
        {
            TempData["Error"] = "Empresa no encontrada.";
            return RedirectToAction(nameof(Index));
        }

        var dbFileName = empresa.DbFileName;
        var nombreEmpresa = empresa.Nombre;

        using var tran = masterConn.BeginTransaction();
        try
        {
            masterConn.Execute("DELETE FROM UsuariosGlobales WHERE EmpresaId = @id", new { id }, tran);
            masterConn.Execute("DELETE FROM Empresas WHERE Id = @id", new { id }, tran);
            if (masterConn is Npgsql.NpgsqlConnection)
            {
                var schemaName = dbFileName.Replace(".db", "").Replace("-", "_").ToLower();
                masterConn.Execute($"DROP SCHEMA IF EXISTS {schemaName} CASCADE;", null, tran);
            }
            tran.Commit();
        }
        catch (Exception ex)
        {
            tran.Rollback();
            TempData["Error"] = $"No se pudo eliminar la empresa: {ex.Message}";
            return RedirectToAction(nameof(Index));
        }

        // Delete the database file from disk if local SQLite
        var dbPath = Path.Combine(Db.StorageRootPath, dbFileName);
        if (System.IO.File.Exists(dbPath))
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                System.IO.File.Delete(dbPath);
            }
            catch
            {
                // File might be locked, but record is deleted
            }
        }

        TempData["Success"] = $"La empresa '{nombreEmpresa}' y todos sus datos han sido eliminados de forma permanente.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult DebugTenant(string dbName)
    {
        var tenantDbPath = Path.Combine(Db.StorageRootPath, dbName);
        var supabaseActive = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUPABASE_CONNECTION_STRING"));
        if (!supabaseActive && !System.IO.File.Exists(tenantDbPath)) return Content("Not found");
        
        using var tenantConn = Db.CreateConnection(dbName);
        var users = tenantConn.Query("SELECT * FROM Usuarios").ToList();
        return Json(users);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult MigrarASupabase(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            TempData["Error"] = "La cadena de conexión de Supabase es requerida.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            DatabaseMigrator.MigrateToSupabase(connectionString.Trim());
            TempData["Success"] = "¡La base de datos y todas las empresas locales se han migrado exitosamente a Supabase!";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Error durante la migración: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }
}


public class EmpresaViewModel
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string DbFileName { get; set; } = string.Empty;
    public string FechaRegistro { get; set; } = string.Empty;
    public int Activa { get; set; }
    public string AdminUser { get; set; } = string.Empty;
    public string Rnc { get; set; } = string.Empty;
    public string Telefono { get; set; } = string.Empty;
    public string Direccion { get; set; } = string.Empty;
}

public class ModuloViewModel
{
    public int Id { get; set; }
    public string Codigo { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
}
