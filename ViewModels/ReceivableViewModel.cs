namespace FacturixWeb.ViewModels;

public class ReceivableViewModel
{
    public long ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public string ClienteTelefono { get; set; } = string.Empty;
    public decimal DeudaTotal { get; set; }
    public string UltimaActualizacion { get; set; } = string.Empty;
}
