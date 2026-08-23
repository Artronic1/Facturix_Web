using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data.Common;

namespace FacturixWeb.Controllers;

[Authorize(Roles = "Admin")]
public sealed class UsersController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;
    private readonly ITenantProvider _tenantProvider;

    public UsersController(IDbConnectionFactory dbConnectionFactory, ITenantProvider tenantProvider)
    {
        _dbConnectionFactory = dbConnectionFactory;
        _tenantProvider = tenantProvider;
    }

    private static readonly List<string> AvailablePermissions =
    [
        "Dashboard",
        "Facturación",
        "Cotizaciones",
        "Productos",
        "Clientes",
        "Gastos",
        "Nómina",
        "Reportes",
        "Usuarios",
        "Auditoría",
        "Configuración"
    ];

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var users = await conn.QueryAsync<Usuario>("SELECT Id, NombreUsuario, NombreCompleto, Rol, Activo, FechaCreacion, UltimoAcceso, Permisos, PasswordHash FROM Usuarios ORDER BY NombreUsuario");
        
        return View(new UserIndexViewModel
        {
            Items = users.ToList()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        return View("Editor", await BuildEditorModelAsync(null));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        return View("Editor", await BuildEditorModelAsync(id));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserEditorViewModel model)
    {
        if (model.Id is null && string.IsNullOrWhiteSpace(model.Password))
        {
            ModelState.AddModelError(nameof(model.Password), "La contraseña es obligatoria para usuarios nuevos.");
        }

        if (!ModelState.IsValid)
        {
            model.PermisosDisponibles = AvailablePermissions;
            return View("Editor", model);
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        
        // --- Multi-tenant Global Verification & Registration ---
        using var masterConn = MasterDb.CreateConnection();
        await masterConn.OpenAsync();
        var currentTenantDbName = _tenantProvider.GetCurrentTenantDbName();
        var empresaId = await masterConn.ExecuteScalarAsync<int>("SELECT Id FROM Empresas WHERE DbFileName = @dbFileName", new { dbFileName = currentTenantDbName });
        
        var existingGlobalOwner = await masterConn.QueryFirstOrDefaultAsync<string>("SELECT DbFileName FROM UsuariosGlobales WHERE NombreUsuario = @username", new { username = model.NombreUsuario });
        
        if (model.Id is null)
        {
            if (existingGlobalOwner != null)
            {
                ModelState.AddModelError(nameof(model.NombreUsuario), "Este nombre de usuario ya está registrado en el sistema. Debes elegir otro (ej. juan_empresa).");
                model.PermisosDisponibles = AvailablePermissions;
                return View("Editor", model);
            }
            
            // Register in global first
            await masterConn.ExecuteAsync("INSERT INTO UsuariosGlobales (NombreUsuario, DbFileName, EmpresaId) VALUES (@user, @db, @empId)", new { user = model.NombreUsuario, db = currentTenantDbName, empId = empresaId });

            await conn.ExecuteAsync(
                """
                INSERT INTO Usuarios (NombreUsuario, PasswordHash, NombreCompleto, Rol, Activo, FechaCreacion, Permisos)
                VALUES (@NombreUsuario, @PasswordHash, @NombreCompleto, @Rol, @Activo, @FechaCreacion, @Permisos)
                """,
                new
                {
                    model.NombreUsuario,
                    PasswordHash = Db.HashPassword(model.Password),
                    model.NombreCompleto,
                    model.Rol,
                    Activo = model.Activo ? 1 : 0,
                    FechaCreacion = DateTime.Now.ToString(Db.DateTimeFormat),
                    Permisos = string.Join(",", model.PermisosSeleccionados)
                });

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Usuarios", "Crear", model.NombreUsuario);
            FlashSuccess("Usuario creado.");
        }
        else
        {
            // If editing, check if username changed and is taken
            var oldUsername = await conn.ExecuteScalarAsync<string>("SELECT NombreUsuario FROM Usuarios WHERE Id = @Id", new { model.Id });
            
            if (oldUsername != model.NombreUsuario)
            {
                if (existingGlobalOwner != null)
                {
                    ModelState.AddModelError(nameof(model.NombreUsuario), "Este nombre de usuario ya está registrado en el sistema. Debes elegir otro.");
                    model.PermisosDisponibles = AvailablePermissions;
                    return View("Editor", model);
                }
                
                // Update in global
                await masterConn.ExecuteAsync("UPDATE UsuariosGlobales SET NombreUsuario = @new WHERE NombreUsuario = @old", new { @new = model.NombreUsuario, old = oldUsername });
            }

            if (string.IsNullOrWhiteSpace(model.Password))
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE Usuarios
                    SET NombreUsuario = @NombreUsuario,
                        NombreCompleto = @NombreCompleto,
                        Rol = @Rol,
                        Activo = @Activo,
                        Permisos = @Permisos
                    WHERE Id = @Id
                    """,
                    new
                    {
                        Id = model.Id.Value,
                        model.NombreUsuario,
                        model.NombreCompleto,
                        model.Rol,
                        Activo = model.Activo ? 1 : 0,
                        Permisos = string.Join(",", model.PermisosSeleccionados)
                    });
            }
            else
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE Usuarios
                    SET NombreUsuario = @NombreUsuario,
                        NombreCompleto = @NombreCompleto,
                        Rol = @Rol,
                        Activo = @Activo,
                        Permisos = @Permisos,
                        PasswordHash = @PasswordHash
                    WHERE Id = @Id
                    """,
                    new
                    {
                        Id = model.Id.Value,
                        model.NombreUsuario,
                        model.NombreCompleto,
                        model.Rol,
                        Activo = model.Activo ? 1 : 0,
                        Permisos = string.Join(",", model.PermisosSeleccionados),
                        PasswordHash = Db.HashPassword(model.Password)
                    });
            }

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Usuarios", "Editar", model.NombreUsuario);
            FlashSuccess("Usuario actualizado.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (id == CurrentUserId)
        {
            FlashError("No puedes eliminar tu propio usuario.");
            return RedirectToAction(nameof(Index));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var userName = await conn.ExecuteScalarAsync<string>("SELECT NombreUsuario FROM Usuarios WHERE Id = @id", new { id }) ?? $"Usuario #{id}";
        await conn.ExecuteAsync("DELETE FROM Usuarios WHERE Id = @id", new { id });
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Usuarios", "Eliminar", userName);
        FlashSuccess("Usuario eliminado.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<UserEditorViewModel> BuildEditorModelAsync(int? id)
    {
        var model = new UserEditorViewModel
        {
            PermisosDisponibles = AvailablePermissions
        };

        if (!id.HasValue)
        {
            return model;
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var user = await conn.QueryFirstOrDefaultAsync<Usuario>("SELECT * FROM Usuarios WHERE Id = @id", new { id });
        if (user is null)
        {
            return model;
        }

        model.Id = user.Id;
        model.NombreUsuario = user.NombreUsuario;
        model.NombreCompleto = user.NombreCompleto;
        model.Rol = user.Rol;
        model.Activo = user.Activo;
        model.PermisosSeleccionados = (user.Permisos ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return model;
    }
}
