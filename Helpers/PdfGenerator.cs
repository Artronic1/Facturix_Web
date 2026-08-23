using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using InventarioProVisual.Models;

namespace InventarioProVisual.Helpers;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class PdfGenerator
{
    public static void GenerarReciboVenta(string path, string negocio, string rnc, string tel, IReadOnlyList<ReciboItem> items, decimal sub, decimal desc, decimal tot, decimal pag, decimal cam, string invoiceNo = "", string ncf = "", string mensajePie = "¡Gracias por su compra!", bool usarBranding = true, string logoBase64 = "")
    {
        var logoBytes = usarBranding ? ObtenerLogoEmpresaPng(logoBase64) : null;
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(226, 600, Unit.Point); // Formato Ticket (80mm aprox)
                page.Margin(10);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Courier New"));

                page.Header().Column(col =>
                {
                    if (logoBytes != null)
                    {
                        col.Item().AlignCenter().Width(42).Height(42).Image(logoBytes);
                    }
                    col.Item().AlignCenter().Text(negocio).FontSize(12).Bold();
                    col.Item().AlignCenter().Text($"RNC: {rnc}");
                    col.Item().AlignCenter().Text($"TEL: {tel}");
                    col.Item().AlignCenter().Text("--------------------------------");
                    col.Item().AlignCenter().Text("RECIBO DE VENTA").Bold();
                    if (!string.IsNullOrEmpty(invoiceNo))
                    {
                        col.Item().AlignCenter().Text($"FACTURA NO. {invoiceNo}").Bold();
                    }
                    if (!string.IsNullOrEmpty(ncf))
                    {
                        col.Item().AlignCenter().Text($"NCF: {ncf}").Bold();
                    }
                    col.Item().Text($"FECHA: {DateTime.Now:dd/MM/yyyy HH:mm}");
                    col.Item().Text("--------------------------------");
                });

                page.Content().Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(1);
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(2);
                        });

                        foreach (var item in items)
                        {
                            table.Cell().Text($"{item.Cantidad}");
                            table.Cell().Text(item.NombreProducto);
                            table.Cell().AlignRight().Text($"{item.Total:N2}");
                        }
                    });

                    col.Item().PaddingTop(5).Column(c =>
                    {
                        c.Item().AlignRight().Text($"SUBTOTAL: RD${sub:N2}");
                        if (desc > 0) c.Item().AlignRight().Text($"DESC: RD${desc:N2}");
                        c.Item().AlignRight().Text($"TOTAL: RD${tot:N2}").Bold().FontSize(11);
                        c.Item().AlignRight().Text($"PAGADO: RD${pag:N2}");
                        c.Item().AlignRight().Text($"CAMBIO: RD${cam:N2}");
                    });
                });

                if (string.IsNullOrWhiteSpace(mensajePie)) 
                    mensajePie = "¡Gracias por su compra!";

                page.Footer().AlignCenter().PaddingTop(10).Text(mensajePie);
            });
        }).GeneratePdf(path);
    }

    public static void GenerarCotizacion(string path, string negocio, string rnc, string tel, Cotizacion c, List<DetalleCotizacion> dets, string logoBase64 = "")
    {
        var logoBytes = ObtenerLogoEmpresaPng(logoBase64);
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily(Fonts.Verdana));

                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        if (logoBytes != null)
                        {
                            col.Item().Width(56).Height(56).Image(logoBytes);
                        }
                        col.Item().Text(negocio).FontSize(20).Bold().FontColor(Colors.Blue.Medium);
                        col.Item().Text($"RNC: {rnc}");
                        col.Item().Text($"Tel: {tel}");
                    });

                    row.RelativeItem().AlignRight().Column(col =>
                    {
                        col.Item().Text($"COTIZACIÓN #{c.Id}").FontSize(16).Bold();
                        col.Item().Text($"Fecha: {c.Fecha:dd/MM/yyyy}");
                        col.Item().Text($"Vence: {c.FechaVencimiento:dd/MM/yyyy}");
                    });
                });

                page.Content().PaddingTop(20).Column(col =>
                {
                    col.Item().Text($"Cliente: {c.Cliente.ToUpper()}").Bold();
                    col.Item().PaddingVertical(10).LineHorizontal(1);

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(4);
                            columns.RelativeColumn(1);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(2);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Text("Producto").Bold();
                            header.Cell().AlignCenter().Text("Cant").Bold();
                            header.Cell().AlignRight().Text("Precio").Bold();
                            header.Cell().AlignRight().Text("Total").Bold();
                        });

                        foreach (var d in dets)
                        {
                            table.Cell().Text(d.NombreProducto);
                            table.Cell().AlignCenter().Text($"{d.Cantidad}");
                            table.Cell().AlignRight().Text($"{d.PrecioUnitario:N2}");
                            table.Cell().AlignRight().Text($"{d.PrecioUnitario * d.Cantidad:N2}");
                        }
                    });

                    col.Item().AlignRight().PaddingTop(10).Column(cSum =>
                    {
                        if (c.DescuentoMonto > 0) cSum.Item().Text($"Descuento: RD${c.DescuentoMonto:N2}");
                        cSum.Item().Text($"TOTAL GENERAL: RD${c.Total:N2}").FontSize(14).Bold().FontColor(Colors.Blue.Medium);
                    });
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Página ");
                    x.CurrentPageNumber();
                });
            });
        }).GeneratePdf(path);
    }

    public static void GenerarReporteArqueo(string path, string negocio, string rnc, Caja c, string usuario, decimal ventas, decimal totalEsperado, decimal fisico, string logoBase64 = "")
    {
        var logoBytes = ObtenerLogoEmpresaPng(logoBase64);
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(226, 500, Unit.Point); // Formato Térmico (80mm)
                page.Margin(10);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Courier New"));

                page.Header().Column(col =>
                {
                    if (logoBytes != null)
                    {
                        col.Item().AlignCenter().Width(42).Height(42).Image(logoBytes);
                    }
                    col.Item().AlignCenter().Text(negocio).FontSize(12).Bold();
                    col.Item().AlignCenter().Text("ARQUEO DE CAJA").FontSize(10).Bold();
                    col.Item().AlignCenter().Text("--------------------------------");
                });

                page.Content().Column(col =>
                {
                    col.Item().Text($"CAJA ID: {c.Id}");
                    col.Item().Text($"USUARIO: {usuario}");
                    col.Item().Text($"APERTURA: {c.Apertura}");
                    col.Item().Text($"CIERRE:   {DateTime.Now:dd/MM/yyyy HH:mm}");
                    col.Item().Text("--------------------------------");

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(2);
                        });

                        table.Cell().Text("FONDO INICIAL:");
                        table.Cell().AlignRight().Text($"{c.SaldoInicial:N2}");

                        table.Cell().Text("VENTAS:");
                        table.Cell().AlignRight().Text($"{ventas:N2}");

                        table.Cell().Text("ESPERADO:").Bold();
                        table.Cell().AlignRight().Text($"{totalEsperado:N2}").Bold();

                        table.Cell().PaddingTop(5).Text("FÍSICO:").Bold();
                        table.Cell().PaddingTop(5).AlignRight().Text($"{fisico:N2}").Bold();

                        var diff = fisico - totalEsperado;
                        table.Cell().Text("DIFERENCIA:");
                        table.Cell().AlignRight().Text($"{diff:N2}");
                    });

                    col.Item().PaddingTop(20).AlignCenter().Column(cSig =>
                    {
                        cSig.Item().Text("________________________________");
                        cSig.Item().Text("FIRMA DEL CAJERO");
                    });
                });

                page.Footer().AlignCenter().PaddingTop(10).Text("FACTURIX POS");
            });
        }).GeneratePdf(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? ObtenerLogoEmpresaPng(string logoBase64)
    {
        if (string.IsNullOrWhiteSpace(logoBase64))
        {
            return null;
        }

        try
        {
            var icoBytes = Convert.FromBase64String(logoBase64);
            using var iconStream = new MemoryStream(icoBytes);
#pragma warning disable CA1416 // Platform compatibility — Facturix targets Windows POS
            using var icon = new Icon(iconStream);
            using var bitmap = icon.ToBitmap();
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
#pragma warning restore CA1416
            return pngStream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static void GenerarManualUsuario(string path)
    {
        Document.Create(container =>
        {
            // Página de Portada
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontFamily(Fonts.Verdana));

                page.Content().Column(col =>
                {
                    col.Item().PaddingTop(100).AlignCenter().Text("FACTURIX POS").FontSize(48).Bold().FontColor(Colors.Blue.Medium);
                    col.Item().AlignCenter().Text("MANUAL INTEGRAL DE USUARIO").FontSize(18).LetterSpacing(0.2f).FontColor(Colors.Grey.Medium);
                    
                    col.Item().PaddingTop(50).AlignCenter().Text("Versión 1.0 - Edición Profesional").FontSize(12);
                    
                    col.Item().PaddingTop(200).AlignRight().Column(c => {
                        c.Item().Text("Desarrollado por:").FontSize(10).Italic();
                        c.Item().Text("Carlos R.").FontSize(14).Bold();
                    });
                });
                
                page.Footer().AlignCenter().Text("Documentación Oficial Facturix").FontSize(9).Italic();
            });

            // Página de Presentación y Agradecimientos
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(11).FontFamily(Fonts.Verdana));

                page.Header().Text("Presentación").FontSize(16).Bold().FontColor(Colors.Blue.Medium);
                
                page.Content().PaddingVertical(20).Column(col =>
                {
                    col.Item().Text("Agradecimientos").FontSize(14).Bold();
                    col.Item().PaddingTop(5).Text("Gracias por elegir FACTURIX POS como su solución tecnológica. Este sistema ha sido diseñado con el objetivo de simplificar la administración comercial y potenciar el crecimiento de su negocio a través de datos precisos y procesos automatizados.");
                    
                    col.Item().PaddingTop(20).Text("Introducción").FontSize(14).Bold();
                    col.Item().Text("Este manual le guiará a través de todas las funcionalidades del sistema, desde la facturación básica hasta la gestión avanzada de inventarios y nóminas. Facturix no es solo un punto de venta, es una herramienta de inteligencia de negocios diseñada para ser intuitiva, rápida y segura.");
                });
            });

            // Página de Contenido (Índice)
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(11).FontFamily(Fonts.Verdana));

                page.Header().Text("Tabla de Contenido").FontSize(16).Bold().FontColor(Colors.Blue.Medium);

                page.Content().PaddingVertical(20).Column(col =>
                {
                    string[] temas = { 
                        "1. Interfaz Principal y Dashboard", 
                        "2. Módulo de Facturación y Ventas", 
                        "3. Gestión de Cotizaciones",
                        "4. Inventario y Configuración de Combos",
                        "5. Control de Caja y Arqueo Físico",
                        "6. Administración de Gastos y Personal",
                        "7. Reportes y Exportación de Datos",
                        "8. Auditoría y Seguridad del Sistema"
                    };

                    foreach (var tema in temas)
                    {
                        col.Item().PaddingVertical(5).Row(row => {
                            row.RelativeItem().Text(tema);
                            row.AutoItem().Text("....................");
                        });
                    }
                });
            });

            // Páginas de Detalle
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10.5f).FontFamily(Fonts.Verdana));

                page.Header().Row(row => {
                    row.RelativeItem().Text("GUÍA OPERATIVA DETALLADA").FontSize(10).FontColor(Colors.Grey.Medium);
                    row.AutoItem().Text(x => { x.Span("Página "); x.CurrentPageNumber(); });
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    // Sección 1
                    col.Item().PaddingTop(10).Text("1. Dashboard y Análisis").FontSize(13).Bold().Underline();
                    col.Item().Text("El Dashboard es el corazón financiero del sistema. Muestra en tiempo real las ventas totales del día y una comparativa de 'Ventas vs Gastos' de los últimos 7 días. Las alertas de inventario le avisarán automáticamente cuando un producto esté por agotarse.");

                    // Sección 2
                    col.Item().PaddingTop(15).Text("2. Proceso de Venta").FontSize(13).Bold().Underline();
                    col.Item().Text("Para realizar una venta, seleccione los productos en la pestaña de Facturación. Puede usar el buscador o un lector de códigos de barras. Al presionar 'Cobrar', podrá registrar el pago y el sistema emitirá un recibo en PDF compatible con impresoras térmicas.");

                    // Sección 3
                    col.Item().PaddingTop(15).Text("3. Combos y Paquetes").FontSize(13).Bold().Underline();
                    col.Item().Text("La gestión de combos permite crear ofertas especiales. Al definir un producto como 'Combo', usted puede asignarle varios productos componentes. El sistema se encarga de descontar proporcionalmente cada componente del inventario al realizar una venta.");

                    // Sección 4
                    col.Item().PaddingTop(15).Text("4. Cierre de Caja").FontSize(13).Bold().Underline();
                    col.Item().Text("El control de caja es vital para evitar descuadres. Al cerrar el turno, el sistema le pedirá el 'Efectivo Físico'. Si hay diferencias con el total esperado, estas quedarán registradas en el reporte de arqueo para su revisión.");

                    // Sección 5
                    col.Item().PaddingTop(15).Text("5. Administración Financiera").FontSize(13).Bold().Underline();
                    col.Item().Text("Utilice el módulo de Gastos para registrar egresos operativos. En el módulo de Nómina, podrá llevar el control de su personal y exportar planillas directamente a Excel, facilitando la labor contable.");

                    // Sección 6
                    col.Item().PaddingTop(15).Text("6. Seguridad y Auditoría").FontSize(13).Bold().Underline();
                    col.Item().Text("Cada acción importante (anulaciones, cambios de precio, exportaciones) queda grabada con fecha, hora y usuario en el módulo de Auditoría. Esto garantiza una trazabilidad total de lo que sucede en su negocio.");
                });

                page.Footer().AlignCenter().Text("FACTURIX POS - Solución Profesional de Ventas").FontSize(8);
            });
        }).GeneratePdf(path);
    }
}
