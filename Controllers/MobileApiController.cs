using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.Services;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Data.Common;

namespace FacturixWeb.Controllers;

[ApiController]
[Route("api/mobile")]
public sealed class MobileApiController : ControllerBase
{
    private const string SqlProductoProjection = "Id, Nombre, Precio, Stock, CodigoBarras";

    private readonly IDbConnectionFactory _dbConnectionFactory;
    private readonly IInventoryService _inventoryService;
    private readonly ISalesService _salesService;

    public MobileApiController(IDbConnectionFactory dbConnectionFactory, IInventoryService inventoryService, ISalesService salesService)
    {
        _dbConnectionFactory = dbConnectionFactory;
        _inventoryService = inventoryService;
        _salesService = salesService;
    }

    [HttpPost("auth/login")]
    public async Task<IActionResult> Login([FromBody] MobileLoginRequest request)
    {
        var username = request.UserName.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new MobileErrorResponse("El usuario y la contraseña son obligatorios."));
        }

        using var masterConn = MasterDb.CreateConnection();
        await masterConn.OpenAsync();

        var superAdminHash = await masterConn.QueryFirstOrDefaultAsync<string>(
            "SELECT PasswordHash FROM SuperAdmins WHERE LOWER(NombreUsuario) = LOWER(@username)",
            new { username });

        if (!string.IsNullOrEmpty(superAdminHash) && MasterDb.VerifyPassword(request.Password, superAdminHash))
        {
            var principal = BuildPrincipal("master", username, "Super Administrador", "SuperAdmin", "master");
            await SignInAsync(principal);
            return Ok(BuildCurrentUser(principal));
        }

        var tenantDb = await masterConn.QueryFirstOrDefaultAsync<string>(
            """
            SELECT u.DbFileName
            FROM UsuariosGlobales u
            INNER JOIN Empresas e ON u.EmpresaId = e.Id
            WHERE LOWER(u.NombreUsuario) = LOWER(@username) AND e.Activa = 1
            """,
            new { username });

        if (string.IsNullOrWhiteSpace(tenantDb))
        {
            return Unauthorized(new MobileErrorResponse("Credenciales incorrectas o empresa deshabilitada."));
        }

        Db.InitializeDatabaseSchema(tenantDb);
        Usuario? user = null;
        DbConnection? tenantConnToClose = null;
        try
        {
            DbConnection connForTenant;
            if (masterConn is Npgsql.NpgsqlConnection)
            {
                connForTenant = masterConn;
                var schemaName = tenantDb.Replace(".db", "").Replace("-", "_").ToLower();
                user = await connForTenant.QueryFirstOrDefaultAsync<Usuario>(
                    $"SELECT * FROM {schemaName}.Usuarios WHERE LOWER(NombreUsuario) = LOWER(@username) AND Activo = 1",
                    new { username });
            }
            else
            {
                tenantConnToClose = Db.CreateConnection(tenantDb);
                connForTenant = tenantConnToClose;
                user = await connForTenant.QueryFirstOrDefaultAsync<Usuario>(
                    "SELECT * FROM Usuarios WHERE LOWER(NombreUsuario) = LOWER(@username) AND Activo = 1",
                    new { username });
            }

            if (user is null || !Db.VerifyPassword(request.Password, user.PasswordHash))
            {
                return Unauthorized(new MobileErrorResponse("Credenciales incorrectas."));
            }

            if (masterConn is Npgsql.NpgsqlConnection)
            {
                var schemaName = tenantDb.Replace(".db", "").Replace("-", "_").ToLower();
                await connForTenant.ExecuteAsync($"UPDATE {schemaName}.Usuarios SET UltimoAcceso = @fecha WHERE Id = @id", new { fecha = DateTime.Now.ToString(Db.DateTimeFormat), id = user.Id });
            }
            else
            {
                await connForTenant.ExecuteAsync("UPDATE Usuarios SET UltimoAcceso = @fecha WHERE Id = @id", new { fecha = DateTime.Now.ToString(Db.DateTimeFormat), id = user.Id });
            }
        }
        finally
        {
            tenantConnToClose?.Dispose();
        }

        var principalTenant = BuildPrincipal(
            user.Id.ToString(),
            user.NombreUsuario,
            string.IsNullOrWhiteSpace(user.NombreCompleto) ? user.NombreUsuario : user.NombreCompleto,
            user.Rol,
            tenantDb);

        await SignInAsync(principalTenant);
        Db.RegistrarAuditoria(user.Id, user.NombreUsuario, user.Rol, "Mobile", "Inicio de sesión", "Acceso desde app móvil");

        return Ok(BuildCurrentUser(principalTenant));
    }

    [Authorize]
    [HttpPost("auth/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        HttpContext.Session.Clear();
        return Ok(new { ok = true });
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(BuildCurrentUser(User));
    }

    [Authorize]
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        if (User.IsInRole("SuperAdmin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new MobileErrorResponse("El dashboard móvil requiere una empresa activa."));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");

        var resumen = await conn.QueryFirstOrDefaultAsync<(double TotalVendido, long NumeroVentas)>(
            "SELECT CAST(COALESCE(SUM(Total), 0) AS REAL), COUNT(*) FROM Ventas WHERE SUBSTR(Fecha, 1, 10) = @todayStr",
            new { todayStr });
        var gastosHoy = await conn.ExecuteScalarAsync<double>(
            "SELECT CAST(COALESCE(SUM(Monto), 0) AS REAL) FROM Gastos WHERE SUBSTR(Fecha, 1, 10) = @todayStr",
            new { todayStr });

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
            ProductosActivos = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Productos"),
            UnidadesVendidas = await conn.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(Cantidad), 0) FROM Ventas WHERE SUBSTR(Fecha, 1, 10) = @todayStr", new { todayStr }),
            CajaAbierta = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Caja WHERE Estado = 'ABIERTA'") > 0,
            AlertasStock = (await conn.QueryAsync<ProductoAlertaViewModel>(
                "SELECT Id, Nombre, Stock FROM Productos WHERE Stock < 5 ORDER BY Stock ASC, Nombre ASC LIMIT 20")).ToList(),
            TopProductos = (await conn.QueryAsync<TopProductoViewModel>(
                """
                SELECT p.Nombre AS Nombre, CAST(SUM(v.Cantidad) AS INTEGER) AS Total
                FROM Ventas v
                JOIN Productos p ON p.Id = v.ProductoId
                GROUP BY p.Id, p.Nombre
                ORDER BY Total DESC
                LIMIT 6
                """)).ToList(),
            BalanceSemanal = balanceSemanal
        };

        return Ok(model);
    }

    [Authorize]
    [HttpGet("products")]
    public async Task<IActionResult> Products([FromQuery] string search = "", [FromQuery] int limit = 80)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var maxRows = Math.Clamp(limit, 1, 200);
        var allProducts = await conn.QueryAsync<Producto>(
            $"""
            SELECT {SqlProductoProjection}
            FROM Productos
            WHERE Stock > 0
              AND (Nombre LIKE @term OR COALESCE(CodigoBarras, '') LIKE @term)
            ORDER BY Nombre
            LIMIT @maxRows
            """,
            new { term = $"%{search.Trim()}%", maxRows });

        var products = new List<Producto>();
        foreach(var product in allProducts)
        {
            if (await _inventoryService.IsComboAvailableAsync(conn, product.Id, 1))
            {
                products.Add(product);
            }
        }
        
        return Ok(products);
    }

    [Authorize]
    [HttpGet("customers")]
    public async Task<IActionResult> Customers([FromQuery] string search = "", [FromQuery] int limit = 80)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var maxRows = Math.Clamp(limit, 1, 200);
        var customers = (await conn.QueryAsync<Cliente>(
            """
            SELECT *
            FROM Clientes
            WHERE Nombre LIKE @term OR COALESCE(Telefono, '') LIKE @term OR COALESCE(Rnc, '') LIKE @term
            ORDER BY Nombre
            LIMIT @maxRows
            """,
            new { term = $"%{search.Trim()}%", maxRows })).ToList();

        return Ok(customers);
    }

    [Authorize]
    [HttpGet("sales/context")]
    public async Task<IActionResult> SalesContext([FromQuery] string search = "")
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var openCash = await GetOpenCashAsync(conn);
        var allProducts = await conn.QueryAsync<Producto>(
            $"""
            SELECT {SqlProductoProjection}
            FROM Productos
            WHERE Stock > 0
              AND (Nombre LIKE @term OR COALESCE(CodigoBarras, '') LIKE @term)
            ORDER BY Nombre
            LIMIT 80
            """,
            new { term = $"%{search.Trim()}%" });

        var products = new List<Producto>();
        foreach(var product in allProducts)
        {
            if (await _inventoryService.IsComboAvailableAsync(conn, product.Id, 1))
            {
                products.Add(product);
            }
        }

        var recentSales = (await conn.QueryAsync<RecentSaleViewModel>(
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
            """)).ToList();

        return Ok(new MobileSalesContextResponse(
            Products: products,
            Customers: (await conn.QueryAsync<Cliente>("SELECT * FROM Clientes ORDER BY Nombre LIMIT 200")).ToList(),
            RecentSales: recentSales,
            Cash: openCash,
            CashIsOpen: openCash is not null));
    }

    [Authorize]
    [HttpPost("sales/checkout")]
    public async Task<IActionResult> Checkout([FromBody] MobileCheckoutRequest request)
    {
        if (request.Items.Count == 0)
        {
            return BadRequest(new MobileErrorResponse("El carrito está vacío."));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var cash = await GetOpenCashAsync(conn);
        if (cash is null)
        {
            return BadRequest(new MobileErrorResponse("Debe abrir la caja antes de vender."));
        }

        var lines = await BuildCheckoutLinesAsync(conn, request.Items);
        if (lines.Count == 0)
        {
            return BadRequest(new MobileErrorResponse("No hay productos disponibles para facturar."));
        }

        var subtotal = lines.Sum(x => x.Total);
        var discountPercent = Math.Clamp(request.DiscountPercent, 0m, 100m);
        var total = subtotal * (1 - (discountPercent / 100m));
        var paymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod)
            ? "EFECTIVO"
            : request.PaymentMethod.Trim().ToUpperInvariant();

        if (paymentMethod != "CREDITO" && request.AmountReceived < total)
        {
            return BadRequest(new MobileErrorResponse("El monto recibido no es suficiente."));
        }

        var reqLines = lines.Select(l => new CheckoutLineRequest(l.ProductId, l.Price, l.Quantity, l.Total)).ToList();
        var result = await _salesService.ProcessCheckoutAsync(
            conn, reqLines, cash.Id, discountPercent, paymentMethod, request.CustomerId, int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0"));

        if (!result.Success)
        {
            return BadRequest(new MobileErrorResponse($"No se pudo procesar la venta: {result.ErrorMessage}"));
        }

        Audit("Ventas", "Venta móvil", $"Factura #{result.InvoiceId} Total RD${total:N2} - {paymentMethod}");
        return Ok(new MobileCheckoutResponse(result.InvoiceId, subtotal, total, paymentMethod));
    }

    [Authorize]
    [HttpGet("cash")]
    public async Task<IActionResult> Cash()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var openCash = await GetOpenCashAsync(conn);
        decimal expected = 0;

        if (openCash is not null)
        {
            var sales = await conn.ExecuteScalarAsync<decimal?>(
                "SELECT COALESCE(SUM(Total), 0) FROM Facturas WHERE CajaId = @id AND MetodoPago != 'CREDITO'",
                new { id = openCash.Id }) ?? 0m;
            var extraIncome = await conn.ExecuteScalarAsync<decimal?>(
                "SELECT COALESCE(SUM(Monto), 0) FROM PagosCuentas WHERE Fecha >= @Apertura",
                new { openCash.Apertura }) ?? 0m;
            expected = openCash.SaldoInicial + sales + extraIncome;
        }

        var history = (await conn.QueryAsync<Caja>(
            "SELECT * FROM Caja WHERE Estado = 'CERRADA' ORDER BY Id DESC LIMIT 20")).ToList();

        return Ok(new MobileCashResponse(openCash, openCash is not null, expected, history));
    }

    [Authorize]
    [HttpPost("cash/open")]
    public async Task<IActionResult> OpenCash([FromBody] MobileOpenCashRequest request)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var current = await GetOpenCashAsync(conn);
        if (current is not null)
        {
            return BadRequest(new MobileErrorResponse("Ya existe una caja abierta."));
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO Caja (UsuarioId, Apertura, SaldoInicial, SaldoFinal, Estado)
            VALUES (@UsuarioId, @Fecha, @Monto, @Monto, 'ABIERTA')
            """,
            new
            {
                UsuarioId = User.FindFirstValue(ClaimTypes.NameIdentifier),
                Fecha = DateTime.Now.ToString(Db.DateTimeFormat),
                Monto = Math.Max(0, request.InitialAmount)
            });

        Audit("Caja", "Apertura móvil", $"Monto inicial RD${request.InitialAmount:N2}");
        return await Cash();
    }

    [Authorize]
    [HttpPost("cash/close")]
    public async Task<IActionResult> CloseCash([FromBody] MobileCloseCashRequest request)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var current = await GetOpenCashAsync(conn);
        if (current is null)
        {
            return BadRequest(new MobileErrorResponse("No hay una caja abierta."));
        }

        var sales = await conn.ExecuteScalarAsync<decimal?>(
            "SELECT COALESCE(SUM(Total), 0) FROM Facturas WHERE CajaId = @id AND MetodoPago != 'CREDITO'",
            new { id = current.Id }) ?? 0m;
        var extraIncome = await conn.ExecuteScalarAsync<decimal?>(
            "SELECT COALESCE(SUM(Monto), 0) FROM PagosCuentas WHERE Fecha >= @Apertura",
            new { current.Apertura }) ?? 0m;
        var expected = current.SaldoInicial + sales + extraIncome;

        await conn.ExecuteAsync(
            "UPDATE Caja SET Estado = 'CERRADA', Cierre = @Cierre, SaldoFinal = @SaldoFinal WHERE Id = @Id",
            new
            {
                Cierre = DateTime.Now.ToString(Db.DateTimeFormat),
                SaldoFinal = Math.Max(0, request.PhysicalAmount),
                current.Id
            });

        Audit("Caja", "Cierre móvil", $"Caja #{current.Id}, esperado RD${expected:N2}, físico RD${request.PhysicalAmount:N2}");
        return await Cash();
    }

    [Authorize]
    [HttpPost("customers")]
    public async Task<IActionResult> SaveCustomer([FromBody] MobileCustomerRequest request)
    {
        var name = request.Nombre.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new MobileErrorResponse("El nombre del cliente es obligatorio."));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO Clientes (Nombre, Telefono, Direccion, Rnc)
            VALUES (@Nombre, @Telefono, @Direccion, @Rnc);
            SELECT last_insert_rowid();
            """,
            new
            {
                Nombre = name,
                request.Telefono,
                request.Direccion,
                request.Rnc
            });

        Audit("Clientes", "Crear móvil", name);
        return Ok(await conn.QueryFirstAsync<Cliente>("SELECT * FROM Clientes WHERE Id = @id", new { id }));
    }

    [Authorize]
    [HttpPost("products/{id:int}/restock")]
    public async Task<IActionResult> RestockProduct(int id, [FromBody] MobileRestockRequest request)
    {
        if (request.Quantity <= 0)
        {
            return BadRequest(new MobileErrorResponse("La cantidad debe ser mayor que cero."));
        }

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var productName = await conn.ExecuteScalarAsync<string>("SELECT Nombre FROM Productos WHERE Id = @id", new { id });
        if (string.IsNullOrWhiteSpace(productName))
        {
            return NotFound(new MobileErrorResponse("Producto no encontrado."));
        }

        await conn.ExecuteAsync("UPDATE Productos SET Stock = Stock + @quantity WHERE Id = @id", new { request.Quantity, id });
        Audit("Inventario", "Reabastecimiento móvil", $"{productName}: +{request.Quantity}");
        return Ok(await conn.QueryFirstAsync<Producto>($"SELECT {SqlProductoProjection} FROM Productos WHERE Id = @id", new { id }));
    }

    [Authorize]
    [HttpGet("quotes")]
    public async Task<IActionResult> Quotes()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        return Ok((await conn.QueryAsync<Cotizacion>(
            """
            SELECT Id, Cliente, Fecha, FechaVencimiento, DescuentoPorcentaje, DescuentoMonto, Total, Estado, ClienteId
            FROM Cotizaciones
            ORDER BY Id DESC
            LIMIT 80
            """)).ToList());
    }

    [Authorize]
    [HttpGet("finance")]
    public async Task<IActionResult> Finance()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var receivables = (await conn.QueryAsync<ReceivableViewModel>(
            """
            SELECT c.Id AS ClienteId, c.Nombre AS ClienteNombre, c.Telefono AS ClienteTelefono, cxp.DeudaTotal, cxp.UltimaActualizacion
            FROM CuentasPorCobrar cxp
            JOIN Clientes c ON c.Id = cxp.ClienteId
            WHERE cxp.DeudaTotal > 0
            ORDER BY cxp.DeudaTotal DESC
            LIMIT 80
            """)).ToList();
        var expenses = (await conn.QueryAsync<Gasto>("SELECT * FROM Gastos ORDER BY Fecha DESC, Id DESC LIMIT 80")).ToList();

        return Ok(new MobileFinanceResponse(receivables, expenses));
    }

    [Authorize]
    [HttpGet("reports")]
    public async Task<IActionResult> Reports()
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        var startDate = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
        var rows = (await conn.QueryAsync<ReporteVentaDiaViewModel>(
            """
            SELECT
                SUBSTR(Fecha, 1, 10) AS Fecha,
                CAST(COALESCE(SUM(Total), 0) AS REAL) AS TotalVendido,
                CAST(COALESCE(SUM(Cantidad), 0) AS INTEGER) AS UnidadesVendidas,
                COUNT(*) AS NumeroVentas
            FROM Ventas
            WHERE SUBSTR(Fecha, 1, 10) >= @startDate
            GROUP BY SUBSTR(Fecha, 1, 10)
            ORDER BY SUBSTR(Fecha, 1, 10) DESC
            LIMIT 30
            """,
            new { startDate })).ToList();

        return Ok(rows);
    }

    private static ClaimsPrincipal BuildPrincipal(string userId, string userName, string fullName, string role, string tenantDb)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.GivenName, fullName),
            new(ClaimTypes.Role, role),
            new("TenantDb", tenantDb)
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private Task SignInAsync(ClaimsPrincipal principal)
    {
        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });
    }

    private static MobileUserResponse BuildCurrentUser(ClaimsPrincipal principal)
    {
        var name = principal.FindFirstValue(ClaimTypes.Name) ?? "";
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? "";
        return new MobileUserResponse(
            UserId: principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            UserName: name,
            FullName: principal.FindFirstValue(ClaimTypes.GivenName) ?? name,
            Role: role,
            TenantDb: principal.FindFirstValue("TenantDb") ?? string.Empty);
    }

    private static async Task<Caja?> GetOpenCashAsync(DbConnection conn)
    {
        return await conn.QueryFirstOrDefaultAsync<Caja>(
            "SELECT Id, UsuarioId, Apertura, Cierre, SaldoInicial, SaldoFinal, Estado FROM Caja WHERE Estado = 'ABIERTA' ORDER BY Id DESC LIMIT 1");
    }

    private async Task<List<MobileCheckoutLine>> BuildCheckoutLinesAsync(DbConnection conn, List<MobileCheckoutItem> items)
    {
        var cleanItems = items
            .Where(x => x.ProductId > 0 && x.Quantity > 0)
            .GroupBy(x => x.ProductId)
            .Select(x => new MobileCheckoutItem(x.Key, x.Sum(y => y.Quantity)))
            .ToList();

        if (cleanItems.Count == 0) return [];

        var ids = cleanItems.Select(x => x.ProductId).ToArray();
        var products = (await conn.QueryAsync<Producto>(
            $"SELECT {SqlProductoProjection} FROM Productos WHERE Id IN @ids",
            new { ids })).ToDictionary(x => x.Id);

        var lines = new List<MobileCheckoutLine>();
        foreach (var item in cleanItems)
        {
            if (!products.TryGetValue(item.ProductId, out var product)) continue;

            var maxQty = await _inventoryService.GetEffectiveMaxStockAsync(conn, product.Id, product.Stock);
            var quantity = Math.Min(item.Quantity, maxQty);
            if (quantity <= 0) continue;

            lines.Add(new MobileCheckoutLine(product.Id, product.Nombre, product.Precio, quantity));
        }

        return lines;
    }

    private void Audit(string module, string action, string detail)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int.TryParse(userIdStr, out var userId);
        var userName = User.FindFirstValue(ClaimTypes.Name) ?? "";
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        Db.RegistrarAuditoria(userId, userName, role, module, action, detail);
    }
}

public sealed record MobileLoginRequest(string UserName, string Password);
public sealed record MobileErrorResponse(string Message);
public sealed record MobileUserResponse(string UserId, string UserName, string FullName, string Role, string TenantDb);
public sealed record MobileCheckoutItem(int ProductId, int Quantity);
public sealed record MobileCheckoutRequest(List<MobileCheckoutItem> Items, long CustomerId, decimal DiscountPercent, decimal AmountReceived, string PaymentMethod);
public sealed record MobileCheckoutLine(int ProductId, string Name, decimal Price, int Quantity)
{
    public decimal Total => Price * Quantity;
}
public sealed record MobileCheckoutResponse(int InvoiceId, decimal Subtotal, decimal Total, string PaymentMethod);
public sealed record MobileOpenCashRequest(decimal InitialAmount);
public sealed record MobileCloseCashRequest(decimal PhysicalAmount);
public sealed record MobileCashResponse(Caja? Cash, bool CashIsOpen, decimal ExpectedAmount, List<Caja> History);
public sealed record MobileCustomerRequest(string Nombre, string? Telefono, string? Direccion, string? Rnc);
public sealed record MobileRestockRequest(int Quantity);
public sealed record MobileFinanceResponse(List<ReceivableViewModel> Receivables, List<Gasto> Expenses);
public sealed record MobileSalesContextResponse(
    List<Producto> Products,
    List<Cliente> Customers,
    List<RecentSaleViewModel> RecentSales,
    Caja? Cash,
    bool CashIsOpen);
