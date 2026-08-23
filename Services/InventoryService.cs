using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using System.Data.Common;

namespace FacturixWeb.Services;

public interface IInventoryService
{
    Task<bool> IsComboAvailableAsync(DbConnection conn, int productId, int quantity, DbTransaction? tran = null);
    Task<int> GetEffectiveMaxStockAsync(DbConnection conn, int productId, int ownStock, DbTransaction? tran = null);
    Task DiscountStockAsync(DbConnection conn, DbTransaction tran, int productId, int quantity);
}

public sealed class InventoryService : IInventoryService
{
    public async Task<bool> IsComboAvailableAsync(DbConnection conn, int productId, int quantity, DbTransaction? tran = null)
    {
        var components = (await conn.QueryAsync<(int ProductoId, int Cantidad)>(
            "SELECT ProductoId, Cantidad FROM Combos WHERE ComboId = @productId",
            new { productId }, tran)).ToList();

        if (components.Count == 0) return true;

        foreach (var c in components)
        {
            var componentStock = await conn.ExecuteScalarAsync<int>(
                "SELECT Stock FROM Productos WHERE Id = @id",
                new { id = c.ProductoId }, tran);
            
            if (componentStock < c.Cantidad * quantity)
            {
                return false;
            }
        }
        return true;
    }

    public async Task<int> GetEffectiveMaxStockAsync(DbConnection conn, int productId, int ownStock, DbTransaction? tran = null)
    {
        var components = (await conn.QueryAsync<(int ProductoId, int Cantidad)>(
            "SELECT ProductoId, Cantidad FROM Combos WHERE ComboId = @productId",
            new { productId }, tran)).ToList();

        if (components.Count == 0) return ownStock;

        var maxFromComponents = int.MaxValue;
        foreach (var c in components)
        {
            if (c.Cantidad <= 0) continue;
            var componentStock = await conn.ExecuteScalarAsync<int>(
                "SELECT Stock FROM Productos WHERE Id = @id",
                new { id = c.ProductoId }, tran);
            maxFromComponents = Math.Min(maxFromComponents, componentStock / c.Cantidad);
        }

        return Math.Min(ownStock, maxFromComponents);
    }

    public async Task DiscountStockAsync(DbConnection conn, DbTransaction tran, int productId, int quantity)
    {
        var components = (await conn.QueryAsync<(int ProductoId, int Cantidad)>(
            "SELECT ProductoId, Cantidad FROM Combos WHERE ComboId = @productId",
            new { productId }, tran)).ToList();

        // Always decrement the product's own stock
        var affected = await conn.ExecuteAsync(
            "UPDATE Productos SET Stock = Stock - @quantity WHERE Id = @productId AND Stock >= @quantity", 
            new { quantity, productId }, tran);

        if (affected == 0 && components.Count == 0)
        {
            throw new InvalidOperationException($"No hay stock suficiente para el producto ID {productId}.");
        }

        // Si es combo, descontamos sus componentes (y se permite stock propio negativo o no afectado si no maneja inventario propio)
        if (components.Count > 0)
        {
            foreach (var component in components)
            {
                var amountToDiscount = component.Cantidad * quantity;
                var compAffected = await conn.ExecuteAsync(
                    "UPDATE Productos SET Stock = Stock - @amount WHERE Id = @componentId AND Stock >= @amount",
                    new { amount = amountToDiscount, componentId = component.ProductoId },
                    tran);
                
                if (compAffected == 0)
                {
                    throw new InvalidOperationException($"No hay stock suficiente del ingrediente ID {component.ProductoId} para el combo ID {productId}.");
                }
            }
        }
    }
}
