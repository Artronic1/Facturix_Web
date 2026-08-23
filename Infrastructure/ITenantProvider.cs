using Microsoft.AspNetCore.Http;
using System.Linq;

namespace FacturixWeb.Infrastructure;

public interface ITenantProvider
{
    string GetCurrentTenantDbName();
}

public sealed class HttpTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private const string DefaultDbName = "facturix.db";

    public HttpTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetCurrentTenantDbName()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context?.User?.Identity?.IsAuthenticated == true)
        {
            var tenantClaim = context.User.Claims.FirstOrDefault(c => c.Type == "TenantDb");
            if (tenantClaim != null && !string.IsNullOrWhiteSpace(tenantClaim.Value))
            {
                return tenantClaim.Value;
            }
        }
        return DefaultDbName;
    }
}
