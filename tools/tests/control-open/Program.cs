using System.Buffers.Binary;
using System.IO.Pipes;
using WPods.Core;

const ulong sampleAddress = 0x112233445566;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await CheckReplyAsync([2, 0, 0, 0, 0], 0);
await CheckReplyAsync([2, 0x8f, 4, 0, 0], 1167);
await CheckReplyAsync([1, 0, 0, 0, 0], 13);
await CheckReplyAsync([2, 0, 0], 13);
await CheckReplyAsync(null, 109);

var (server, client) = await CreatePairAsync();
await using (server)
await using (client)
{
    using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    var open = ServicePipes.ExchangeAapOpenAsync(client, sampleAddress, cancelled.Token);
    await CheckHelloAsync(server);
    Require(!open.IsCompleted, "The transfer must yield while the service is still opening the channel.");
    cancelled.Cancel();
    try
    {
        await open.AsTask().WaitAsync(timeout.Token);
        throw new InvalidOperationException("The pending reply was accepted after cancellation.");
    }
    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
}
await CheckReplyAsync([2, 0, 0, 0, 0], 0);
Console.WriteLine("Control open passed verified framing, disconnect, refused and malformed replies, cancellation and a fresh attempt.");

async Task<(NamedPipeServerStream, NamedPipeClientStream)> CreatePairAsync()
{
    string name = "parity-control-open-test-" + Guid.NewGuid().ToString("N");
    var service = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
    var desktop = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await Task.WhenAll(service.WaitForConnectionAsync(timeout.Token), desktop.ConnectAsync(timeout.Token));
        desktop.ReadMode = PipeTransmissionMode.Message;
        return (service, desktop);
    }
    catch
    {
        service.Dispose();
        desktop.Dispose();
        throw;
    }
}

async Task CheckHelloAsync(NamedPipeServerStream service)
{
    byte[] hello = new byte[16];
    int count = await service.ReadAsync(hello, timeout.Token);
    Require(count == 10 && service.IsMessageComplete && hello[0] == 'A' && hello[1] == 'P' &&
        BinaryPrimitives.ReadUInt64LittleEndian(hello.AsSpan(2)) == sampleAddress, "Invalid channel-open frame.");
}

async Task CheckReplyAsync(byte[]? reply, int expected)
{
    var (service, desktop) = await CreatePairAsync();
    await using (service)
    await using (desktop)
    {
        var open = ServicePipes.ExchangeAapOpenAsync(desktop, sampleAddress, timeout.Token);
        await CheckHelloAsync(service);
        if (reply is null) service.Dispose();
        else await service.WriteAsync(reply, timeout.Token);
        Require(await open == expected, $"Expected Win32 result {expected}.");
    }
}

static void Require(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
}

namespace WPods.Core
{
    // The tests open their own pipe, so production service ownership and its
    // diagnostic logging aren't exercised.
    internal static class Native { internal const int ErrorSuccess = 0; }
    internal static class Log { internal static void Warn(string category, string message) { } }
}
