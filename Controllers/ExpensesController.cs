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
public sealed class ExpensesController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public ExpensesController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var gastos = await conn.QueryAsync<Gasto>("SELECT * FROM Gastos ORDER BY Fecha DESC, Id DESC");
        return View(new ExpenseIndexViewModel
        {
            Items = gastos.ToList()
        });
    }

    [HttpGet]
    public IActionResult Create() => View("Editor", new ExpenseEditorViewModel());

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var gasto = await conn.QueryFirstOrDefaultAsync<Gasto>("SELECT * FROM Gastos WHERE Id = @id", new { id });
        if (gasto is null)
        {
            FlashError("Gasto no encontrado.");
            return RedirectToAction(nameof(Index));
        }

        return View("Editor", new ExpenseEditorViewModel
        {
            Id = gasto.Id,
            Concepto = gasto.Concepto,
            Categoria = gasto.Categoria,
            Monto = gasto.Monto,
            Fecha = gasto.Fecha
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ExpenseEditorViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View("Editor", model);
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var fecha = model.Fecha.ToString(Db.DateTimeFormat);
        if (model.Id is null)
        {
            await conn.ExecuteAsync(
                "INSERT INTO Gastos (Concepto, Monto, Fecha, Categoria, UsuarioId) VALUES (@Concepto, @Monto, @Fecha, @Categoria, @UsuarioId)",
                new { model.Concepto, model.Monto, Fecha = fecha, model.Categoria, UsuarioId = CurrentUserId });
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Gastos", "Crear", model.Concepto);
            FlashSuccess("Gasto registrado.");
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE Gastos SET Concepto = @Concepto, Monto = @Monto, Fecha = @Fecha, Categoria = @Categoria WHERE Id = @Id",
                new { model.Concepto, model.Monto, Fecha = fecha, model.Categoria, Id = model.Id.Value });
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Gastos", "Editar", model.Concepto);
            FlashSuccess("Gasto actualizado.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var nombre = await conn.ExecuteScalarAsync<string>("SELECT Concepto FROM Gastos WHERE Id = @id", new { id }) ?? $"Gasto #{id}";
        await conn.ExecuteAsync("DELETE FROM Gastos WHERE Id = @id", new { id });
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Gastos", "Eliminar", nombre);
        FlashSuccess("Gasto eliminado.");
        return RedirectToAction(nameof(Index));
    }
}
