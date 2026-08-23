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
public sealed class EmployeesController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public EmployeesController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var empleados = await conn.QueryAsync<Empleado>("SELECT * FROM Empleados ORDER BY Nombre");
        return View(new EmployeeIndexViewModel
        {
            Items = empleados.ToList()
        });
    }

    [HttpGet]
    public IActionResult Create() => View("Editor", new EmployeeEditorViewModel());

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var empleado = await conn.QueryFirstOrDefaultAsync<Empleado>("SELECT * FROM Empleados WHERE Id = @id", new { id });
        if (empleado is null)
        {
            FlashError("Empleado no encontrado.");
            return RedirectToAction(nameof(Index));
        }

        return View("Editor", new EmployeeEditorViewModel
        {
            Id = empleado.Id,
            Nombre = empleado.Nombre,
            Cargo = empleado.Cargo,
            Salario = empleado.Salario,
            FechaIngreso = empleado.FechaIngreso,
            Activo = empleado.Activo
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(EmployeeEditorViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View("Editor", model);
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var fechaIngreso = model.FechaIngreso.ToString(Db.DateTimeFormat);
        if (model.Id is null)
        {
            await conn.ExecuteAsync(
                "INSERT INTO Empleados (Nombre, Cargo, Salario, FechaIngreso, Activo) VALUES (@Nombre, @Cargo, @Salario, @FechaIngreso, @Activo)",
                new { model.Nombre, model.Cargo, model.Salario, FechaIngreso = fechaIngreso, Activo = model.Activo ? 1 : 0 });
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Nómina", "Crear empleado", model.Nombre);
            FlashSuccess("Empleado creado.");
        }
        else
        {
            await conn.ExecuteAsync(
                """
                UPDATE Empleados
                SET Nombre = @Nombre,
                    Cargo = @Cargo,
                    Salario = @Salario,
                    FechaIngreso = @FechaIngreso,
                    Activo = @Activo
                WHERE Id = @Id
                """,
                new { model.Nombre, model.Cargo, model.Salario, FechaIngreso = fechaIngreso, Activo = model.Activo ? 1 : 0, Id = model.Id.Value });
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Nómina", "Editar empleado", model.Nombre);
            FlashSuccess("Empleado actualizado.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var nombre = await conn.ExecuteScalarAsync<string>("SELECT Nombre FROM Empleados WHERE Id = @id", new { id }) ?? $"Empleado #{id}";
        await conn.ExecuteAsync("UPDATE Empleados SET Activo = 0 WHERE Id = @id", new { id });
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Nómina", "Desactivar empleado", nombre);
        FlashSuccess("Empleado marcado como inactivo.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(PayrollInputViewModel model)
    {
        if (!ModelState.IsValid)
        {
            FlashError("Complete correctamente el pago de nómina.");
            return RedirectToAction(nameof(Index));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();

        try 
        {
            var empleado = await conn.QueryFirstOrDefaultAsync<Empleado>("SELECT * FROM Empleados WHERE Id = @id", new { id = model.EmpleadoId }, tran);
            if (empleado is null)
            {
                FlashError("Empleado no encontrado.");
                return RedirectToAction(nameof(Index));
            }

            await conn.ExecuteAsync(
                "INSERT INTO PagosNomina (EmpleadoId, Monto, FechaPago, Periodo) VALUES (@EmpleadoId, @Monto, @FechaPago, @Periodo)",
                new { model.EmpleadoId, model.Monto, FechaPago = DateTime.Now.ToString(Db.DateTimeFormat), model.Periodo }, tran);

            await conn.ExecuteAsync(
                "INSERT INTO Gastos (Concepto, Monto, Fecha, Categoria, UsuarioId) VALUES (@Concepto, @Monto, @Fecha, 'SUELDOS', @UsuarioId)",
                new
                {
                    Concepto = $"Pago Nómina: {empleado.Nombre} ({model.Periodo})",
                    model.Monto,
                    Fecha = DateTime.Now.ToString(Db.DateTimeFormat),
                    UsuarioId = CurrentUserId
                },
                tran);

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Nómina", "Pago individual", $"{empleado.Nombre}: RD${model.Monto:N2}", tran);
            await tran.CommitAsync();
            FlashSuccess("Pago de nómina registrado.");
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            FlashError("Error: " + ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PayAll(string? periodo = null)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var empleados = await conn.QueryAsync<Empleado>("SELECT * FROM Empleados WHERE Activo = 1 ORDER BY Nombre");
        var empList = empleados.ToList();

        if (empList.Count == 0)
        {
            FlashError("No hay empleados activos para pagar.");
            return RedirectToAction(nameof(Index));
        }

        var periodoAplicado = string.IsNullOrWhiteSpace(periodo) ? DateTime.Now.ToString("MMMM yyyy") : periodo.Trim();
        var totalNomina = empList.Sum(x => x.Salario);

        using var tran = await conn.BeginTransactionAsync();
        try
        {
            foreach (var empleado in empList)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO PagosNomina (EmpleadoId, Monto, FechaPago, Periodo) VALUES (@EmpleadoId, @Monto, @FechaPago, @Periodo)",
                    new { EmpleadoId = empleado.Id, Monto = empleado.Salario, FechaPago = DateTime.Now.ToString(Db.DateTimeFormat), Periodo = periodoAplicado }, tran);
            }

            await conn.ExecuteAsync(
                "INSERT INTO Gastos (Concepto, Monto, Fecha, Categoria, UsuarioId) VALUES (@Concepto, @Monto, @Fecha, 'SUELDOS', @UsuarioId)",
                new
                {
                    Concepto = $"Nómina Completa ({empList.Count} emp.): {periodoAplicado}",
                    Monto = totalNomina,
                    Fecha = DateTime.Now.ToString(Db.DateTimeFormat),
                    UsuarioId = CurrentUserId
                },
                tran);

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Nómina", "Pago completo", $"Empleados: {empList.Count}, Total: RD${totalNomina:N2}", tran);
            await tran.CommitAsync();

            FlashSuccess("Nómina completa registrada.");
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            FlashError("Error: " + ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }
}
