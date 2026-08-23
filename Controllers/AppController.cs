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
        ViewBag.CurrentUserName = CurrentFullName;
        ViewBag.CurrentRole = CurrentRole;
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
