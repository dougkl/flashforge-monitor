using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace FlashforgeMonitor;

/// <summary>Sends print events to webhooks: plain JSON, ntfy, or Discord.</summary>
public sealed class Notifier(
    IHttpClientFactory http,
    IOptionsMonitor<NotificationOptions> options,
    IOptionsMonitor<PrinterOptions> printer,
    ILogger<Notifier> log)
{
    public static readonly string[] AllEvents = ["started", "finished", "cancelled", "paused", "resumed", "offline", "online", "test"];

    public async Task SendAsync(string evt, string title, string message, CancellationToken ct)
    {
        log.LogInformation("Event {Event}: {Message}", evt, message);
        foreach (var hook in options.CurrentValue.Webhooks)
        {
            if (string.IsNullOrWhiteSpace(hook.Url)) continue;
            if (evt != "test" && hook.Events.Count > 0 && !hook.Events.Contains(evt, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                var client = http.CreateClient("notify");
                HttpResponseMessage resp = hook.Type.ToLowerInvariant() switch
                {
                    "ntfy" => await client.SendAsync(Ntfy(hook.Url, evt, title, message), ct),
                    "discord" => await client.PostAsJsonAsync(hook.Url, new { content = $"**{title}**\n{message}" }, ct),
                    _ => await client.PostAsJsonAsync(hook.Url, new
                    {
                        @event = evt,
                        title,
                        message,
                        printer = printer.CurrentValue.Host,
                        at = DateTimeOffset.Now,
                    }, ct),
                };
                if (!resp.IsSuccessStatusCode)
                    log.LogWarning("Webhook {Type} returned {Status}", hook.Type, (int)resp.StatusCode);
                resp.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("Webhook {Type} failed: {Message}", hook.Type, ex.Message);
            }
        }
    }

    private static HttpRequestMessage Ntfy(string url, string evt, string title, string message)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message) };
        req.Headers.TryAddWithoutValidation("Title", title);
        req.Headers.TryAddWithoutValidation("Tags", evt switch
        {
            "finished" => "white_check_mark,printer",
            "cancelled" => "x,printer",
            "offline" => "warning,printer",
            _ => "printer",
        });
        if (evt is "finished" or "offline" or "cancelled") req.Headers.TryAddWithoutValidation("Priority", "high");
        return req;
    }
}
