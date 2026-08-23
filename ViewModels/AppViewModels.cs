using System.ComponentModel.DataAnnotations;
using InventarioProVisual.Models;

namespace FacturixWeb.ViewModels;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "El usuario es obligatorio.")]
    [Display(Name = "Usuario")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    [DataType(DataType.Password)]
    [Display(Name = "Contraseña")]
    public string Password { get; set; } = string.Empty;
}

public sealed class DashboardViewModel
{
    public double VentasHoy { get; set; }
    public double GastosHoy { get; set; }
    public double UtilidadHoy { get; set; }
    public long TransaccionesHoy { get; set; }
    public long ProductosActivos { get; set; }
    public long UnidadesVendidas { get; set; }
    public bool CajaAbierta { get; set; }
    public List<ProductoAlertaViewModel> AlertasStock { get; set; } = [];
    public List<TopProductoViewModel> TopProductos { get; set; } = [];
    public List<BalanceDiarioViewModel> BalanceSemanal { get; set; } = [];
}

public sealed class ProductoAlertaViewModel
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public int Stock { get; set; }
}

public sealed class TopProductoViewModel
{
    public string Nombre { get; set; } = string.Empty;
    public long Total { get; set; }
}

public sealed class BalanceDiarioViewModel
{
    public string Fecha { get; set; } = string.Empty;
    public double Ventas { get; set; }
    public double Gastos { get; set; }
}

public sealed class ProductIndexViewModel
{
    public string Search { get; set; } = string.Empty;
    public List<ProductoListItemViewModel> Items { get; set; } = [];
}

public sealed class ProductoListItemViewModel
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public decimal Precio { get; set; }
    public int Stock { get; set; }
    public string? CodigoBarras { get; set; }
    public bool EsCombo { get; set; }
    public string ComboResumen { get; set; } = string.Empty;
}

public sealed class ProductEditorViewModel
{
    public int? Id { get; set; }

    [Required(ErrorMessage = "El nombre es obligatorio.")]
    public string Nombre { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "9999999", ErrorMessage = "El precio debe ser válido.")]
    public decimal Precio { get; set; }

    [Range(0, 9999999)]
    public int Stock { get; set; }

    [Display(Name = "Código de barras")]
    public string? CodigoBarras { get; set; }

    public List<ComboComponentInputViewModel> Componentes { get; set; } = [];
    public List<SimpleSelectOptionViewModel> ProductosDisponibles { get; set; } = [];
}

public sealed class ComboComponentInputViewModel
{
    public int ProductoId { get; set; }
    public string NombreProducto { get; set; } = string.Empty;
    public int Cantidad { get; set; }
}

public sealed class SimpleSelectOptionViewModel
{
    public int Value { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class CustomerIndexViewModel
{
    public string Search { get; set; } = string.Empty;
    public List<Cliente> Items { get; set; } = [];
}

public sealed class CustomerEditorViewModel
{
    public long? Id { get; set; }

    [Required(ErrorMessage = "El nombre es obligatorio.")]
    public string Nombre { get; set; } = string.Empty;

    public string? Telefono { get; set; }
    public string? Direccion { get; set; }

    [Display(Name = "RNC / Cédula")]
    public string? Rnc { get; set; }
}

public sealed class ExpenseEditorViewModel
{
    public int? Id { get; set; }

    [Required]
    public string Concepto { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "9999999", ErrorMessage = "El monto debe ser mayor que cero.")]
    public decimal Monto { get; set; }

    [Required]
    public string Categoria { get; set; } = "OTROS";

    [DataType(DataType.Date)]
    public DateTime Fecha { get; set; } = DateTime.Today;
}

public sealed class ExpenseIndexViewModel
{
    public List<Gasto> Items { get; set; } = [];
}

public sealed class EmployeeEditorViewModel
{
    public int? Id { get; set; }

    [Required]
    public string Nombre { get; set; } = string.Empty;

    public string Cargo { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "9999999")]
    public decimal Salario { get; set; }

    [DataType(DataType.Date)]
    public DateTime FechaIngreso { get; set; } = DateTime.Today;

    public bool Activo { get; set; } = true;
}

public sealed class EmployeeIndexViewModel
{
    public List<Empleado> Items { get; set; } = [];
}

