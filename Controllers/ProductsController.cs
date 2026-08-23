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
public sealed class ProductsController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public ProductsController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string search = "")
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var items = await conn.QueryAsync<ProductoListItemViewModel>(
            """
            SELECT
                p.Id,
                p.Nombre,
                p.Precio,
                p.Stock,
                p.CodigoBarras,
                CASE WHEN COUNT(c.Id) > 0 THEN 1 ELSE 0 END AS EsCombo,
                COALESCE(GROUP_CONCAT(cp.Nombre || ' x' || c.Cantidad, ', '), '') AS ComboResumen
            FROM Productos p
            LEFT JOIN Combos c ON c.ComboId = p.Id
            LEFT JOIN Productos cp ON cp.Id = c.ProductoId
            WHERE p.Nombre LIKE @term OR COALESCE(p.CodigoBarras, '') LIKE @term
            GROUP BY p.Id, p.Nombre, p.Precio, p.Stock, p.CodigoBarras
            ORDER BY p.Nombre
            """,
            new { term = $"%{search}%" });

        var model = new ProductIndexViewModel
        {
            Search = search,
            Items = items.ToList()
        };

        return View(model);
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
    public async Task<IActionResult> Save(ProductEditorViewModel model)
    {
        NormalizeComponents(model);

        if (!ModelState.IsValid)
        {
            await PopulateSelectableProductsAsync(model, model.Id);
            return View("Editor", model);
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();

        try
        {
            var productId = model.Id ?? 0;
            if (model.Id is null)
            {
                productId = await conn.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO Productos (Nombre, Precio, Stock, CodigoBarras)
                    VALUES (@Nombre, @Precio, @Stock, @CodigoBarras);
                    SELECT last_insert_rowid();
                    """,
                    new { model.Nombre, model.Precio, model.Stock, model.CodigoBarras }, tran);
            }
            else
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE Productos
                    SET Nombre = @Nombre,
                        Precio = @Precio,
                        Stock = @Stock,
                        CodigoBarras = @CodigoBarras
                    WHERE Id = @Id
                    """,
                    new { Id = model.Id.Value, model.Nombre, model.Precio, model.Stock, model.CodigoBarras }, tran);
            }

            await conn.ExecuteAsync("DELETE FROM Combos WHERE ComboId = @id", new { id = productId }, tran);
            foreach (var componente in model.Componentes)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO Combos (ComboId, ProductoId, Cantidad) VALUES (@comboId, @productoId, @cantidad)",
                    new { comboId = productId, productoId = componente.ProductoId, cantidad = componente.Cantidad }, tran);
            }

            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Inventario", model.Id is null ? "Creación" : "Edición", $"{model.Nombre}", tran);
            await tran.CommitAsync();
            FlashSuccess("Producto guardado correctamente.");
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            ModelState.AddModelError(string.Empty, ex.Message);
            await PopulateSelectableProductsAsync(model, model.Id);
            return View("Editor", model);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();
        try
        {
            var product = await conn.QueryFirstOrDefaultAsync<Producto>("SELECT * FROM Productos WHERE Id = @id", new { id }, tran);
            if (product is null)
            {
                FlashError("Producto no encontrado.");
                return RedirectToAction(nameof(Index));
            }

            await conn.ExecuteAsync("DELETE FROM Combos WHERE ComboId = @id OR ProductoId = @id", new { id }, tran);
            await conn.ExecuteAsync("DELETE FROM Productos WHERE Id = @id", new { id }, tran);
            
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Inventario", "Eliminación", product.Nombre, tran);
            await tran.CommitAsync();
            FlashSuccess("Producto eliminado.");
        }
        catch
        {
            await tran.RollbackAsync();
            FlashError("No se pudo eliminar el producto. Verifique si tiene ventas asociadas.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restock(int id, int quantity)
    {
        if (quantity <= 0)
        {
            FlashError("La cantidad a reabastecer debe ser mayor que cero.");
            return RedirectToAction(nameof(Index));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();
        try
        {
            await conn.ExecuteAsync("UPDATE Productos SET Stock = Stock + @quantity WHERE Id = @id", new { id, quantity }, tran);
            var nombre = await conn.ExecuteScalarAsync<string>("SELECT Nombre FROM Productos WHERE Id = @id", new { id }, tran) ?? $"Producto #{id}";
            
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Inventario", "Reabastecimiento", $"{nombre}: +{quantity}", tran);
            await tran.CommitAsync();

            FlashSuccess("Inventario actualizado.");
        }
        catch
        {
            await tran.RollbackAsync();
            FlashError("Error al actualizar inventario.");
        }
        
        return RedirectToAction(nameof(Index));
    }

    private async Task<ProductEditorViewModel> BuildEditorModelAsync(int? id)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var model = new ProductEditorViewModel();

        if (id.HasValue)
        {
            var producto = await conn.QueryFirstOrDefaultAsync<Producto>("SELECT * FROM Productos WHERE Id = @id", new { id });
            if (producto is not null)
            {
                model.Id = producto.Id;
                model.Nombre = producto.Nombre;
                model.Precio = producto.Precio;
                model.Stock = producto.Stock;
                model.CodigoBarras = producto.CodigoBarras;
                var componentes = await conn.QueryAsync<ComboComponentInputViewModel>(
                    """
                    SELECT
                        c.ProductoId,
                        p.Nombre AS NombreProducto,
                        c.Cantidad
                    FROM Combos c
                    JOIN Productos p ON p.Id = c.ProductoId
                    WHERE c.ComboId = @id
                    ORDER BY p.Nombre
                    """,
                    new { id });
                model.Componentes = componentes.ToList();
            }
        }

        await PopulateSelectableProductsAsync(model, id);
        while (model.Componentes.Count < 5)
        {
            model.Componentes.Add(new ComboComponentInputViewModel());
        }

        return model;
    }

    private async Task PopulateSelectableProductsAsync(ProductEditorViewModel model, int? currentProductId)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var productos = await conn.QueryAsync<SimpleSelectOptionViewModel>(
            """
            SELECT Id AS Value, Nombre AS Text
            FROM Productos
            WHERE (@currentProductId IS NULL OR Id <> @currentProductId)
            ORDER BY Nombre
            """,
            new { currentProductId });
        model.ProductosDisponibles = productos.ToList();
    }

    private static void NormalizeComponents(ProductEditorViewModel model)
    {
        model.Componentes = model.Componentes
            .Where(x => x.ProductoId > 0 && x.Cantidad > 0)
            .GroupBy(x => x.ProductoId)
            .Select(g => new ComboComponentInputViewModel
            {
                ProductoId = g.Key,
                Cantidad = g.Sum(x => x.Cantidad)
            })
            .ToList();
    }
}
