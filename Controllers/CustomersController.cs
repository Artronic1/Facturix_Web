using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

[Authorize(Roles = "Admin")]
public sealed class CustomersController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public CustomersController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string search = "")
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var sql = "SELECT * FROM Clientes";
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " WHERE Nombre LIKE @term OR Telefono LIKE @term OR Rnc LIKE @term";
        }

        sql += " ORDER BY Nombre";

        var clientes = await conn.QueryAsync<Cliente>(sql, new { term = $"%{search}%" });

        return View(new CustomerIndexViewModel
        {
            Search = search,
            Items = clientes.ToList()
        });
    }

    [HttpGet]
    public IActionResult Create() => View("Editor", new CustomerEditorViewModel());

    [HttpGet]
    public async Task<IActionResult> Edit(long id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var cliente = await conn.QueryFirstOrDefaultAsync<Cliente>("SELECT * FROM Clientes WHERE Id = @id", new { id });
        if (cliente is null)
        {
            FlashError("Cliente no encontrado.");
            return RedirectToAction(nameof(Index));
        }

        return View("Editor", new CustomerEditorViewModel
        {
            Id = cliente.Id,
            Nombre = cliente.Nombre,
            Telefono = cliente.Telefono,
            Direccion = cliente.Direccion,
            Rnc = cliente.Rnc
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CustomerEditorViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View("Editor", model);
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        if (model.Id is null)
        {
            await conn.ExecuteAsync(
                "INSERT INTO Clientes (Nombre, Telefono, Direccion, Rnc) VALUES (@Nombre, @Telefono, @Direccion, @Rnc)",
                model);
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Clientes", "Crear", model.Nombre);
            FlashSuccess("Cliente creado.");
        }
        else
        {
            if (model.Id == 1)
            {
                FlashError("No se puede editar el cliente por defecto.");
                return RedirectToAction(nameof(Index));
            }

            await conn.ExecuteAsync(
                "UPDATE Clientes SET Nombre = @Nombre, Telefono = @Telefono, Direccion = @Direccion, Rnc = @Rnc WHERE Id = @Id",
                model);
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Clientes", "Editar", model.Nombre);
            FlashSuccess("Cliente actualizado.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id)
    {
        if (id == 1)
        {
            FlashError("No se puede eliminar el cliente por defecto.");
            return RedirectToAction(nameof(Index));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var hasSales = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Ventas WHERE ClienteId = @id", new { id }) > 0;
        if (hasSales)
        {
            FlashError("No se puede eliminar el cliente porque tiene ventas asociadas.");
            return RedirectToAction(nameof(Index));
        }

        var customerName = await conn.ExecuteScalarAsync<string>("SELECT Nombre FROM Clientes WHERE Id = @id", new { id }) ?? $"Cliente #{id}";
        await conn.ExecuteAsync("DELETE FROM Clientes WHERE Id = @id", new { id });
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Clientes", "Eliminar", customerName);
        FlashSuccess("Cliente eliminado.");
        return RedirectToAction(nameof(Index));
    }
}