public sealed class PayrollInputViewModel
{
    [Required]
    public int EmpleadoId { get; set; }

    [Required]
    public decimal Monto { get; set; }

    [Required]
    public string Periodo { get; set; } = DateTime.Now.ToString("MMMM yyyy");
}

public sealed class SalesPageViewModel
{
    public string Search { get; set; } = string.Empty;
    public List<Producto> Productos { get; set; } = [];
    public List<Cliente> Clientes { get; set; } = [];
    public List<CartLineViewModel> Carrito { get; set; } = [];
    public List<RecentSaleViewModel> VentasRecientes { get; set; } = [];
    public List<Cotizacion> CotizacionesPendientes { get; set; } = [];
    public decimal Subtotal { get; set; }
    public decimal DescuentoMonto { get; set; }
    public decimal Total { get; set; }
    public decimal DiscountPercent { get; set; }
    public bool CajaAbierta { get; set; }
    public Caja? CajaActual { get; set; }
    public int? CotizacionActualId { get; set; }
    public string? CotizacionCargadaLabel { get; set; }
    public decimal AmountReceived { get; set; }
    public long SelectedCustomerId { get; set; } = 1;
    public string QuoteCustomerName { get; set; } = string.Empty;
}

public sealed class CartLineViewModel
{
    public int ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public int Stock { get; set; }
    public decimal Total => Price * Quantity;
}

public sealed class CartSessionState
{
    public List<CartSessionLine> Items { get; set; } = [];
    public decimal DiscountPercent { get; set; }
    public int? QuoteId { get; set; }
}

public sealed class CartSessionLine
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}

public sealed class RecentSaleViewModel
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public int CajaId { get; set; }
    public long? ClienteId { get; set; }
    public string Producto { get; set; } = string.Empty;
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal Total { get; set; }
    public DateTime Fecha { get; set; }
    public string FechaStr { get; set; } = string.Empty;
}

public sealed class QuoteIndexViewModel
{
    public string Search { get; set; } = string.Empty;
    public string Estado { get; set; } = "Todos";
    public List<Cotizacion> Items { get; set; } = [];
}

public sealed class QuoteDetailsPageViewModel
{
    public Cotizacion Cotizacion { get; set; } = new();
    public List<DetalleCotizacion> Detalles { get; set; } = [];
}

public sealed class ReportsIndexViewModel
{
    [DataType(DataType.Date)]
    public DateTime Desde { get; set; } = DateTime.Today.AddDays(-7);

    [DataType(DataType.Date)]
    public DateTime Hasta { get; set; } = DateTime.Today;

    public List<ReporteVentaDiaViewModel> Items { get; set; } = [];
    public double TotalRecaudado { get; set; }
}

public sealed class ReporteVentaDiaViewModel
{
    public string Fecha { get; set; } = string.Empty;
    public double TotalVendido { get; set; }
    public long UnidadesVendidas { get; set; }
    public long NumeroVentas { get; set; }
}

public sealed class UserIndexViewModel
{
    public List<Usuario> Items { get; set; } = [];
}

public sealed class UserEditorViewModel
{
    public int? Id { get; set; }

    [Required]
    [Display(Name = "Usuario")]
    public string NombreUsuario { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Nombre completo")]
    public string NombreCompleto { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string Rol { get; set; } = "User";

    public bool Activo { get; set; } = true;
    public List<string> PermisosSeleccionados { get; set; } = [];
    public List<string> PermisosDisponibles { get; set; } = [];
}

public sealed class AuditIndexViewModel
{
    public string Filter { get; set; } = string.Empty;
    public string Module { get; set; } = "Todos";
    public List<AuditoriaRegistro> Items { get; set; } = [];
}

public sealed class SettingsViewModel
{
    public string NombreNegocio { get; set; } = string.Empty;
    public string Rnc { get; set; } = string.Empty;
    public string Telefono { get; set; } = string.Empty;
    public string Direccion { get; set; } = string.Empty;
    public string BackupFolder { get; set; } = string.Empty;
    public bool HasLogo { get; set; }
    public string DataPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    
    // NCF y Branding
    public string SecuenciaNcf { get; set; } = string.Empty;
    public string MensajeRecibo { get; set; } = string.Empty;
    public bool UsarBranding { get; set; }
}
