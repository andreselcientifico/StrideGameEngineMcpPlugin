using System.Net.Sockets;
using System.Text;
using System.Text.Json;

public sealed class StrideBridgeClient
{
    private const string Host = "127.0.0.1";
    private const int Port = 9877;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonElement> SendAsync(
        string operation,
        object arguments,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(Host, Port, cancellationToken);

        await using NetworkStream stream = client.GetStream();
        var request = new
        {
            requestId = Guid.NewGuid().ToString("N"),
            operation,
            arguments
        };

        byte[] requestBytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, JsonOptions) + "\n");
        await stream.WriteAsync(requestBytes, cancellationToken);

        using var reader = new StreamReader(stream, leaveOpen: true);
        string? line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
            throw new IOException("Stride closed the bridge connection without a response.");

        BridgeResponse? response = JsonSerializer.Deserialize<BridgeResponse>(line, JsonOptions);
        if (response is null)
            throw new InvalidDataException("Stride returned an invalid bridge response.");
        if (!string.Equals(response.RequestId, request.requestId, StringComparison.Ordinal))
            throw new InvalidDataException("Stride returned a response for a different bridge request.");
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "The Stride bridge request failed.");
        if (response.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidDataException("Stride returned an empty bridge response.");

        return response.Data.Clone();
    }

    private sealed record BridgeResponse(string RequestId, bool Success, JsonElement Data, string? Error);
}
