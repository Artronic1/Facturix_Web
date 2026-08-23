using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

public sealed class HomeController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public HomeController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.IsInRole("SuperAdmin"))
        {
            return RedirectToAction("Index", "Master");
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        
        var resumen = await conn.QueryFirstOrDefaultAsync<(double TotalVendido, long NumeroVentas)>(
            "SELECT CAST(COALESCE(SUM(Total), 0) AS REAL), COUNT(*) FROM Ventas WHERE date(Fecha) = date('now', 'localtime')");

        var gastosHoy = await conn.ExecuteScalarAsync<double>(
            "SELECT CAST(COALESCE(SUM(Monto), 0) AS REAL) FROM Gastos WHERE date(Fecha) = date('now', 'localtime')");

        var productosActivos = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Productos");
        
        var unidadesVendidas = await conn.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(Cantidad), 0) FROM Ventas WHERE date(Fecha) = date('now', 'localtime')");
        
        var cajaAbierta = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Caja WHERE Estado = 'ABIERTA'") > 0;

        var alertasStock = await conn.QueryAsync<ProductoAlertaViewModel>(
            "SELECT Id, Nombre, Stock FROM Productos WHERE Stock < 5 ORDER BY Stock ASC, Nombre ASC LIMIT 20");

        var topProductos = await conn.QueryAsync<TopProductoViewModel>(
            """
            SELECT p.Nombre AS Nombre, CAST(SUM(v.Cantidad) AS INTEGER) AS Total
            FROM Ventas v
            JOIN Productos p ON p.Id = v.ProductoId
            GROUP BY p.Id, p.Nombre
            ORDER BY Total DESC
            LIMIT 6
            """);

        var balanceSemanal = await conn.QueryAsync<BalanceDiarioViewModel>(
            """
            SELECT
                d.fecha AS Fecha,
                CAST(COALESCE((SELECT SUM(Total) FROM Ventas WHERE date(Fecha) = d.fecha), 0) AS REAL) AS Ventas,
                CAST(COALESCE((SELECT SUM(Monto) FROM Gastos WHERE date(Fecha) = d.fecha), 0) AS REAL) AS Gastos
            FROM (
                SELECT date('now', '-6 days', 'localtime') AS fecha
                UNION SELECT date('now', '-5 days', 'localtime')
                UNION SELECT date('now', '-4 days', 'localtime')
                UNION SELECT date('now', '-3 days', 'localtime')
                UNION SELECT date('now', '-2 days', 'localtime')
                UNION SELECT date('now', '-1 days', 'localtime')
                UNION SELECT date('now', 'localtime')
            ) d
            ORDER BY d.fecha ASC
            """);

        var model = new DashboardViewModel
        {
            VentasHoy = resumen.TotalVendido,
            GastosHoy = gastosHoy,
            UtilidadHoy = resumen.TotalVendido - gastosHoy,
            TransaccionesHoy = resumen.NumeroVentas,
            ProductosActivos = productosActivos,
            UnidadesVendidas = unidadesVendidas,
            CajaAbierta = cajaAbierta,
            AlertasStock = alertasStock.ToList(),
            TopProductos = topProductos.ToList(),
            BalanceSemanal = balanceSemanal.ToList()
        };

        return View(model);
    }
}
