using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using InventarioProVisual.Data;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

[Authorize]
public class CashRegisterController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public CashRegisterController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var openCash = await conn.QueryFirstOrDefaultAsync<Caja>("SELECT * FROM Caja WHERE Estado = 'ABIERTA' ORDER BY Id DESC LIMIT 1");
        
        if (openCash != null)
        {
            var ventas = await conn.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(Total), 0) FROM Facturas WHERE CajaId = @id AND MetodoPago != 'CREDITO'", new { id = openCash.Id }) ?? 0m;
            var ingresosExtra = await conn.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(Monto), 0) FROM PagosCuentas WHERE Fecha >= @Apertura", new { openCash.Apertura }) ?? 0m;

            ViewBag.VentasEfectivo = ventas;
            ViewBag.IngresosExtra = ingresosExtra;
            ViewBag.TotalEsperado = openCash.SaldoInicial + ventas + ingresosExtra;
        }

        return View(openCash);
    }

    [HttpGet]
    public async Task<IActionResult> History()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var history = await conn.QueryAsync<Caja>("SELECT * FROM Caja WHERE Estado = 'CERRADA' ORDER BY Id DESC LIMIT 50");
        return View(history.ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenCash(decimal initialAmount)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var current = await conn.QueryFirstOrDefaultAsync<Caja>("SELECT * FROM Caja WHERE Estado = 'ABIERTA' ORDER BY Id DESC LIMIT 1");
        if (current is not null)
        {
            FlashError("Ya existe una caja abierta.");
            return RedirectToAction(nameof(Index));
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO Caja (UsuarioId, Apertura, SaldoInicial, SaldoFinal, Estado)
            VALUES (@UsuarioId, @Fecha, @Monto, @Monto, 'ABIERTA')
            """,
            new { UsuarioId = CurrentUserId, Fecha = DateTime.Now.ToString(Db.DateTimeFormat), Monto = initialAmount });

        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Cajero", "Caja", "Apertura", $"Monto inicial RD${initialAmount:N2}");
        FlashSuccess("Caja abierta correctamente.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseCash(decimal physicalAmount)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var current = await conn.QueryFirstOrDefaultAsync<Caja>("SELECT * FROM Caja WHERE Estado = 'ABIERTA' ORDER BY Id DESC LIMIT 1");
        if (current is null)
        {
            FlashError("No hay una caja abierta.");
            return RedirectToAction(nameof(Index));
        }

        var ventas = await conn.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(Total), 0) FROM Facturas WHERE CajaId = @id AND MetodoPago != 'CREDITO'", new { id = current.Id }) ?? 0m;
        var ingresosExtra = await conn.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(Monto), 0) FROM PagosCuentas WHERE Fecha >= @Apertura", new { current.Apertura }) ?? 0m;
        
        var totalEsperado = current.SaldoInicial + ventas + ingresosExtra;
        
        await conn.ExecuteAsync(
            "UPDATE Caja SET Estado = 'CERRADA', Cierre = @Cierre, SaldoFinal = @SaldoFinal WHERE Id = @Id",
            new { Cierre = DateTime.Now.ToString(Db.DateTimeFormat), SaldoFinal = physicalAmount, Id = current.Id });

        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Cajero", "Caja", "Cierre", $"Caja #{current.Id}, esperado RD${totalEsperado:N2}, físico RD${physicalAmount:N2}");
        FlashSuccess($"Caja cerrada. Diferencia: RD${physicalAmount - totalEsperado:N2}");
        return RedirectToAction(nameof(Index));
    }
}
