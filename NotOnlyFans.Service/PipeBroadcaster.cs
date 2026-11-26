using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NotOnlyFans.Contracts;

namespace NotOnlyFans.Service;

/// <summary>
/// Accepts window-app connections on the sensor pipe and sends every poll to all of them.
/// The service runs as administrator, the window app does not, so the pipe explicitly allows
/// signed-in users to read (see <see cref="ServicePipe"/>).
/// </summary>
internal sealed class PipeBroadcaster
{
    private readonly object _lock = new();
    private readonly List<Client> _clients = [];
    private byte[]? _catalogLine;
    private byte[]? _valuesLine;
    private byte[]? _controlLine;
    private byte[]? _colorsLine;
    private byte[]? _ocLine;

    /// <summary>Fan control settings / safety state changed: tell every window app (and remember for new ones).</summary>
    public void PublishControl(byte[] controlLine)
    {
        lock (_lock)
        {
            _controlLine = controlLine;
            foreach (var client in _clients)
            {
                client.Send(controlLine);
            }
        }
    }

    /// <summary>Lights / their settings changed: tell every window app (and remember for new ones).</summary>
    public void PublishColors(byte[] colorsLine)
    {
        lock (_lock)
        {
            _colorsLine = colorsLine;
            foreach (var client in _clients)
            {
                client.Send(colorsLine);
            }
        }
    }

    /// <summary>Overclocking state changed: tell every window app (and remember for new ones).</summary>
    public void PublishOc(byte[] ocLine)
    {
        lock (_lock)
        {
            _ocLine = ocLine;
            foreach (var client in _clients)
            {
                client.Send(ocLine);
            }
        }
    }

    public event Action<int>? ClientCountChanged;

    public void Publish(byte[]? catalogLine, byte[] valuesLine)
    {
        lock (_lock)
        {
            if (catalogLine is not null)
            {
                _catalogLine = catalogLine;
            }
            _valuesLine = valuesLine;

            foreach (var client in _clients)
            {
                if (catalogLine is not null)
                {
                    client.Send(catalogLine);
                }
                client.Send(valuesLine);
            }
        }
    }

    /// <summary>Window apps (one per signed-in user, plus a few spare).</summary>
    private const int MaxClients = 16;

    public Task AcceptLoopAsync(ILogger log, CancellationToken ct) =>
        ServicePipe.AcceptLoopAsync(SensorPipe.Name, PipeDirection.Out, WellKnownSidType.AuthenticatedUserSid,
            MaxClients, ServeAsync, log, ct);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var client = new Client(pipe);
        int count;
        lock (_lock)
        {
            // A new client gets the current picture straight away: no waiting for the next poll.
            foreach (var line in new[] { _catalogLine, _controlLine, _colorsLine, _ocLine, _valuesLine })
            {
                if (line is not null)
                {
                    client.Send(line);
                }
            }
            _clients.Add(client);
            count = _clients.Count;
        }
        ClientCountChanged?.Invoke(count);
        try
        {
            await client.RunAsync(ct);
        }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(client);
                count = _clients.Count;
            }
            ClientCountChanged?.Invoke(count);
        }
    }

    /// <summary>One connected window app. Lines are queued and written in order on its own task.</summary>
    private sealed class Client(NamedPipeServerStream pipe)
    {
        // A healthy client reads each line within milliseconds. If 16 lines pile up it is stuck: drop it.
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public void Send(byte[] line)
        {
            if (!_queue.Writer.TryWrite(line))
            {
                _queue.Writer.TryComplete();
            }
        }

        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var line in _queue.Reader.ReadAllAsync(ct))
                {
                    await pipe.WriteAsync(line, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Window app closed or service stopping.
            }
            finally
            {
                _queue.Writer.TryComplete();
                await pipe.DisposeAsync();
            }
        }
    }
}
