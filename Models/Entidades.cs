using System;

namespace InventarioProVisual.Models;

public class Usuario
{
    public int Id { get; set; }
    public string NombreUsuario { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string NombreCompleto { get; set; } = string.Empty;
    public string Rol { get; set; } = string.Empty;
    public bool Activo { get; set; } = true;
    public DateTime FechaCreacion { get; set; }
    public DateTime? UltimoAcceso { get; set; }
    public string Permisos { get; set; } = string.Empty;
    public bool DebeCambiarPassword { get; set; } = false;
}

public class Producto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public decimal Precio { get; set; }
    public int Stock { get; set; }
    public string? CodigoBarras { get; set; }
}

public class Venta
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public int CajaId { get; set; }
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal Total { get; set; }
    public DateTime Fecha { get; set; }
    public long? ClienteId { get; set; }
    public string MetodoPago { get; set; } = "EFECTIVO";
}

public class Caja
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public DateTime Apertura { get; set; }
    public DateTime? Cierre { get; set; }
    public decimal SaldoInicial { get; set; }
    public decimal SaldoFinal { get; set; }
    public string Estado { get; set; } = "ABIERTA";
}

public class Configuracion
{
    public int Id { get; set; }
    public string Clave { get; set; } = string.Empty;
    public string Valor { get; set; } = string.Empty;
}

public class Cotizacion
{
    public int Id { get; set; }
    public string Cliente { get; set; } = string.Empty;
    public long? ClienteId { get; set; }
    public DateTime Fecha { get; set; }
    public DateTime FechaVencimiento { get; set; }
    public decimal DescuentoPorcentaje { get; set; }
    public decimal DescuentoMonto { get; set; }
    public decimal Total { get; set; }
    public string Estado { get; set; } = "Pendiente";
}

public class DetalleCotizacion
{
    public int Id { get; set; }
    public int CotizacionId { get; set; }
    public int ProductoId { get; set; }
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public string NombreProducto { get; set; } = string.Empty;
}

public class ReciboItem
{
    public int Cantidad { get; set; }
    public string NombreProducto { get; set; } = string.Empty;
    public decimal Total { get; set; }
}

public class AuditoriaRegistro
{
    public int Id { get; set; }
    public int? UsuarioId { get; set; }
    public string Usuario { get; set; } = string.Empty;
    public string NombreCompleto { get; set; } = string.Empty;
    public string Rol { get; set; } = string.Empty;
    public string Modulo { get; set; } = string.Empty;
    public string Accion { get; set; } = string.Empty;
    public string Detalle { get; set; } = string.Empty;
    public DateTime FechaHora { get; set; }
    public string Equipo { get; set; } = string.Empty;
}

public class Gasto
{
    public int Id { get; set; }
    public string Concepto { get; set; } = string.Empty;
    public decimal Monto { get; set; }
    public DateTime Fecha { get; set; }
    public string Categoria { get; set; } = string.Empty;
    public int? UsuarioId { get; set; }
}

public class Empleado
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Cargo { get; set; } = string.Empty;
    public decimal Salario { get; set; }
    public DateTime FechaIngreso { get; set; }
    public bool Activo { get; set; } = true;
}

public class PagoNomina
{
    public int Id { get; set; }
    public int EmpleadoId { get; set; }
    public decimal Monto { get; set; }
    public DateTime FechaPago { get; set; }
    public string Periodo { get; set; } = string.Empty;
}
