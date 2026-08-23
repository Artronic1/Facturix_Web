using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace FacturixWeb.Services;

public sealed record CheckoutLineRequest(int ProductId, decimal Price, int Quantity, decimal Total);

public sealed record CheckoutResult(bool Success, int InvoiceId, string ErrorMessage = "");

public interface ISalesService
{
    Task<CheckoutResult> ProcessCheckoutAsync(
        SqliteConnection conn, 
        List<CheckoutLineRequest> lines, 
        int cashId, 
        decimal discountPercent, 
        string paymentMethod, 
        long customerId, 
        int userId,
        int? quoteId = null);
}

public sealed class SalesService : ISalesService
{
    private readonly IInventoryService _inventoryService;

    public SalesService(IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    public async Task<CheckoutResult> ProcessCheckoutAsync(
        SqliteConnection conn, 
        List<CheckoutLineRequest> lines, 
        int cashId, 
        decimal discountPercent, 
        string paymentMethod, 
        long customerId, 
        int userId,
        int? quoteId = null)
    {
        if (lines.Count == 0) return new CheckoutResult(false, 0, "El carrito está vacío.");

        using var tran = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            var subtotal = lines.Sum(x => x.Total);
            var discount = Math.Clamp(discountPercent, 0m, 100m);
            var total = subtotal * (1 - (discount / 100m));
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var resolvedCustomerId = customerId <= 0 ? 1L : customerId;

            var ncf = "";
            var currentNcf = await conn.ExecuteScalarAsync<string>(
                "SELECT Valor FROM Configuracion WHERE Clave = 'SECUENCIA_NCF'", null, tran);
                
            if (!string.IsNullOrWhiteSpace(currentNcf))
            {
                ncf = currentNcf;
                var nextNcf = IncrementNcf(currentNcf);
                await conn.ExecuteAsync(
                    "UPDATE Configuracion SET Valor = @nextNcf WHERE Clave = 'SECUENCIA_NCF'", 
                    new { nextNcf }, tran);
            }

            var invoiceId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO Facturas (ClienteId, CajaId, Total, MetodoPago, Fecha, Ncf, UsuarioId, Estado)
                VALUES (@ClienteId, @CajaId, @Total, @MetodoPago, @Fecha, @Ncf, @UsuarioId, @Estado);
                SELECT last_insert_rowid();
                """,
                new { 
                    ClienteId = resolvedCustomerId, CajaId = cashId, Total = total, MetodoPago = paymentMethod, 
                    Fecha = timestamp, Ncf = ncf, UsuarioId = userId, 
                    Estado = paymentMethod == "CREDITO" ? "Pendiente" : "Pagada" 
                }, tran);

            var runningTotal = 0m;
            var factor = 1 - (discount / 100m);

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                var adjusted = index == lines.Count - 1
                    ? total - runningTotal
                    : Math.Round(line.Total * factor, 2, MidpointRounding.AwayFromZero);
                runningTotal += adjusted;

                await InsertSaleAsync(conn, tran, invoiceId, line, cashId, adjusted, timestamp, resolvedCustomerId, paymentMethod);
            }

            if (paymentMethod == "CREDITO")
            {
                var cxpExists = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CuentasPorCobrar WHERE ClienteId = @id", new { id = resolvedCustomerId }, tran);
                if (cxpExists > 0)
                {
                    await conn.ExecuteAsync("UPDATE CuentasPorCobrar SET DeudaTotal = DeudaTotal + @total, UltimaActualizacion = @fecha WHERE ClienteId = @id", 
                        new { total, fecha = timestamp, id = resolvedCustomerId }, tran);
                }
                else
                {
                    await conn.ExecuteAsync("INSERT INTO CuentasPorCobrar (ClienteId, DeudaTotal, UltimaActualizacion) VALUES (@id, @total, @fecha)",
                        new { id = resolvedCustomerId, total, fecha = timestamp }, tran);
                }
            }
            else
            {
                await conn.ExecuteAsync("UPDATE Caja SET SaldoFinal = SaldoFinal + @total WHERE Id = @cashId", new { total, cashId }, tran);
            }

            if (quoteId.HasValue && quoteId.Value > 0)
            {
                await conn.ExecuteAsync("UPDATE Cotizaciones SET Estado = 'Facturada' WHERE Id = @id", new { id = quoteId.Value }, tran);
            }

            await tran.CommitAsync();
            return new CheckoutResult(true, invoiceId);
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            return new CheckoutResult(false, 0, ex.Message);
        }
    }

    private async Task InsertSaleAsync(
        SqliteConnection conn, SqliteTransaction tran, int invoiceId, CheckoutLineRequest line, 
        int cashId, decimal totalAdjusted, string timestamp, long customerId, string paymentMethod)
    {
        await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO Ventas (FacturaId, ProductoId, CajaId, Cantidad, PrecioUnitario, Total, Fecha, ClienteId, MetodoPago)
            VALUES (@invoiceId, @productId, @cashId, @quantity, @unitPrice, @total, @fecha, @customerId, @paymentMethod);
            SELECT last_insert_rowid();
            """,
            new { invoiceId, productId = line.ProductId, cashId, quantity = line.Quantity, unitPrice = line.Price, total = totalAdjusted, fecha = timestamp, customerId, paymentMethod }, tran);

        await _inventoryService.DiscountStockAsync(conn, tran, line.ProductId, line.Quantity);
    }

    private static string IncrementNcf(string currentNcf)
    {
        if (string.IsNullOrWhiteSpace(currentNcf)) return "";
        var match = Regex.Match(currentNcf.Trim(), @"^([A-Za-z]+)(\d+)$");
        if (match.Success)
        {
            var prefix = match.Groups[1].Value;
            var numStr = match.Groups[2].Value;
            if (long.TryParse(numStr, out var num))
            {
                return $"{prefix}{(num + 1).ToString(new string('0', numStr.Length))}";
            }
        }
        return currentNcf;
    }
}
