using FlanderDev.RouteGen.Abstractions;
using System.Text.Json;

namespace F3M.Client.Shared;

public static class ApiErrors
{
    /// <summary>
    /// ApiException.Message is a generic "API call failed with status 400" string — the actual
    /// server-provided detail (e.g. "You cannot delete your own account.") is in ResponseBody,
    /// JSON-serialized as a plain string by BadRequest(ex.Message) on the server.
    /// </summary>
    public static string Describe(Exception ex)
    {
        if (ex is not ApiException { ResponseBody: { Length: > 0 } body })
            return ex.Message;

        try
        {
            return JsonSerializer.Deserialize<string>(body) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
