namespace F3M.Client.Identity;

/// <summary>
/// Fires when the server refuses an API call (401/403), so the app can re-check who's signed in.
/// </summary>
/// <remarks>
/// Singleton, since HTTP handlers live in their own scope and can't reach the app's auth state provider.
/// </remarks>
public sealed class AuthStateSignal
{
    public event Action? Refused;

    public void RaiseRefused() => Refused?.Invoke();
}
