#if PAIRSIDE_LIVE
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WPods.Core;

internal sealed class PulseFeed : IDisposable
{
    internal const string Origin = "http://127.0.0.1:48752";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Reading(int Bpm, long MeasuredAtMs);
    private readonly HttpListener _listener = new();
    private Reading? _reading;
    private string _token;

    internal PulseFeed(string token) => _token = token;

    internal static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    internal string OverlayUrl => $"{Origin}/{Volatile.Read(ref _token)}/overlay";
    internal string DataUrl => $"{Origin}/{Volatile.Read(ref _token)}/latest.json";

    internal void Start()
    {
        _listener.Prefixes.Add(Origin + "/");
        _listener.Start();
        _ = AcceptAsync();
    }

    internal void Rotate(string token) => Volatile.Write(ref _token, token);
    internal void Publish(long time, int bpm) => Volatile.Write(ref _reading, new Reading(bpm, time));
    internal void Clear() => Volatile.Write(ref _reading, null);

    private async Task AcceptAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                try { await ReplyAsync(context).ConfigureAwait(false); }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
                finally
                {
                    try { context.Response.Close(); }
                    catch (ObjectDisposedException) { }
                    catch (HttpListenerException) { }
                }
            }
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task ReplyAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'none'";
        string prefix = "/" + Volatile.Read(ref _token) + "/";
        if (request.RemoteEndPoint is not { Address: var remote } || !IPAddress.IsLoopback(remote) ||
            request.UserHostName != "127.0.0.1:48752" ||
            request.HttpMethod != "GET" || request.HasEntityBody ||
            request.Url is not { AbsolutePath: var path } || !path.StartsWith(prefix, StringComparison.Ordinal))
        {
            response.StatusCode = 404;
            return;
        }

        byte[] payload;
        switch (path[prefix.Length..])
        {
            case "latest.json":
                var reading = Volatile.Read(ref _reading);
                if (reading is not null &&
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - reading.MeasuredAtMs is >= 0 and < 8000)
                    payload = JsonSerializer.SerializeToUtf8Bytes(new { bpm = (int?)reading.Bpm, measuredAtMs = (long?)reading.MeasuredAtMs }, Json);
                else
                    payload = """{"bpm":null,"measuredAtMs":null}"""u8.ToArray();
                response.ContentType = "application/json; charset=utf-8";
                break;
            case "overlay":
                payload = Encoding.UTF8.GetBytes(Overlay);
                response.ContentType = "text/html; charset=utf-8";
                break;
            case "overlay.js":
                payload = Encoding.UTF8.GetBytes(OverlayScript);
                response.ContentType = "text/javascript; charset=utf-8";
                break;
            default:
                response.StatusCode = 404;
                return;
        }
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Clear();
        _listener.Close();
    }

    private const string Overlay = """
        <!doctype html>
        <html lang="en">
        <meta charset="utf-8">
        <title>Parity Pulse</title>
        <style>
          body{margin:0;background:transparent;color:#fff;font:600 22px system-ui,sans-serif}
          div#pulse{display:inline-flex;align-items:baseline;gap:8px;padding:8px 15px;border-radius:16px;background:#151517ed}
          div#pulse[hidden]{display:none} b{font-size:38px;font-variant-numeric:tabular-nums} span{color:#ff9db5}
        </style>
        <div id="pulse" hidden><b id="bpm"></b><span>BPM</span></div>
        <script src="overlay.js" defer></script>
        </html>
        """;
    private const string OverlayScript = """
        const pulse = document.getElementById('pulse');
        const bpm = document.getElementById('bpm');
        async function update() {
          try {
            const response = await fetch('latest.json', {signal: AbortSignal.timeout(1500)});
            const reading = response.ok ? await response.json() : null;
            pulse.hidden = !Number.isInteger(reading?.bpm);
            if (!pulse.hidden) bpm.textContent = reading.bpm;
          } catch { pulse.hidden = true; }
          setTimeout(update, 1000);
        }
        void update();
        """;
}
#endif
