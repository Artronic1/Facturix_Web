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
            new("TenantDb", tenantDb),
            new("MustChangePassword", user.DebeCambiarPassword ? "true" : "false")
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

        if (user.DebeCambiarPassword)
        {
            return RedirectToAction(nameof(CambiarPassword));
        }

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult CambiarPassword()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction(nameof(Login));
        }
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CambiarPassword(string currentPassword, string newPassword, string confirmPassword)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction(nameof(Login));
        }

        if (string.IsNullOrWhiteSpace(newPassword) || string.IsNullOrWhiteSpace(confirmPassword))
        {
            ModelState.AddModelError(string.Empty, "Debe completar todos los campos.");
            return View();
        }

        if (newPassword != confirmPassword)
        {
            ModelState.AddModelError(string.Empty, "La nueva contraseña y la confirmación no coinciden.");
            return View();
        }

        var tenantDb = User.Claims.FirstOrDefault(c => c.Type == "TenantDb")?.Value;
        var rawId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(tenantDb) || string.IsNullOrEmpty(rawId) || !int.TryParse(rawId, out var userId))
        {
            return RedirectToAction(nameof(Login));
        }

        using var tenantConn = Db.CreateConnection(tenantDb);
        var user = tenantConn.QueryFirstOrDefault<Usuario>("SELECT * FROM Usuarios WHERE Id = @userId", new { userId });
        if (user == null)
        {
            return RedirectToAction(nameof(Login));
        }

        if (!string.IsNullOrWhiteSpace(currentPassword) && !Db.VerifyPassword(currentPassword, user.PasswordHash))
        {
            ModelState.AddModelError(string.Empty, "La contraseña actual no es correcta.");
            return View();
        }

        var newHash = Db.HashPassword(newPassword);
        tenantConn.Execute("UPDATE Usuarios SET PasswordHash = @newHash, DebeCambiarPassword = 0 WHERE Id = @userId", new { newHash, userId });

        // Update current authentication cookie to remove MustChangePassword flag
        var updatedClaims = User.Claims.Where(c => c.Type != "MustChangePassword").ToList();
        updatedClaims.Add(new Claim("MustChangePassword", "false"));

        var updatedIdentity = new ClaimsIdentity(updatedClaims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(updatedIdentity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });

        TempData["Success"] = "¡Contraseña actualizada exitosamente!";
        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        try
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
        }
        catch { }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        HttpContext.Session.Clear();

        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }
}
