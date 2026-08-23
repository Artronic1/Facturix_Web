using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using FacturixWeb.Infrastructure;
using FacturixWeb.ViewModels;
using InventarioProVisual.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FacturixWeb.Controllers;

[Authorize(Roles = "Admin")]
public sealed class SettingsController : AppController
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public SettingsController(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await BuildModelAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBusinessData(SettingsViewModel model)
    {
        await SetConfigValuesAsync(new Dictionary<string, string>
        {
            ["NOMBRE_NEGOCIO"] = model.NombreNegocio ?? string.Empty,
            ["RNC"] = model.Rnc ?? string.Empty,
            ["TELEFONO"] = model.Telefono ?? string.Empty,
            ["DIRECCION"] = model.Direccion ?? string.Empty,
            ["SECUENCIA_NCF"] = model.SecuenciaNcf ?? string.Empty,
            ["MENSAJE_RECIBO"] = model.MensajeRecibo ?? string.Empty,
            ["USAR_BRANDING"] = model.UsarBranding ? "1" : "0"
        });

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Configuración", "Actualizar negocio", model.NombreNegocio ?? string.Empty);
        FlashSuccess("Datos del negocio actualizados.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBackupFolder(string backupFolder)
    {
        await SetConfigValueAsync("CARPETA_BACKUP", backupFolder ?? string.Empty);
        
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Configuración", "Actualizar ruta backup", backupFolder ?? string.Empty);
        FlashSuccess("Ruta de backup guardada.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BackupNow()
    {
        var path = Db.CreateBackupNow();
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Configuración", "Backup manual", path);
        FlashSuccess($"Backup generado en: {path}");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLogo(IFormFile? logoFile)
    {
        if (logoFile is null || logoFile.Length == 0)
        {
            FlashError("Seleccione un archivo .ico válido.");
            return RedirectToAction(nameof(Index));
        }

        using var ms = new MemoryStream();
        await logoFile.CopyToAsync(ms);
        await SetConfigValueAsync(Db.LogoEmpresaIcoConfigKey, Convert.ToBase64String(ms.ToArray()));
        
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Configuración", "Subir logo", logoFile.FileName);
        FlashSuccess("Logo cargado correctamente.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLogo()
    {
        await SetConfigValueAsync(Db.LogoEmpresaIcoConfigKey, string.Empty);
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Configuración", "Eliminar logo");
        FlashSuccess("Logo eliminado.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreDatabase(IFormFile? backupFile)
    {
        if (backupFile is null || backupFile.Length == 0)
        {
            FlashError("Seleccione un archivo de respaldo.");
            return RedirectToAction(nameof(Index));
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"facturix-restore-{Guid.NewGuid():N}.db");
        await using (var stream = System.IO.File.Create(tempPath))
        {
            await backupFile.CopyToAsync(stream);
        }

        try
        {
            Db.CreateBackupNow();
            System.IO.File.Copy(tempPath, Db.DbPath, overwrite: true);
            using var conn = await _dbConnectionFactory.CreateConnectionAsync();
            Db.RegistrarAuditoria(CurrentUserId, User.Identity?.Name ?? "", "Admin", "Configuración", "Restaurar backup", backupFile.FileName);
            FlashSuccess("Base de datos restaurada. Recargue la aplicación para trabajar con los datos restaurados.");
        }
        catch (Exception ex)
        {
            FlashError($"No se pudo restaurar el respaldo: {ex.Message}");
        }
        finally
        {
            System.IO.File.Delete(tempPath);
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<SettingsViewModel> BuildModelAsync()
    {
        var model = new SettingsViewModel();
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        
        var configs = await conn.QueryAsync<(string Clave, string Valor)>("SELECT Clave, Valor FROM Configuracion");
        var dict = new Dictionary<string, string>();
        foreach (var c in configs) dict[c.Clave] = c.Valor;

        model.NombreNegocio = dict.GetValueOrDefault("NOMBRE_NEGOCIO", "Facturix");
        model.Rnc = dict.GetValueOrDefault("RNC", string.Empty);
        model.Telefono = dict.GetValueOrDefault("TELEFONO", string.Empty);
        model.Direccion = dict.GetValueOrDefault("DIRECCION", string.Empty);
        model.SecuenciaNcf = dict.GetValueOrDefault("SECUENCIA_NCF", string.Empty);
        model.MensajeRecibo = dict.GetValueOrDefault("MENSAJE_RECIBO", string.Empty);
        model.UsarBranding = dict.GetValueOrDefault("USAR_BRANDING", "0") == "1";
        model.BackupFolder = dict.GetValueOrDefault("CARPETA_BACKUP", string.Empty);
        model.HasLogo = !string.IsNullOrWhiteSpace(dict.GetValueOrDefault(Db.LogoEmpresaIcoConfigKey, string.Empty));
        model.DataPath = Db.DbPath;
        model.BackupPath = Db.GetBackupFolderPath();
        
        return model;
    }

    private async Task SetConfigValueAsync(string clave, string valor)
    {
        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO Configuracion (Clave, Valor)
            VALUES (@clave, @valor)
            ON CONFLICT(Clave) DO UPDATE SET Valor = excluded.Valor
            """,
            new { clave, valor });
    }

    private async Task SetConfigValuesAsync(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return;

        using var conn = await _dbConnectionFactory.CreateConnectionAsync();
        using var tran = await conn.BeginTransactionAsync();

        const string sql =
            """
            INSERT INTO Configuracion (Clave, Valor)
            VALUES (@clave, @valor)
            ON CONFLICT(Clave) DO UPDATE SET Valor = excluded.Valor
            """;

        foreach (var item in values)
        {
            await conn.ExecuteAsync(sql, new { clave = item.Key, valor = item.Value }, tran);
        }

        await tran.CommitAsync();
    }
}
