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
public sealed class ReceivablesController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public ReceivablesController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var data = await conn.QueryAsync<ReceivableViewModel>(
            """
            SELECT c.Id AS ClienteId, c.Nombre AS ClienteNombre, c.Telefono AS ClienteTelefono, cxp.DeudaTotal, cxp.UltimaActualizacion
            FROM CuentasPorCobrar cxp
            JOIN Clientes c ON c.Id = cxp.ClienteId
            WHERE cxp.DeudaTotal > 0
            ORDER BY cxp.DeudaTotal DESC
            """);

        return View(data.ToList());
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var cliente = await conn.QueryFirstOrDefaultAsync<Cliente>("SELECT * FROM Clientes WHERE Id = @id", new { id });
        if (cliente is null)
        {
            FlashError("Cliente no encontrado.");
            return RedirectToAction(nameof(Index));
        }

        var deuda = await conn.QueryFirstOrDefaultAsync<ReceivableViewModel>(
            """
            SELECT c.Id AS ClienteId, c.Nombre AS ClienteNombre, c.Telefono AS ClienteTelefono, cxp.DeudaTotal, cxp.UltimaActualizacion
            FROM CuentasPorCobrar cxp
            JOIN Clientes c ON c.Id = cxp.ClienteId
            WHERE c.Id = @id
            """, new { id });

        if (deuda is null)
        {
            deuda = new ReceivableViewModel { ClienteId = cliente.Id, ClienteNombre = cliente.Nombre, ClienteTelefono = cliente.Telefono ?? string.Empty, DeudaTotal = 0 };
        }

        var facturasPendientes = await conn.QueryAsync(
            "SELECT Id, Fecha, Total, MetodoPago, Estado FROM Facturas WHERE ClienteId = @id AND MetodoPago = 'CREDITO' ORDER BY Id DESC",
            new { id });

        var pagos = await conn.QueryAsync(
            "SELECT Id, Monto, MetodoPago, Referencia, Fecha FROM PagosCuentas WHERE ClienteId = @id ORDER BY Id DESC LIMIT 30",
            new { id });

        ViewBag.Facturas = facturasPendientes.ToList();
        ViewBag.Pagos = pagos.ToList();
        return View(deuda);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(long clienteId, decimal amount, string method, string reference)
    {
        if (amount <= 0)
        {
            FlashError("El monto debe ser mayor a cero.");
            return RedirectToAction(nameof(Details), new { id = clienteId });
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();
        try
        {
            var deudaActual = await conn.ExecuteScalarAsync<decimal>("SELECT DeudaTotal FROM CuentasPorCobrar WHERE ClienteId = @id", new { id = clienteId }, tran);
            
            if (deudaActual < amount)
            {
                amount = deudaActual; // No permitir abonar más de la deuda
            }

            var timestamp = DateTime.Now.ToString(Db.DateTimeFormat);

            // Registrar el pago
            await conn.ExecuteAsync(
                "INSERT INTO PagosCuentas (ClienteId, Monto, MetodoPago, Referencia, Fecha, UsuarioId) VALUES (@clienteId, @amount, @method, @reference, @fecha, @usuarioId)",
                new { clienteId, amount, method, reference = reference ?? "", fecha = timestamp, usuarioId = CurrentUserId }, tran);

            // Reducir la deuda
            await conn.ExecuteAsync(
                "UPDATE CuentasPorCobrar SET DeudaTotal = DeudaTotal - @amount, UltimaActualizacion = @fecha WHERE ClienteId = @id",
                new { amount, fecha = timestamp, id = clienteId }, tran);

            // Intentar marcar facturas como pagadas
            var facturasPendientes = await conn.QueryAsync<FacturaPendiente>(
                "SELECT Id, Total FROM Facturas WHERE ClienteId = @id AND Estado = 'Pendiente' ORDER BY Id ASC",
                new { id = clienteId }, tran);

            var montoRestante = amount;
            foreach (var fac in facturasPendientes)
            {
                if (montoRestante >= fac.Total)
                {
                    await conn.ExecuteAsync("UPDATE Facturas SET Estado = 'Pagada' WHERE Id = @id", new { id = fac.Id }, tran);
                    montoRestante -= fac.Total;
                }
                else
                {
                    break;
                }
            }

            // Afectar la caja actual
            var caja = await conn.QueryFirstOrDefaultAsync<Caja>("SELECT * FROM Caja WHERE Estado = 'ABIERTA' ORDER BY Id DESC LIMIT 1", null, tran);
            if (caja != null)
            {
                await conn.ExecuteAsync("UPDATE Caja SET SaldoFinal = SaldoFinal + @amount WHERE Id = @id", new { amount, id = caja.Id }, tran);
            }

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Cuentas Por Cobrar", "Abono", $"Cliente {clienteId} abonó RD${amount:N2} via {method}", tran);
            await tran.CommitAsync();
            FlashSuccess($"Abono de RD${amount:N2} registrado exitosamente.");
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            FlashError("Error al procesar el pago: " + ex.Message);
        }

        return RedirectToAction(nameof(Details), new { id = clienteId });
    }
}

public class FacturaPendiente
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}
