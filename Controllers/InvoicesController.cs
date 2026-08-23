using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using InventarioProVisual.Data;
using InventarioProVisual.Helpers;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

[Authorize]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class InvoicesController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public InvoicesController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string start = "", string end = "")
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        
        var query = """
            SELECT f.*, COALESCE(c.Nombre, 'Consumidor Final') AS ClienteNombre, u.NombreUsuario AS CajeroNombre
            FROM Facturas f
            LEFT JOIN Clientes c ON f.ClienteId = c.Id
            LEFT JOIN Usuarios u ON f.UsuarioId = u.Id
            WHERE 1=1
            """;
            
        if (!string.IsNullOrWhiteSpace(start) && DateTime.TryParse(start, out var startDate))
        {
            query += " AND date(f.Fecha) >= date(@start)";
        }
        if (!string.IsNullOrWhiteSpace(end) && DateTime.TryParse(end, out var endDate))
        {
            query += " AND date(f.Fecha) <= date(@end)";
        }
        
        query += " ORDER BY f.Id DESC LIMIT 100";
        
        var facturas = await conn.QueryAsync(query, new { start, end });
        
        ViewBag.Start = start;
        ViewBag.End = end;
        
        return View(facturas.ToList());
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var factura = await conn.QueryFirstOrDefaultAsync(
            """
            SELECT f.*, COALESCE(c.Nombre, 'Consumidor Final') AS ClienteNombre
            FROM Facturas f
            LEFT JOIN Clientes c ON f.ClienteId = c.Id
            WHERE f.Id = @id
            """, new { id });
            
        if (factura == null)
        {
            FlashError("Factura no encontrada.");
            return RedirectToAction(nameof(Index));
        }

        var detalles = await conn.QueryAsync(
            """
            SELECT v.*, p.Nombre AS ProductoNombre
            FROM Ventas v
            LEFT JOIN Productos p ON v.ProductoId = p.Id
            WHERE v.FacturaId = @id
            """, new { id });

        ViewBag.Detalles = detalles.ToList();
        return View(factura);
    }

    [HttpGet]
    public async Task<IActionResult> Receipt(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var factura = await conn.QueryFirstOrDefaultAsync("SELECT * FROM Facturas WHERE Id = @id", new { id });

        if (factura is null)
        {
            FlashError("Factura no encontrada.");
            return RedirectToAction(nameof(Index));
        }

        var items = await conn.QueryAsync<ReciboItem>(
            """
            SELECT v.Cantidad, p.Nombre AS NombreProducto, v.Total
            FROM Ventas v
            JOIN Productos p ON p.Id = v.ProductoId
            WHERE v.FacturaId = @id
            """, new { id });

        var subtotal = (decimal)factura.Total;
        var ncf = factura.Ncf as string ?? "";
        
        var usarBranding = await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'USAR_BRANDING'") == "1";
        var mensajePie = usarBranding ? (await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'MENSAJE_RECIBO'") ?? "¡Gracias por su compra!") : "¡Gracias por su compra!";
        var nombreNegocio = await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'NOMBRE_NEGOCIO'") ?? "Facturix";
        var rnc = await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'RNC'") ?? "---";
        var telefono = await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'TELEFONO'") ?? "---";

        var path = Db.GetSecureTempPath($"recibo_{id}.pdf");
        PdfGenerator.GenerarReciboVenta(
            path,
            nombreNegocio,
            rnc,
            telefono,
            items.ToList(),
            subtotal,
            0,
            subtotal,
            subtotal,
            0,
            id.ToString("000000"),
            ncf,
            mensajePie,
            usarBranding);

        return PhysicalFile(path, "application/pdf", $"Recibo_{id}.pdf");
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();
        try
        {
            var factura = await conn.QueryFirstOrDefaultAsync("SELECT * FROM Facturas WHERE Id = @id", new { id }, tran);
            if (factura == null)
            {
                FlashError("Factura no encontrada.");
                return RedirectToAction(nameof(Index));
            }

            var ventas = await conn.QueryAsync("SELECT * FROM Ventas WHERE FacturaId = @id", new { id }, tran);
            
            // 1. Restaurar Inventario
            foreach(var venta in ventas)
            {
                var components = await conn.QueryAsync<(int ProductoId, int Cantidad)>("SELECT ProductoId, Cantidad FROM Combos WHERE ComboId = @productId", new { productId = (int)venta.ProductoId }, tran);
                var compList = components.ToList();
                if (compList.Count > 0)
                {
                    foreach (var component in compList)
                    {
                        await conn.ExecuteAsync("UPDATE Productos SET Stock = Stock + @amount WHERE Id = @componentId", new { amount = component.Cantidad * (int)venta.Cantidad, componentId = component.ProductoId }, tran);
                    }
                }
                else
                {
                    await conn.ExecuteAsync("UPDATE Productos SET Stock = Stock + @quantity WHERE Id = @productId", new { quantity = (int)venta.Cantidad, productId = (int)venta.ProductoId }, tran);
                }
            }

            // 2. Reversar de Caja o Cuentas por Cobrar
            var metodo = factura.MetodoPago as string;
            var total = (decimal)factura.Total;
            
            if (metodo == "CREDITO")
            {
                var cxpId = factura.ClienteId;
                await conn.ExecuteAsync("UPDATE CuentasPorCobrar SET DeudaTotal = MAX(0, DeudaTotal - @total) WHERE ClienteId = @id", new { total, id = cxpId }, tran);
            }
            else
            {
                await conn.ExecuteAsync("UPDATE Caja SET SaldoFinal = SaldoFinal - @total WHERE Id = @CajaId", new { total, CajaId = factura.CajaId }, tran);
            }

            // 3. Eliminar registros
            await conn.ExecuteAsync("DELETE FROM Ventas WHERE FacturaId = @id", new { id }, tran);
            await conn.ExecuteAsync("DELETE FROM Facturas WHERE Id = @id", new { id }, tran);
            
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Facturas", "Eliminación", $"Factura #{id} (Total: RD${total:N2}) eliminada permanentemente", tran);
            await tran.CommitAsync();

            FlashSuccess("Factura eliminada correctamente y el inventario/caja ha sido restaurado.");
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            FlashError("Error eliminando la factura: " + ex.Message);
        }
        
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteItem(int facturaId, int ventaId)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();
        try
        {
            var factura = await conn.QueryFirstOrDefaultAsync("SELECT * FROM Facturas WHERE Id = @facturaId", new { facturaId }, tran);
            if (factura == null) return RedirectToAction(nameof(Details), new { id = facturaId });

            var venta = await conn.QueryFirstOrDefaultAsync("SELECT * FROM Ventas WHERE Id = @ventaId AND FacturaId = @facturaId", new { ventaId, facturaId }, tran);
            if (venta == null) return RedirectToAction(nameof(Details), new { id = facturaId });

            var montoVenta = (decimal)venta.Total;
            var nuevoTotal = (decimal)factura.Total - montoVenta;

            // Restaurar Stock
            var components = await conn.QueryAsync<(int ProductoId, int Cantidad)>("SELECT ProductoId, Cantidad FROM Combos WHERE ComboId = @productId", new { productId = (int)venta.ProductoId }, tran);
            var compList = components.ToList();
            if (compList.Count > 0)
            {
                foreach (var component in compList)
                {
                    await conn.ExecuteAsync("UPDATE Productos SET Stock = Stock + @amount WHERE Id = @componentId", new { amount = component.Cantidad * (int)venta.Cantidad, componentId = component.ProductoId }, tran);
                }
            }
            else
            {
                await conn.ExecuteAsync("UPDATE Productos SET Stock = Stock + @quantity WHERE Id = @productId", new { quantity = (int)venta.Cantidad, productId = (int)venta.ProductoId }, tran);
            }

            // Reversar en caja o cxp
            var metodo = factura.MetodoPago as string;
            if (metodo == "CREDITO")
            {
                var cxpId = factura.ClienteId;
                await conn.ExecuteAsync("UPDATE CuentasPorCobrar SET DeudaTotal = MAX(0, DeudaTotal - @monto) WHERE ClienteId = @id", new { monto = montoVenta, id = cxpId }, tran);
            }
            else
            {
                await conn.ExecuteAsync("UPDATE Caja SET SaldoFinal = SaldoFinal - @monto WHERE Id = @CajaId", new { monto = montoVenta, CajaId = factura.CajaId }, tran);
            }

            // Actualizar factura y eliminar linea
            await conn.ExecuteAsync("DELETE FROM Ventas WHERE Id = @ventaId", new { ventaId }, tran);
            await conn.ExecuteAsync("UPDATE Facturas SET Total = @nuevoTotal WHERE Id = @facturaId", new { nuevoTotal, facturaId }, tran);

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Facturas", "Edición", $"Producto retirado de la factura #{facturaId}. Nuevo Total: RD${nuevoTotal:N2}", tran);
            await tran.CommitAsync();

            FlashSuccess("Producto eliminado de la factura y valores ajustados correctamente.");
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            FlashError("Error al eliminar el producto: " + ex.Message);
        }

        return RedirectToAction(nameof(Details), new { id = facturaId });
    }
}
