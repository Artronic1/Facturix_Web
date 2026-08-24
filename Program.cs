using FacturixWeb.Infrastructure;
using InventarioProVisual.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

QuestPDF.Settings.License = LicenseType.Community;

builder.Services.AddControllersWithViews();
// CORS: allow any origin in development; restrict in production.
var allowedOrigins = builder.Configuration
    .GetSection("AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("MobileDev", policy =>
    {
        if (builder.Environment.IsDevelopment() || allowedOrigins.Length == 0)
        {
            // Expo dev client may use any local IP — allow all during development.
            policy
                .SetIsOriginAllowed(_ => true)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
        else
        {
            policy
                .WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<FacturixWeb.Infrastructure.ITenantProvider, FacturixWeb.Infrastructure.HttpTenantProvider>();
builder.Services.AddScoped<FacturixWeb.Infrastructure.IDbConnectionFactory, FacturixWeb.Infrastructure.DbConnectionFactory>();
builder.Services.AddScoped<FacturixWeb.Services.IInventoryService, FacturixWeb.Services.InventoryService>();
builder.Services.AddScoped<FacturixWeb.Services.ISalesService, FacturixWeb.Services.SalesService>();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = ".FacturixWeb.Antiforgery";
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".FacturixWeb.Session";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(12);
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.Cookie.Name = ".FacturixWeb.Auth";
        options.Cookie.Path = "/";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UsePathBase("/facturix");

MasterDb.Initialize();
Db.Initialize(app.Services.GetRequiredService<IHttpContextAccessor>());
try
{
    Db.EnsureDailyBackup();
}
catch
{
    // El sistema web debe seguir arrancando incluso si falla el respaldo automático.
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseCors("MobileDev");
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
