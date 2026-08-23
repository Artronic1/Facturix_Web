using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

[Authorize(Roles = "Admin")]
public sealed class AuditController : AppController
{
    private static readonly List<string> Modules =
    [
        "Todos",
        "Acceso",
        "Ventas",
        "Caja",
        "Inventario",
        "Clientes",
        "Cotizaciones",
        "Gastos",
        "Nómina",
        "Reportes",
        "Usuarios",
        "Configuración"
    ];

    [HttpGet]
    public IActionResult Index(string filter = "", string module = "Todos")
    {
        ViewBag.Modules = Modules;
        return View(new AuditIndexViewModel
        {
            Filter = filter,
            Module = module,
            Items = Db.ObtenerAuditoria(filter, module).ToList()
        });
    }
}
