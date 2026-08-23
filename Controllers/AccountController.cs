using System.Security.Claims;
using FacturixWeb.ViewModels;
using Dapper;
using InventarioProVisual.Data;
using InventarioProVisual.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

public class AccountController : Controller
{
    [HttpGet]
    public IActionResult Login()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new LoginViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var username = model.UserName.Trim();
        using var masterConn = MasterDb.CreateConnection();
        masterConn.Open();

        // 1. Check SuperAdmin
        var superAdminHash = masterConn.QueryFirstOrDefault<string>("SELECT PasswordHash FROM SuperAdmins WHERE NombreUsuario = @username", new { username });
        if (!string.IsNullOrEmpty(superAdminHash) && MasterDb.VerifyPassword(model.Password, superAdminHash))
        {
            var adminClaims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "master"),
                new(ClaimTypes.Name, username),
                new(ClaimTypes.GivenName, "Súper Administrador"),
                new(ClaimTypes.Role, "SuperAdmin"),
                new("TenantDb", "master")
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(adminClaims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });

            return RedirectToAction("Index", "Master");
        }

        // 2. Check Global User
        var query = @"
            SELECT u.DbFileName 
            FROM UsuariosGlobales u 
            INNER JOIN Empresas e ON u.EmpresaId = e.Id 
            WHERE LOWER(u.NombreUsuario) = LOWER(@username) AND e.Activa = 1";
            
        var tenantDb = masterConn.QueryFirstOrDefault<string>(query, new { username });
        if (string.IsNullOrEmpty(tenantDb))
        {
            ModelState.AddModelError(string.Empty, "Credenciales incorrectas o la empresa está deshabilitada.");
            return View(model);
        }

        // 3. Authenticate in Tenant DB
        InventarioProVisual.Data.Db.InitializeDatabaseSchema(tenantDb);
        using var tenantConn = Db.CreateConnection(tenantDb);
        
        var user = tenantConn.QueryFirstOrDefault<Usuario>(
            "SELECT * FROM Usuarios WHERE LOWER(NombreUsuario) = LOWER(@username) AND Activo = 1",
            new { username });

        if (user is null || !Db.VerifyPassword(model.Password, user.PasswordHash))
        {
            ModelState.AddModelError(string.Empty, "Credenciales incorrectas.");
            return View(model);
        }

        tenantConn.Execute("UPDATE Usuarios SET UltimoAcceso = @fecha WHERE Id = @id", new { fecha = DateTime.Now.ToString(Db.DateTimeFormat), id = user.Id });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.NombreUsuario),
            new(ClaimTypes.GivenName, string.IsNullOrWhiteSpace(user.NombreCompleto) ? user.NombreUsuario : user.NombreCompleto),
            new(ClaimTypes.Role, user.Rol),
            new("TenantDb", tenantDb)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            });

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            Db.RegistrarAuditoria(
                User.FindFirstValue(ClaimTypes.NameIdentifier) is string rawId && int.TryParse(rawId, out var userId) ? userId : null,
                User.FindFirstValue(ClaimTypes.Name) ?? "usuario",
                User.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
                "Acceso",
                "Cierre de sesión",
                "Salida del sistema web");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        HttpContext.Session.Clear();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }
}
