namespace F3M.Shared.Api;

/// <summary>Outcome of an operation with no other return value, e.g. ChangePasswordAsync.</summary>
public class ApiResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    public static ApiResult Ok() => new() { Success = true };
    public static ApiResult Fail(string error) => new() { Success = false, Error = error };
}
