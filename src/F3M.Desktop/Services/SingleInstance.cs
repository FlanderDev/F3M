using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using F3M.Desktop.Core;

namespace F3M.Desktop.Services;

/// <summary>
/// One running app per user. The running instance listens on a named pipe (Windows) or a Unix socket (Linux, mode 0600).
/// A second launch sends its message as one line, waits for "OK", and exits (plan 9.5).
/// </summary>
public static class SingleInstance
{
    private const int MaxLineLength = 4096;

    /// <summary>Sends a line to the running instance. False when no instance is listening.</summary>
    public static bool TrySend(string message, AppPaths paths)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut);
                pipe.Connect(500);
                return Exchange(pipe, message);
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath(paths)));
            using var stream = new NetworkStream(socket, ownsSocket: false);
            return Exchange(stream, message);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Accepts lines from second launches until cancelled. Each message is handed to <paramref name="onMessage"/>.</summary>
    public static Task ListenAsync(AppPaths paths, Action<string> onMessage, CancellationToken ct) =>
        OperatingSystem.IsWindows()
            ? ListenPipesAsync(onMessage, ct)
            : ListenSocketAsync(SocketPath(paths), onMessage, ct);

    private static string PipeName()
    {
        var identity = Environment.UserName + "@" + Environment.UserDomainName;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"F3M-{hash}";
    }

    private static string SocketPath(AppPaths paths)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var folder = string.IsNullOrWhiteSpace(runtime) ? paths.Root : runtime;
        return Path.Combine(folder, "f3m.sock");
    }

    [SupportedOSPlatform("windows")]
    private static async Task ListenPipesAsync(Action<string> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                PipeName(), PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(ct);
            await HandleAsync(server, onMessage, ct);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task ListenSocketAsync(string path, Action<string> onMessage, CancellationToken ct)
    {
        // A socket file left by a crash. A live instance would have answered TrySend first.
        if (File.Exists(path)) File.Delete(path);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(8);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(ct);
            _ = HandleSocketAsync(client, onMessage, ct);
        }
    }

    private static async Task HandleSocketAsync(Socket client, Action<string> onMessage, CancellationToken ct)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            await HandleAsync(stream, onMessage, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            AppLog.Error("A second launch could not be handled", ex);
        }
    }

    private static async Task HandleAsync(Stream stream, Action<string> onMessage, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct);
        if (!string.IsNullOrWhiteSpace(line) && line.Length <= MaxLineLength)
            onMessage(line.Trim());

        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("OK");
    }

    private static bool Exchange(Stream stream, string message)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        writer.WriteLine(message);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadLine() == "OK";
    }
}
