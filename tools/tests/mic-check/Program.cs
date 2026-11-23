using System.Buffers.Binary;
using WPods.Audio;

// A press to stop or a page-leave cancellation may reach the capture worker before it starts.
// Use an intentionally nonexistent endpoint. Both checks must return before opening Windows audio.
const string missing = "microphone-test-fixture-no-endpoint";
try
{
    MicCheck.Capture(missing, verifyCallsDefault: true, CancellationToken.None, finished: static () => true);
    throw new InvalidOperationException("The quick stop should return the short-sample result.");
}
catch (MicCheck.ShortSampleException)
{
}
try
{
    MicCheck.Capture(missing, verifyCallsDefault: false, new CancellationToken(canceled: true));
    throw new InvalidOperationException("The capture worker ignored a cancellation before start.");
}
catch (OperationCanceledException)
{
}

short[] pcm = new short[48_000];
Expect("silent");
pcm[0] = 12_000;
Expect("quiet");
Array.Fill(pcm, (short)9000);
Expect("ready");
Array.Fill(pcm, short.MinValue, 0, 60);
Expect("clipped");
Console.WriteLine("Microphone check passed early stop, early cancellation, and captured sample classification.");

void Expect(string level)
{
    MicCheck.Recording check = MicCheck.Format(pcm);
    ReadOnlySpan<byte> wave = check.Wave;
    if (check.Level != level || check.Envelope.Length != 32 || !wave[..4].SequenceEqual("RIFF"u8)
        || BinaryPrimitives.ReadInt32LittleEndian(wave[24..]) != 48_000
        || BinaryPrimitives.ReadInt32LittleEndian(wave[40..]) != pcm.Length * sizeof(short))
    {
        throw new InvalidOperationException($"Unexpected {check.Level} result or invalid WAV for {level} sample.");
    }
}
