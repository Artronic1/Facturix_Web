using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

[Authorize(Roles = "Admin")]
public sealed class ReportsController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public ReportsController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index(DateTime? desde = null, DateTime? hasta = null)
    {
        var model = await BuildModelAsync(desde ?? DateTime.Today.AddDays(-7), hasta ?? DateTime.Today);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Export(DateTime? desde = null, DateTime? hasta = null)
    {
        var model = await BuildModelAsync(desde ?? DateTime.Today.AddDays(-7), hasta ?? DateTime.Today);

        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Resumen Diario");
        ws.Cell(1, 1).Value = "Fecha";
        ws.Cell(1, 2).Value = "Ventas";
        ws.Cell(1, 3).Value = "Unidades";
        ws.Cell(1, 4).Value = "Total Recaudado";
        ws.Range(1, 1, 1, 4).Style.Font.Bold = true;

        for (var index = 0; index < model.Items.Count; index++)
        {
            var row = index + 2;
            var item = model.Items[index];
            ws.Cell(row, 1).Value = item.Fecha;
            ws.Cell(row, 2).Value = item.NumeroVentas;
            ws.Cell(row, 3).Value = item.UnidadesVendidas;
            ws.Cell(row, 4).Value = item.TotalVendido;
            ws.Cell(row, 4).Style.NumberFormat.Format = "RD$ #,##0.00";
        }

        ws.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Reportes", "Exportar Excel", $"{model.Desde:yyyy-MM-dd} a {model.Hasta:yyyy-MM-dd}");
        
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Reporte_Facturix_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
    }

    private async Task<ReportsIndexViewModel> BuildModelAsync(DateTime desde, DateTime hasta)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var items = await conn.QueryAsync<ReporteVentaDiaViewModel>(
            """
            SELECT
                date(Fecha) AS Fecha,
                CAST(COALESCE(SUM(Total), 0) AS REAL) AS TotalVendido,
                CAST(COALESCE(SUM(Cantidad), 0) AS INTEGER) AS UnidadesVendidas,
                COUNT(*) AS NumeroVentas
            FROM Ventas
            WHERE date(Fecha) BETWEEN @desde AND @hasta
            GROUP BY date(Fecha)
            ORDER BY date(Fecha) DESC
            """,
            new { desde = desde.ToString("yyyy-MM-dd"), hasta = hasta.ToString("yyyy-MM-dd") });

        var itemsList = items.ToList();
        return new ReportsIndexViewModel
        {
            Desde = desde,
            Hasta = hasta,
            Items = itemsList,
            TotalRecaudado = itemsList.Sum(x => x.TotalVendido)
        };
    }
}
