using System.Net.Sockets;
using System.Text;

namespace TrMadenci.Protocols.Stratum;

public sealed class StratumConnection : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public bool IsConnected => _client.Connected;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (IsConnected)
            throw new InvalidOperationException("The Stratum connection is already open.");

        _client.NoDelay = true;
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        await _client.ConnectAsync(host, port, cancellationToken);

        var stream = _client.GetStream();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(stream, encoding, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public async Task SendAsync(StratumRequest request, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("The Stratum connection is not open.");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(request.ToJsonLine().TrimEnd('\n').AsMemory(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<StratumMessage> ReadAsync(CancellationToken cancellationToken)
    {
        var reader = _reader ?? throw new InvalidOperationException("The Stratum connection is not open.");
        var line = await reader.ReadLineAsync(cancellationToken);

        if (line is null)
            throw new EndOfStreamException("The pool closed the Stratum connection.");

        return StratumMessage.Parse(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync();
        _reader?.Dispose();
        _client.Dispose();
        _writeLock.Dispose();
    }
}
