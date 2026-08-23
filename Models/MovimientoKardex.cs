using System;

namespace InventarioProVisual.Models;

public record MovimientoKardex(
    long Id,
    long ProductoId,
    string Tipo, // "ENTRADA" o "SALIDA"
    int Cantidad,
    string Motivo,
    string Fecha,
    long UsuarioId
);
