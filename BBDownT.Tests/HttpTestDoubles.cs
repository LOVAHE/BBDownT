using System.Net;

namespace BBDownT.Tests;

internal sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    internal DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => send(request, cancellationToken);
}

internal sealed class MemoryContent(Func<Stream, CancellationToken, Task> write) : HttpContent
{
    internal bool Disposed { get; private set; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => write(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => write(stream, cancellationToken);
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
