using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.Services;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using InventarioProVisual.Helpers;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FacturixWeb.Controllers;

[Authorize]
public sealed class SalesController : AppController
{
    private const string SqlProductoProjection = "Id, Nombre, Precio, Stock, CodigoBarras";
    private const string SqlCajaProjection = "Id, UsuarioId, Apertura, Cierre, SaldoInicial, SaldoFinal, Estado";
    private const string SqlCotizacionProjection = "Id, Cliente, Fecha, FechaVencimiento, DescuentoPorcentaje, DescuentoMonto, Total, Estado, ClienteId";

    private readonly IDbConnectionFactory _dbConnectionFactory;
    private readonly IInventoryService _inventoryService;
    private readonly ISalesService _salesService;

    public SalesController(IDbConnectionFactory dbConnectionFactory, IInventoryService inventoryService, ISalesService salesService)
    {
        _dbConnectionFactory = dbConnectionFactory;
        _inventoryService = inventoryService;
        _salesService = salesService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string search = "")
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            using var conn = await _dbConnectionFactory.CreateConnectionAsync();
            var exactBarcodeMatch = await conn.QueryFirstOrDefaultAsync<Producto>(
                $"SELECT {SqlProductoProjection} FROM Productos WHERE CodigoBarras = @search AND Stock > 0",
                new { search });
                
