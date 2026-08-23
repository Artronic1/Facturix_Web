using System.Security.Claims;

namespace FacturixWeb.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(raw, out var userId) ? userId : 0;
    }

    public static string GetUserName(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
    }

    public static string GetFullName(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.GivenName) ?? principal.GetUserName();
    }

    public static string GetRoleName(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    }
}
