using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace FacturixWeb.Infrastructure;

public static class SessionExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void SetObject<T>(this ISession session, string key, T value)
    {
        session.SetString(key, JsonSerializer.Serialize(value, JsonOptions));
    }

    public static T? GetObject<T>(this ISession session, string key)
    {
        var value = session.GetString(key);
        return value is null ? default : JsonSerializer.Deserialize<T>(value, JsonOptions);
    }
}
