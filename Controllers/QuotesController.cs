using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using InventarioProVisual.Helpers;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Data.Common;

namespace FacturixWeb.Controllers;

[Authorize]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class QuotesController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public QuotesController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    protected override string CartSessionKey => "FACTURIX_WEB_QUOTE_CART";
    private const string SqlProductoProjection = "Id, Nombre, Precio, Stock, CodigoBarras";
    private const string SqlCotizacionProjection = "Id, Cliente, Fecha, FechaVencimiento, DescuentoPorcentaje, DescuentoMonto, Total, Estado, ClienteId";

    [HttpGet]
    public async Task<IActionResult> Index(string search = "", string estado = "Todos")
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var sql = $"SELECT {SqlCotizacionProjection} FROM Cotizaciones WHERE Cliente LIKE @search";
        if (!string.Equals(estado, "Todos", StringComparison.OrdinalIgnoreCase))
        {
            sql += " AND Estado = @estado";
        }

        sql += " ORDER BY Id DESC";
        var items = await conn.QueryAsync<Cotizacion>(sql, new { search = $"%{search}%", estado });

        return View(new QuoteIndexViewModel
        {
            Search = search,
            Estado = estado,
            Items = items.ToList()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var quote = await conn.QueryFirstOrDefaultAsync<Cotizacion>($"SELECT {SqlCotizacionProjection} FROM Cotizaciones WHERE Id = @id", new { id });
        if (quote is null)
        {
            FlashError("Cotización no encontrada.");
            return RedirectToAction(nameof(Index));
        }

        var details = await conn.QueryAsync<DetalleCotizacion>(
            """
            SELECT d.*, COALESCE(NULLIF(d.NombreProducto, ''), p.Nombre) AS NombreProducto
            FROM DetallesCotizacion d
            LEFT JOIN Productos p ON p.Id = d.ProductoId
            WHERE d.CotizacionId = @id
            ORDER BY d.Id
            """,
            new { id });

        return View(new QuoteDetailsPageViewModel
        {
            Cotizacion = quote,
            Detalles = details.ToList()
        });
    }

    [HttpGet]
    public IActionResult LoadToCart(int id)
    {
        return RedirectToAction("LoadQuote", "Sales", new { id });
    }

    [HttpGet]
    public IActionResult Facturar(int id)
    {
        return RedirectToAction("LoadQuote", "Sales", new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Pdf(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var quote = await conn.QueryFirstOrDefaultAsync<Cotizacion>($"SELECT {SqlCotizacionProjection} FROM Cotizaciones WHERE Id = @id", new { id });
        if (quote is null)
        {
            FlashError("Cotización no encontrada.");
            return RedirectToAction(nameof(Index));
        }

        var details = await conn.QueryAsync<DetalleCotizacion>(
            """
            SELECT d.*, COALESCE(NULLIF(d.NombreProducto, ''), p.Nombre) AS NombreProducto
            FROM DetallesCotizacion d
            LEFT JOIN Productos p ON p.Id = d.ProductoId
            WHERE d.CotizacionId = @id
            ORDER BY d.Id
            """,
            new { id });

        var path = Db.GetSecureTempPath($"cotizacion_{id}.pdf");
        
        var nombreNegocio = await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'NOMBRE_NEGOCIO'") ?? "Facturix";
        var rnc = await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'RNC'") ?? "---";
        var telefono = await conn.ExecuteScalarAsync<string>("SELECT Valor FROM Configuracion WHERE Clave = 'TELEFONO'") ?? "---";

        PdfGenerator.GenerarCotizacion(
            path,
            nombreNegocio,
            rnc,
            telefono,
            quote,
            details.ToList());

        return PhysicalFile(path, "application/pdf", $"Cotizacion_{id}.pdf");
    }

    [HttpGet]
    public async Task<IActionResult> Create(string search = "")
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var exactBarcodeMatch = await conn.QueryFirstOrDefaultAsync<Producto>(
                $"SELECT {SqlProductoProjection} FROM Productos WHERE CodigoBarras = @search",
                new { search });
                
            if (exactBarcodeMatch != null)
            {
                var state = GetCartState();
                var line = state.Items.FirstOrDefault(x => x.ProductId == exactBarcodeMatch.Id);
                if (line is null)
                {
                    state.Items.Add(new CartSessionLine { ProductId = exactBarcodeMatch.Id, Quantity = 1 });
                }
                else
                {
                    line.Quantity = line.Quantity + 1;
                }
                SaveCartState(state);
                FlashSuccess($"{exactBarcodeMatch.Nombre} agregado.");
                return RedirectToAction(nameof(Create), new { search = "" });
            }
        }
        
        var stateCart = GetCartState();
        var cartLines = await BuildCartLinesAsync(conn, stateCart);
        var subtotal = cartLines.Sum(x => x.Total);
        var discountAmount = subtotal * (stateCart.DiscountPercent / 100m);

        var productos = await conn.QueryAsync<Producto>(
            $"""
            SELECT {SqlProductoProjection}
            FROM Productos
            WHERE Nombre LIKE @term OR COALESCE(CodigoBarras, '') LIKE @term
            ORDER BY Nombre
            LIMIT 80
            """,
            new { term = $"%{search}%" });

        var clientes = await conn.QueryAsync<Cliente>("SELECT * FROM Clientes ORDER BY Nombre");

        return View(new SalesPageViewModel
        {
            Search = search,
            Productos = productos.ToList(),
            Clientes = clientes.ToList(),
            Carrito = cartLines,
            Subtotal = subtotal,
            DescuentoMonto = discountAmount,
            Total = subtotal - discountAmount,
            DiscountPercent = stateCart.DiscountPercent
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddToCart(int productId, int quantity = 1, string search = "")
    {
        if (quantity <= 0) quantity = 1;

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var product = await conn.QueryFirstOrDefaultAsync<Producto>($"SELECT {SqlProductoProjection} FROM Productos WHERE Id = @productId", new { productId });

        if (product is null)
        {
            FlashError("Producto no disponible.");
            return RedirectToAction(nameof(Create), new { search });
        }

        var state = GetCartState();
        var line = state.Items.FirstOrDefault(x => x.ProductId == productId);
        if (line is null)
        {
            state.Items.Add(new CartSessionLine { ProductId = productId, Quantity = quantity });
        }
        else
        {
            line.Quantity = line.Quantity + quantity;
        }

        SaveCartState(state);
        FlashSuccess($"{product.Nombre} agregado.");
        return RedirectToAction(nameof(Create), new { search });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult UpdateQuantity(int productId, int quantity)
    {
        var state = GetCartState();
        var line = state.Items.FirstOrDefault(x => x.ProductId == productId);
        if (line is null) return RedirectToAction(nameof(Create));

        if (quantity <= 0)
        {
            state.Items.Remove(line);
        }
        else
        {
            line.Quantity = quantity;
        }

        SaveCartState(state);
        return RedirectToAction(nameof(Create));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult SetDiscount(decimal discountPercent)
    {
        var state = GetCartState();
        state.DiscountPercent = Math.Clamp(discountPercent, 0m, 100m);
        SaveCartState(state);
        return RedirectToAction(nameof(Create));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RemoveFromCart(int productId)
    {
        var state = GetCartState();
        state.Items.RemoveAll(x => x.ProductId == productId);
        SaveCartState(state);
        return RedirectToAction(nameof(Create));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ClearCart()
    {
        ClearCartState();
        return RedirectToAction(nameof(Create));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveQuote(long customerId, string? customerName)
    {
        var state = GetCartState();
        
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var lines = await BuildCartLinesAsync(conn, state);
        if (lines.Count == 0)
        {
            FlashError("El carrito está vacío.");
            return RedirectToAction(nameof(Create));
        }

        var quoteCustomerName = await ResolveQuoteCustomerNameAsync(conn, customerId, customerName);
        var subtotal = lines.Sum(x => x.Total);
        var discountAmount = subtotal * (state.DiscountPercent / 100m);
        var total = subtotal - discountAmount;

        using var tran = await conn.BeginTransactionAsync();
        try
        {
            var quoteId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO Cotizaciones (Cliente, Fecha, FechaVencimiento, DescuentoPorcentaje, DescuentoMonto, Total, Estado, ClienteId)
                VALUES (@Cliente, @Fecha, @FechaVencimiento, @DescuentoPorcentaje, @DescuentoMonto, @Total, 'Pendiente', @ClienteId);
                SELECT last_insert_rowid();
                """,
                new
                {
                    Cliente = quoteCustomerName,
                    Fecha = DateTime.Now.ToString(Db.DateTimeFormat),
                    FechaVencimiento = DateTime.Now.AddDays(7).ToString(Db.DateTimeFormat),
                    DescuentoPorcentaje = state.DiscountPercent,
                    DescuentoMonto = discountAmount,
                    Total = total,
                    ClienteId = customerId <= 0 ? (long?)null : customerId
                },
                tran);

            foreach (var line in lines)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO DetallesCotizacion (CotizacionId, ProductoId, NombreProducto, Cantidad, PrecioUnitario)
                    VALUES (@CotizacionId, @ProductoId, @NombreProducto, @Cantidad, @PrecioUnitario)
                    """,
                    new
                    {
                        CotizacionId = quoteId,
                        ProductoId = line.ProductId,
                        NombreProducto = line.Name,
                        Cantidad = line.Quantity,
                        PrecioUnitario = line.Price
                    },
                    tran);
            }

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Vendedor", "Cotizaciones", "Creación", $"Cotización #{quoteId} para {quoteCustomerName}", tran);
            await tran.CommitAsync();
            ClearCartState();
            FlashSuccess($"Cotización #{quoteId} guardada exitosamente.");
            return RedirectToAction(nameof(Details), new { id = quoteId });
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            FlashError($"No se pudo guardar la cotización: {ex.Message}");
            return RedirectToAction(nameof(Create));
        }
    }

    private async Task<List<CartLineViewModel>> BuildCartLinesAsync(DbConnection conn, CartSessionState state)
    {
        if (state.Items.Count == 0) return [];

        var productIds = state.Items.Select(x => x.ProductId).Distinct().ToArray();
        var productsList = await conn.QueryAsync<Producto>(
            $"SELECT {SqlProductoProjection} FROM Productos WHERE Id IN @productIds",
            new { productIds });
            
        var products = productsList.ToDictionary(x => x.Id);

        var lines = new List<CartLineViewModel>();
        foreach (var item in state.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product)) continue;

            lines.Add(new CartLineViewModel
            {
                ProductId = product.Id,
                Name = product.Nombre,
                Price = product.Precio,
                Quantity = item.Quantity,
                Stock = product.Stock
            });
        }
        return lines;
    }

    private static async Task<string> ResolveQuoteCustomerNameAsync(DbConnection conn, long customerId, string? customerName)
    {
        if (!string.IsNullOrWhiteSpace(customerName)) return customerName.Trim();
        if (customerId > 0)
        {
            var customer = await conn.QueryFirstOrDefaultAsync<Cliente>("SELECT * FROM Clientes WHERE Id = @customerId", new { customerId });
            if (customer is not null) return customer.Nombre;
        }
        return "Consumidor Final";
    }
}
