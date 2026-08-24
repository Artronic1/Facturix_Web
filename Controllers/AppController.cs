using FacturixWeb.Infrastructure;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FacturixWeb.Controllers;

[Authorize]
public abstract class AppController : Controller
{
    protected virtual string CartSessionKey => "FACTURIX_WEB_CART";

    protected int CurrentUserId => User.GetUserId();
    protected string CurrentUserName => User.GetUserName();
    protected string CurrentFullName => User.GetFullName();
    protected string CurrentRole => User.GetRoleName();
    protected bool IsAdmin => User.IsInRole("Admin");

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        ViewBag.CurrentUserName = CurrentFullName;
        ViewBag.CurrentRole = CurrentRole;

        if (User.Identity?.IsAuthenticated == true)
        {
            var mustChangeClaim = User.Claims.FirstOrDefault(c => c.Type == "MustChangePassword")?.Value;
            if (string.Equals(mustChangeClaim, "true", StringComparison.OrdinalIgnoreCase))
            {
                var controllerName = context.RouteData.Values["controller"]?.ToString();
                var actionName = context.RouteData.Values["action"]?.ToString();
                if (!string.Equals(controllerName, "Account", StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(actionName, "CambiarPassword", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(actionName, "Logout", StringComparison.OrdinalIgnoreCase)))
                {
                    context.Result = new RedirectToActionResult("CambiarPassword", "Account", null);
                    return;
                }
            }
        }

        base.OnActionExecuting(context);
    }

    protected CartSessionState GetCartState()
    {
        return HttpContext.Session.GetObject<CartSessionState>(CartSessionKey) ?? new CartSessionState();
    }

    protected void SaveCartState(CartSessionState state)
    {
        HttpContext.Session.SetObject(CartSessionKey, state);
    }

    protected void ClearCartState()
    {
        HttpContext.Session.Remove(CartSessionKey);
    }

    protected void Audit(string modulo, string accion, string detalle = "", System.Data.IDbTransaction? tran = null)
    {
        Db.RegistrarAuditoria(CurrentUserId, CurrentUserName, CurrentRole, modulo, accion, detalle, tran);
    }

    protected void FlashSuccess(string message)
    {
        TempData["Success"] = message;
    }

    protected void FlashError(string message)
    {
        TempData["Error"] = message;
    }
}
