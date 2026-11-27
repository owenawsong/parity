using System.IO.Pipes;
using System.Reflection;
using WPods.Core;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
string pipeName = "ParityOnboardEqTest-" + Guid.NewGuid();
using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
    PipeOptions.Asynchronous, 4096, 4096);
await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
client.ReadMode = PipeTransmissionMode.Message;

await using var session = new AapSession(0, client, new SettingsStore(), productId: 0);
var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
session.Changed += change =>
{
    if ((change & DeviceChange.Connection) != 0 && session.State.Status == SessionStatus.Ready)
    {
        ready.TrySetResult();
    }
};
session.Start();
Expect((await ReadPacket()).SequenceEqual(Aap.Handshake), "The session did not start with its handshake.");
await Report(Aap.ToneVolume(50));
Expect((await ReadPacket()).SequenceEqual(Aap.FeatureFlags), "The session did not request features.");
Expect((await ReadPacket()).SequenceEqual(Aap.RequestNotifications(true)), "The session did not request notifications.");
await ready.Task.WaitAsync(timeout.Token);

var reported = new OnboardEq(true, 52, 64, 57);
var preview = reported with { Low = 67 };
var obsolete = preview with { Low = 32 };
var newest = preview with { High = 78 };
var nextMode = newest with { Enabled = false };

var malformed = Aap.CustomEqPacket(reported);
malformed[11] = 101;
await Report(malformed);
Expect(!await Write(preview), "An invalid settings report enabled EQ.");
await Report(Aap.CustomEqPacket(reported));
Expect(await Write(preview), "The first preview was refused.");
await ExpectCurve(preview);
Expect(await Write(preview), "The matching commit did not reuse the preview.");
Expect(await Write(newest), "The next curve was refused.");
await ExpectCurve(newest);

// The earbuds can repeat their original snapshot without producing a settings delta.
await Report(Aap.CustomEqPacket(reported));
Expect(await Write(newest), "A conflicting report should allow another write.");
await ExpectCurve(newest);
await Report(Aap.CustomEqPacket(newest));
Expect(await Write(newest), "A confirming report should preserve preview reuse.");

// Hold the same transport gate used by other controls while a newer request supersedes the preview.
var gate = (SemaphoreSlim)typeof(AapSession).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
await gate.WaitAsync(timeout.Token);
using var stale = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
var superseded = session.WriteCustomEqAsync(obsolete, stale.Token).AsTask();
Expect(!superseded.IsCompleted, "The preview did not wait for the busy transport.");
stale.Cancel();
gate.Release();
try
{
    await superseded;
    throw new InvalidOperationException("An obsolete preview survived cancellation.");
}
catch (OperationCanceledException) when (stale.IsCancellationRequested)
{
}

Expect(await Write(nextMode), "The obsolete preview blocked the final write.");
await ExpectCurve(nextMode);
await server.WriteAsync(new byte[] { ServicePipes.AapEnded, 0, 0, 0, 0 }, timeout.Token);
await session.Completion.WaitAsync(timeout.Token);
Expect(!await Write(nextMode), "A closed channel reused a successful write.");
Console.WriteLine("Onboard EQ replay passed report gating, duplicate suppression, superseded previews, and closed channels.");

ValueTask<bool> Write(OnboardEq eq) => session.WriteCustomEqAsync(eq, timeout.Token);

async Task Report(byte[] packet)
{
    // Observe the next packet before continuing so the report has passed through the real parser.
    byte[] marker = [0x04, 0, 0x04, 0, 0xFF, 0];
    var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Observe(ReadOnlySpan<byte> packet)
    {
        if (packet.SequenceEqual(marker)) processed.TrySetResult();
    }
    session.PacketReceived += Observe;
    try
    {
        await server.WriteAsync(Frame(packet), timeout.Token);
        await server.WriteAsync(Frame(marker), timeout.Token);
        await processed.Task.WaitAsync(timeout.Token);
    }
    finally
    {
        session.PacketReceived -= Observe;
    }
}

async Task ExpectCurve(OnboardEq eq)
{
    byte[] packet;
    do
    {
        packet = await ReadPacket();
    }
    while (packet.Length >= 5 && packet[4] is 0x09 or 0x30); // The normal attach settings and key request.
    byte[] expected = [0x04, 0, 0x04, 0, 0x63, 0, 0x05, 0, 0x01, eq.Enabled ? (byte)2 : (byte)1, eq.Low, eq.Mid, eq.High];
    Expect(packet.SequenceEqual(expected), $"Expected {Convert.ToHexString(expected)} but received {Convert.ToHexString(packet)}.");
}

async Task<byte[]> ReadPacket()
{
    byte[] packet = new byte[32];
    int length = await server.ReadAsync(packet, timeout.Token);
    Expect(length > 0 && server.IsMessageComplete, "Expected one complete AAP packet.");
    return packet[..length];
}

static byte[] Frame(byte[] packet) => [ServicePipes.AapPacket, .. packet];

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
