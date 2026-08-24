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
        
        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");

        var resumen = await conn.QueryFirstOrDefaultAsync<(double TotalVendido, long NumeroVentas)>(
            "SELECT CAST(COALESCE(SUM(Total), 0) AS REAL), COUNT(*) FROM Ventas WHERE SUBSTR(Fecha, 1, 10) = @todayStr",
            new { todayStr });

        var gastosHoy = await conn.ExecuteScalarAsync<double>(
            "SELECT CAST(COALESCE(SUM(Monto), 0) AS REAL) FROM Gastos WHERE SUBSTR(Fecha, 1, 10) = @todayStr",
            new { todayStr });

        var productosActivos = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Productos");
        
        var unidadesVendidas = await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(SUM(Cantidad), 0) FROM Ventas WHERE SUBSTR(Fecha, 1, 10) = @todayStr",
            new { todayStr });
        
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

        var last7Days = Enumerable.Range(0, 7)
            .Select(i => DateTime.Today.AddDays(-6 + i).ToString("yyyy-MM-dd"))
            .ToList();

        var startDate = last7Days.First();

        var ventasSemanaRows = await conn.QueryAsync<(string Fecha, double Total)>(
            "SELECT SUBSTR(Fecha, 1, 10) AS Fecha, CAST(COALESCE(SUM(Total), 0) AS REAL) AS Total FROM Ventas WHERE SUBSTR(Fecha, 1, 10) >= @startDate GROUP BY SUBSTR(Fecha, 1, 10)",
            new { startDate });
        var ventasSemana = ventasSemanaRows.ToDictionary(x => x.Fecha, x => x.Total);

        var gastosSemanaRows = await conn.QueryAsync<(string Fecha, double Total)>(
            "SELECT SUBSTR(Fecha, 1, 10) AS Fecha, CAST(COALESCE(SUM(Monto), 0) AS REAL) AS Total FROM Gastos WHERE SUBSTR(Fecha, 1, 10) >= @startDate GROUP BY SUBSTR(Fecha, 1, 10)",
            new { startDate });
        var gastosSemana = gastosSemanaRows.ToDictionary(x => x.Fecha, x => x.Total);

        var balanceSemanal = last7Days.Select(d => new BalanceDiarioViewModel
        {
            Fecha = d,
            Ventas = ventasSemana.GetValueOrDefault(d, 0.0),
            Gastos = gastosSemana.GetValueOrDefault(d, 0.0)
        }).ToList();

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
            BalanceSemanal = balanceSemanal
        };

        return View(model);
    }
}
