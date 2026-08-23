namespace InventarioProVisual.Models;

public class Cliente
{
    public long Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Telefono { get; set; }
    public string? Direccion { get; set; }
    public string? Rnc { get; set; }

    public Cliente() { }

    public Cliente(long id, string nombre, string? telefono, string? direccion, string? rnc)
    {
        Id = id;
        Nombre = nombre;
        Telefono = telefono;
        Direccion = direccion;
        Rnc = rnc;
    }
}
