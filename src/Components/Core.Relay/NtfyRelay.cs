using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HandloomFactory.Core.Relay;

/// <param name="Id">The relay's message id; pass the last one seen as <c>since</c> to get only newer messages.</param>
public sealed record RelayMessage(string Id, DateTimeOffset Time, string Text);

/// <summary>
/// A store-and-forward mailbox between phones. The relay only sees encrypted text, the topic name and IP addresses,
/// and keeps messages for a limited time (ntfy.sh: about 12 hours).
/// </summary>
public interface IRelay
{
    /// <returns>The relay's id for the new message.</returns>
    Task<string> PublishAsync(string topic, string text, CancellationToken cancellationToken = default);

    /// <param name="since">The last message id already seen, or null for everything still kept.</param>
    Task<IReadOnlyList<RelayMessage>> FetchAsync(string topic, string? since, CancellationToken cancellationToken = default);

    /// <summary>A human-readable push notification (ntfy app on the owner's phone). Plain text, not encrypted.</summary>
    Task NotifyAsync(string topic, string title, string message, CancellationToken cancellationToken = default);
}

/// <summary>Creates a relay client for a server URL.</summary>
public interface IRelayFactory
{
    IRelay For(string serverUrl);
}

public sealed class NtfyRelayFactory(HttpClient http, string userAgent) : IRelayFactory
{
    public const string DefaultServer = "https://ntfy.sh";

    public IRelay For(string serverUrl) => new NtfyRelay(http, new Uri(serverUrl), userAgent);
}

/// <summary>
/// Sends and fetches short text messages through an ntfy server (the free public ntfy.sh or a self-hosted one).
/// No account is needed: anyone who knows a topic name can read and write it, so topics are unguessable and sync
/// messages are encrypted and signed before they are sent. Copied from Travel Vault.
/// </summary>
public sealed partial class NtfyRelay(HttpClient http, Uri server, string userAgent) : IRelay
{
    /// <summary>ntfy.sh turns larger messages into attachments; stay below this.</summary>
    public const int MaxMessageLength = 3800;

    /// <exception cref="HttpRequestException">No connection, or the server refused (e.g. daily limit reached).</exception>
    public async Task<string> PublishAsync(string topic, string text, CancellationToken cancellationToken = default)
    {
        CheckTopic(topic);
        if (text.Length > MaxMessageLength)
        {
            throw new ArgumentException($"Messages must be at most {MaxMessageLength} characters.", nameof(text));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TopicUrl(topic)) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
        AddHeaders(request);
        request.Headers.Add("X-Firebase", "no"); // only our app reads these: no push through Google
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id").GetString() ?? "";
    }

    /// <exception cref="HttpRequestException">No connection or server error.</exception>
    public async Task NotifyAsync(string topic, string title, string message, CancellationToken cancellationToken = default)
    {
        CheckTopic(topic);
        using var request = new HttpRequestMessage(HttpMethod.Post, TopicUrl(topic)) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
        AddHeaders(request);
        request.Headers.Add("Title", Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(title)).Replace('?', ' ')); // headers are ASCII
        request.Headers.Add("Tags", "thread");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <param name="since">The last message id already seen, or null for everything the server still keeps.</param>
    /// <exception cref="HttpRequestException">No connection or server error.</exception>
    public async Task<IReadOnlyList<RelayMessage>> FetchAsync(string topic, string? since, CancellationToken cancellationToken = default)
    {
        CheckTopic(topic);
        var sinceValue = since is not null && IdRegex().IsMatch(since) ? since : "all";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TopicUrl(topic) + $"/json?poll=1&since={sinceValue}"));
        AddHeaders(request);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>Reads ntfy's one-JSON-object-per-line format, keeping only real messages.</summary>
    internal static IReadOnlyList<RelayMessage> Parse(string body)
    {
        var messages = new List<RelayMessage>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("event", out var ev) && ev.GetString() == "message"
                    && root.TryGetProperty("id", out var id) && root.TryGetProperty("message", out var message))
                {
                    var time = root.TryGetProperty("time", out var t) && t.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.MinValue;
                    messages.Add(new RelayMessage(id.GetString() ?? "", time, message.GetString() ?? ""));
                }
            }
            catch (JsonException)
            {
                // Skip a broken line; the rest is still usable.
            }
        }

        return messages;
    }

    private string TopicUrl(string topic) => server.ToString().TrimEnd('/') + "/" + topic;

    private void AddHeaders(HttpRequestMessage request) => request.Headers.UserAgent.Add(new ProductInfoHeaderValue(userAgent, null));

    private static void CheckTopic(string topic)
    {
        if (!TopicRegex().IsMatch(topic))
        {
            throw new ArgumentException("Topic names may only contain letters, digits, '-' and '_'.", nameof(topic));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex TopicRegex();

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex IdRegex();
}
