using System.Buffers.Binary;
using System.Runtime.InteropServices;
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
short[] mono = [short.MinValue, -1, 0, 1, short.MaxValue];
Span<short> stereo = stackalloc short[mono.Length * 2];
MicSampleOutput.CopyStereo(mono, stereo);
for (int index = 0; index < mono.Length; index++)
    if (stereo[index * 2] != mono[index] || stereo[index * 2 + 1] != mono[index])
        throw new InvalidOperationException("Native sample playback changed the left/right channel balance.");
Span<float> floatStereo = stackalloc float[mono.Length * 2];
MicSampleOutput.CopyStereoFloat(mono, floatStereo);
for (int index = 0; index < mono.Length; index++)
    if (floatStereo[index * 2] != mono[index] / 32768f || floatStereo[index * 2 + 1] != floatStereo[index * 2])
        throw new InvalidOperationException("Native float playback changed the sample or channel balance.");
try
{
    MicSampleOutput.Play([], 0, new CancellationToken(canceled: true), _ => { });
    throw new InvalidOperationException("Cancelled playback should stop before opening Windows audio.");
}
catch (OperationCanceledException)
{
}
if (OperatingSystem.IsWindows())
{
    const string missingOutput = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
    try
    {
        MicSampleOutput.Play(MicCheck.Format(new short[48_000]).Wave, 0, CancellationToken.None,
            _ => throw new InvalidOperationException("A missing output must never fall back to Windows default."), missingOutput);
        throw new InvalidOperationException("A missing output must report a playback error.");
    }
    catch (COMException error) when ((uint)error.HResult == 0x80070490)
    {
        Console.WriteLine("A missing sample output fails without opening Windows default.");
    }
}
Console.WriteLine("Native sample playback passed symmetric stereo and early cancellation.");

var owner = new MicSampleOutput.PlaybackOwner();
var oldPlayback = owner.Begin();
owner.Cancel(); // A new recording clears the sample and stops its current player.
if (!oldPlayback.IsCancellationRequested || owner.IsCurrent(oldPlayback))
    throw new InvalidOperationException("The next recording kept ownership of the old replay.");
var newPlayback = owner.Begin();
if (owner.Complete(oldPlayback) || !owner.IsCurrent(newPlayback))
    throw new InvalidOperationException("A cancelled replay interrupted playback of the new sample.");
if (!owner.Complete(newPlayback)) throw new InvalidOperationException("The new replay did not complete.");
owner.Cancel(); // A completed worker may already have disposed its token.
Console.WriteLine("Recording during replay releases the old request and leaves the new sample playable.");

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
