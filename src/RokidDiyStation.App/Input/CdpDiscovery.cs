// SPDX-License-Identifier: MIT
//
// Finding a Chromium browser that we can drive.
//
// The browser has to be reachable over the DevTools protocol, which
// means it has to have been started with --remote-debugging-port.
// We do NOT launch the user's existing browser - that would mean
// closing it and reopening it with the flag, which is disruptive and
// loses their session.
//
// So there are two supported paths:
//
//  1. The user launches their browser with the flag themselves (a
//     shortcut, a .bat, a separate profile).  We find it by asking
//     the local DevTools HTTP endpoint for its target list.
//
//  2. We launch a dedicated browser instance ourselves, with its own
//     --user-data-dir, and the URL the user wants to project.  This
//     is the path the product should take: it does not touch the
//     user's own browsing at all.
//
// The HTTP endpoint is plain http on 127.0.0.1 - no auth, no TLS,
// and no network traffic off the machine.
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace RokidDiyStation.App.Input;

/// <summary>
/// One debuggable page, as reported by the DevTools HTTP endpoint.
/// </summary>
public sealed class CdpTarget
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("webSocketDebuggerUrl")] public string WebSocketDebuggerUrl { get; set; } = "";
    [JsonPropertyName("devtoolsFrontendUrl")] public string DevtoolsFrontendUrl { get; set; } = "";
}

/// <summary>
/// Discovers Chromium browsers that expose a DevTools endpoint.
/// </summary>
public static class CdpDiscovery
{
    /// <summary>The default port Chrome uses for the DevTools endpoint.</summary>
    public const int DefaultPort = 9222;

    /// <summary>
    /// List the debuggable pages on a DevTools endpoint.
    /// </summary>
    /// <returns>The targets, or an empty list when the endpoint is not there.</returns>
    public static async Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(
        int port = DefaultPort, CancellationToken token = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var url = $"http://127.0.0.1:{port}/json/list";
            var targets = await client.GetFromJsonAsync<List<CdpTarget>>(url, token).ConfigureAwait(false);
            return targets ?? new List<CdpTarget>();
        }
        catch (Exception ex) when (ex is HttpRequestException
                                      or TaskCanceledException
                                      or JsonException
                                      or UriFormatException
                                      or InvalidOperationException)
        {
            // No browser on that port, or it is not a DevTools
            // endpoint.  Either way, there is nothing to drive.
            return Array.Empty<CdpTarget>();
        }
    }

    /// <summary>
    /// The page targets only - the ones a user would call "a tab".
    /// </summary>
    /// <remarks>
    /// The endpoint also lists the browser's own internal targets
    /// ("background_page", "service_worker", "other"), which are not
    /// things the user projects.
    /// </remarks>
    public static async Task<IReadOnlyList<CdpTarget>> ListPagesAsync(
        int port = DefaultPort, CancellationToken token = default)
    {
        var all = await ListTargetsAsync(port, token).ConfigureAwait(false);
        return all.Where(t => t.Type == "page" && !string.IsNullOrEmpty(t.WebSocketDebuggerUrl)).ToList();
    }
}