            if (exactBarcodeMatch != null)
            {
                if (!await _inventoryService.IsComboAvailableAsync(conn, exactBarcodeMatch.Id, 1))
                {
                    FlashError($"{exactBarcodeMatch.Nombre} no está disponible (ingredientes insuficientes).");
                    return RedirectToAction(nameof(Index), new { search = "" });
                }

                var state = GetCartState();
                var line = state.Items.FirstOrDefault(x => x.ProductId == exactBarcodeMatch.Id);
                var newQty = (line?.Quantity ?? 0) + 1;
                var maxQty = await _inventoryService.GetEffectiveMaxStockAsync(conn, exactBarcodeMatch.Id, exactBarcodeMatch.Stock);
                newQty = Math.Min(newQty, maxQty);

                if (line is null)
                {
                    state.Items.Add(new CartSessionLine { ProductId = exactBarcodeMatch.Id, Quantity = newQty });
                }
                else
                {
                    line.Quantity = newQty;
                }
                SaveCartState(state);
                FlashSuccess($"{exactBarcodeMatch.Nombre} escaneado y agregado.");
                return RedirectToAction(nameof(Index), new { search = "" });
            }
        }
        
        return View(await BuildSalesViewModelAsync(search));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddToCart(int productId, int quantity = 1, string search = "")
    {
        if (quantity <= 0) quantity = 1;

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var product = await conn.QueryFirstOrDefaultAsync<Producto>(
            $"SELECT {SqlProductoProjection} FROM Productos WHERE Id = @productId AND Stock > 0",
            new { productId });

        if (product is null)
        {
            FlashError("Producto no disponible.");
            return RedirectToAction(nameof(Index), new { search });
        }

        if (!await _inventoryService.IsComboAvailableAsync(conn, productId, quantity))
        {
            FlashError($"{product.Nombre} no está disponible (ingredientes insuficientes).");
            return RedirectToAction(nameof(Index), new { search });
        }

        var maxQty = await _inventoryService.GetEffectiveMaxStockAsync(conn, productId, product.Stock);
        var state = GetCartState();
        var line = state.Items.FirstOrDefault(x => x.ProductId == productId);
        if (line is null)
        {
            state.Items.Add(new CartSessionLine { ProductId = productId, Quantity = Math.Min(quantity, maxQty) });
        }
        else
        {
            line.Quantity = Math.Min(maxQty, line.Quantity + quantity);
        }

        SaveCartState(state);
        FlashSuccess($"{product.Nombre} agregado al carrito.");
        return RedirectToAction(nameof(Index), new { search });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateQuantity(int productId, int quantity)
    {
        var state = GetCartState();
        var line = state.Items.FirstOrDefault(x => x.ProductId == productId);
        if (line is null) return RedirectToAction(nameof(Index));

        if (quantity <= 0)
        {
            state.Items.Remove(line);
        }
        else
        {
            using var conn = await _dbConnectionFactory.CreateConnectionAsync();
            var stock = await conn.ExecuteScalarAsync<int?>("SELECT Stock FROM Productos WHERE Id = @productId", new { productId }) ?? 0;
            var maxQty = await _inventoryService.GetEffectiveMaxStockAsync(conn, productId, stock);
            line.Quantity = Math.Min(quantity, maxQty);
            if (line.Quantity <= 0)
            {
                state.Items.Remove(line);
            }
        }

        SaveCartState(state);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult SetDiscount(decimal discountPercent)
    {
        var state = GetCartState();
        state.DiscountPercent = Math.Clamp(discountPercent, 0m, 100m);
        SaveCartState(state);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RemoveFromCart(int productId)
    {
        var state = GetCartState();
        state.Items.RemoveAll(x => x.ProductId == productId);
        SaveCartState(state);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ClearCart()
    {
        ClearCartState();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(long customerId, decimal amountReceived, string metodoPago = "EFECTIVO")
    {
        var state = GetCartState();
        
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var lines = await BuildCartLinesAsync(conn, state);
        if (lines.Count == 0)
        {
            FlashError("El carrito está vacío.");
            return RedirectToAction(nameof(Index));
        }

        var caja = await GetOpenCashAsync(conn);
        if (caja is null)
        {
            FlashError("Debe abrir la caja antes de vender.");
            return RedirectToAction(nameof(Index));
        }

        var subtotal = lines.Sum(x => x.Total);
        var total = subtotal * (1 - (state.DiscountPercent / 100m));
        
        if (metodoPago != "CREDITO" && amountReceived < total)
        {
            FlashError("El monto recibido no es suficiente.");
            return RedirectToAction(nameof(Index));
        }

        var requests = lines.Select(x => new CheckoutLineRequest(x.ProductId, x.Price, x.Quantity, x.Total)).ToList();
        var result = await _salesService.ProcessCheckoutAsync(
            conn, requests, caja.Id, state.DiscountPercent, metodoPago, customerId, CurrentUserId, state.QuoteId);

        if (result.Success)
        {
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Vendedor", "Ventas", "Venta", $"Factura #{result.InvoiceId} Total RD${total:N2} - {metodoPago}");
            ClearCartState();
            FlashSuccess($"Venta completada por RD${total:N2}.");
            TempData["DownloadReceipt"] = result.InvoiceId;
        }
        else
        {
            FlashError($"No se pudo procesar la venta: {result.ErrorMessage}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> LoadQuote(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var quote = await conn.QueryFirstOrDefaultAsync<Cotizacion>($"SELECT {SqlCotizacionProjection} FROM Cotizaciones WHERE Id = @id", new { id });
        if (quote is null)
        {
            FlashError("Cotización no encontrada.");
            return RedirectToAction(nameof(Index));
        }

        var details = (await conn.QueryAsync<DetalleCotizacion>(
            """
            SELECT d.*, COALESCE(NULLIF(d.NombreProducto, ''), p.Nombre) AS NombreProducto
            FROM DetallesCotizacion d
            LEFT JOIN Productos p ON p.Id = d.ProductoId
            WHERE d.CotizacionId = @id
            """,
            new { id })).ToList();

        var state = new CartSessionState
        {
            DiscountPercent = quote.DescuentoPorcentaje,
            QuoteId = quote.Id,
            Items = details.Select(x => new CartSessionLine
            {
                ProductId = x.ProductoId,
                Quantity = x.Cantidad
            }).ToList()
        };

        SaveCartState(state);
        FlashSuccess($"Cotización #{id} cargada en el carrito.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<SalesPageViewModel> BuildSalesViewModelAsync(string search)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var state = GetCartState();
        var cartLines = await BuildCartLinesAsync(conn, state);
        var subtotal = cartLines.Sum(x => x.Total);
        var discountAmount = subtotal * (state.DiscountPercent / 100m);
        var openCash = await GetOpenCashAsync(conn);

        var allProducts = (await conn.QueryAsync<Producto>(
            $"""
            SELECT {SqlProductoProjection}
            FROM Productos
            WHERE Stock > 0
              AND (Nombre LIKE @term OR COALESCE(CodigoBarras, '') LIKE @term)
            ORDER BY Nombre
            LIMIT 80
            """,
            new { term = $"%{search}%" })).ToList();

        var availableProducts = new List<Producto>();
        foreach (var p in allProducts)
        {
            if (await _inventoryService.IsComboAvailableAsync(conn, p.Id, 1))
            {
                availableProducts.Add(p);
            }
        }

        return new SalesPageViewModel
        {
            Search = search,
            Productos = availableProducts,
            Clientes = (await conn.QueryAsync<Cliente>("SELECT * FROM Clientes ORDER BY Nombre")).ToList(),
            VentasRecientes = (await conn.QueryAsync<RecentSaleViewModel>(
                """
                SELECT MIN(v.Id) AS Id, MIN(v.ProductoId) AS ProductoId, v.CajaId, MIN(v.ClienteId) AS ClienteId, 
                       CASE WHEN COUNT(*) > 1 THEN COUNT(*) || ' productos (Varios)' ELSE MIN(p.Nombre) END AS Producto, 
                       SUM(v.Cantidad) AS Cantidad, MIN(v.PrecioUnitario) AS PrecioUnitario, SUM(v.Total) AS Total, 
                       MIN(v.Fecha) AS Fecha, MIN(v.Fecha) AS FechaStr
                FROM Ventas v
                JOIN Productos p ON p.Id = v.ProductoId
                GROUP BY v.CajaId, v.Fecha
                ORDER BY MIN(v.Id) DESC
                LIMIT 30
                """)).ToList(),
            CotizacionesPendientes = (await conn.QueryAsync<Cotizacion>(
                $"""
                SELECT {SqlCotizacionProjection}
                FROM Cotizaciones
                WHERE Estado != 'Facturada'
                ORDER BY Id DESC
                LIMIT 12
                """)).ToList(),
            Carrito = cartLines,
            Subtotal = subtotal,
            DescuentoMonto = discountAmount,
            Total = subtotal - discountAmount,
            DiscountPercent = state.DiscountPercent,
            CajaActual = openCash,
            CajaAbierta = openCash is not null,
            CotizacionActualId = state.QuoteId,
            CotizacionCargadaLabel = state.QuoteId.HasValue ? $"Cotización #{state.QuoteId.Value} cargada" : null,
            AmountReceived = subtotal - discountAmount,
            SelectedCustomerId = 1
        };
    }

    private async Task<List<CartLineViewModel>> BuildCartLinesAsync(SqliteConnection conn, CartSessionState state)
    {
        if (state.Items.Count == 0) return [];

        var productIds = state.Items.Select(x => x.ProductId).Distinct().ToArray();
        var products = (await conn.QueryAsync<Producto>(
            $"SELECT {SqlProductoProjection} FROM Productos WHERE Id IN @productIds",
            new { productIds })).ToDictionary(x => x.Id);

        var sanitizedItems = new List<CartSessionLine>();
        var lines = new List<CartLineViewModel>();
        foreach (var item in state.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product)) continue;

            var quantity = Math.Min(item.Quantity, product.Stock);
            if (quantity <= 0) continue;

            sanitizedItems.Add(new CartSessionLine { ProductId = item.ProductId, Quantity = quantity });
            lines.Add(new CartLineViewModel
            {
                ProductId = product.Id,
                Name = product.Nombre,
                Price = product.Precio,
                Quantity = quantity,
                Stock = product.Stock
            });
        }

        if (sanitizedItems.Count != state.Items.Count)
        {
            state.Items = sanitizedItems;
            SaveCartState(state);
        }

        return lines;
    }

    private static async Task<Caja?> GetOpenCashAsync(SqliteConnection conn)
    {
        return await conn.QueryFirstOrDefaultAsync<Caja>($"SELECT {SqlCajaProjection} FROM Caja WHERE Estado = 'ABIERTA' ORDER BY Id DESC LIMIT 1");
    }
}
