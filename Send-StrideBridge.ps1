param(
    [Parameter(Mandatory = $true)][string]$Operation,
    [string]$ArgumentsJson = '{}',
    [int]$TimeoutMs = 15000
)

$ErrorActionPreference = 'Stop'

$requestId = [Guid]::NewGuid().ToString('N')
$payload = "{`"requestId`":`"$requestId`",`"operation`":`"$Operation`",`"arguments`":$ArgumentsJson}`n"

$bytes = [Text.Encoding]::UTF8.GetBytes($payload)

$client = [Net.Sockets.TcpClient]::new('127.0.0.1', 9877)
$client.ReceiveTimeout = $TimeoutMs
try {
    $stream = $client.GetStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()

    $buffer = [IO.MemoryStream]::new()
    $chunk = [byte[]]::new(4096)
    while ($true) {
        $line = $stream.Read($chunk, 0, $chunk.Length)
        if ($line -le 0) { break }
        $buffer.Write($chunk, 0, $line)
        if ([Text.Encoding]::UTF8.GetString($buffer.ToArray()) -match "`n") {
            $stream.Read($chunk, 0, 1) | Out-Null # consume trailing newline lookahead
            break
        }
    }
    $text = [Text.Encoding]::UTF8.GetString($buffer.ToArray()).Trim()
    if (-not $text) { throw 'No response from bridge.' }
    Write-Output $text
}
finally {
    $client.Dispose()
}
