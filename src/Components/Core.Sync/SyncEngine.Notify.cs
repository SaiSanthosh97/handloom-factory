using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Relay;
using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.Core.Sync;

/// <summary>
/// Optional push notifications through ntfy (TD-05). The owner turns them on in Settings: a random topic is created and
/// opened in the free ntfy app on the owner's phone. These texts are plain (not encrypted), which the Settings screen
/// says; sync data itself stays encrypted.
/// </summary>
public sealed partial class SyncEngine
{
    /// <summary>"Ramesh submitted 3 sarees today" (the app passes translated text).</summary>
    public Func<string, int, string> SubmittedText { get; set; } = (name, count) => $"{name} submitted {count} sarees today";

    /// <summary>"Weekly payments ready: ₹42,500 for 8 workers".</summary>
    public Func<Money, int, string> WeeklyText { get; set; } = (total, workers) => $"Weekly payments ready: {total} for {workers} workers";

    /// <summary>The weekly notice is sent on Saturday from this hour (factory time), once per week.</summary>
    public const int WeeklyNoticeHour = 18;

    public Task<string?> NotifyTopicAsync(CancellationToken cancellationToken = default) => store.GetSettingAsync(SyncSettings.NotifyTopic, cancellationToken);

    /// <summary>
    /// Owner: turn notifications on (a new random topic) or off (null). Linked workers' phones are told, so they send
    /// "submitted" notices to this topic (or stop).
    /// </summary>
    /// <returns>The topic now in use, or null when off.</returns>
    public async Task<string?> SetNotificationsAsync(bool on, CancellationToken cancellationToken = default)
    {
        var topic = on ? "hf-" + RandomText.New(24) : null;
        await store.SetSettingAsync(SyncSettings.NotifyTopic, topic, cancellationToken);
        var shared = OutboxStore.Item("", RecordTypes.Settings, new SharedSettings(topic));
        await store.AddOutboxAsync(
            (await store.GetChannelsAsync(cancellationToken)).Where(c => c.IsLinked).Select(c => shared with { Channel = c.EmployeeId }),
            cancellationToken);
        return topic;
    }

    /// <summary>A test notification from Settings.</summary>
    public async Task<bool> SendTestNotificationAsync(string message, CancellationToken cancellationToken = default)
    {
        if (await NotifyTopicAsync(cancellationToken) is not { } topic)
        {
            return false;
        }

        var factory = await data.GetFactoryAsync(cancellationToken);
        try
        {
            await relays.For(DefaultRelay).NotifyAsync(topic, factory?.Name ?? "Handloom Factory", message, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Owner's phone, Saturday from 18:00 (factory time), once per week: "Weekly payments ready" with the total still to
    /// pay for the week. Runs from the background job.
    /// </summary>
    public async Task<bool> SendWeeklyNoticeIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (await store.GetRoleAsync(cancellationToken) != PhoneRole.Owner || await NotifyTopicAsync(cancellationToken) is not { } topic
            || await data.GetFactoryAsync(cancellationToken) is not { } factory)
        {
            return false;
        }

        var now = new FactoryTime(clock, FactoryTime.FindZone(factory.TimeZoneId)).Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        var week = Periods.PayWeek(today);
        if (today != week.End || now.Hour < WeeklyNoticeHour
            || await store.GetSettingAsync("notify.weeklySent", cancellationToken) == week.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
        {
            return false;
        }

        var entries = await data.GetEntriesAsync(new DateRange(DateOnly.MinValue, week.End), null, cancellationToken);
        var payments = await data.GetPaymentsAsync(null, cancellationToken);
        var rates = new RateBook(await data.GetRateChangesAsync(cancellationToken));
        var total = Money.Zero;
        var workers = 0;
        foreach (var worker in (await data.GetEmployeesAsync(cancellationToken)).Where(e => e.IsWorker))
        {
            var line = PaymentCalculator.ForWeek(worker.Id, week, entries, payments, rates);
            if (line.Status == PaymentStatus.Pending)
            {
                total += line.Total;
                workers++;
            }
        }

        if (workers == 0)
        {
            return false;
        }

        try
        {
            await relays.For(DefaultRelay).NotifyAsync(topic, factory.Name, WeeklyText(total, workers), cancellationToken);
            await store.SetSettingAsync("notify.weeklySent", week.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false; // tried again on the next run
        }
    }

    /// <summary>Worker's phone, after today's entry reached the relay: tell the owner's ntfy app (if turned on).</summary>
    private async Task NotifySubmittedAsync(IRelay relay, Channel channel, IEnumerable<SyncRecord> sent, CancellationToken cancellationToken)
    {
        if (await NotifyTopicAsync(cancellationToken) is not { } topic)
        {
            return;
        }

        var entry = sent.Where(r => r.Type == RecordTypes.Entry).Select(Read<ProductionEntry>).LastOrDefault(e => e is not null);
        var worker = (await data.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == channel.EmployeeId);
        var factory = await data.GetFactoryAsync(cancellationToken);
        if (entry is null || worker is null)
        {
            return;
        }

        try
        {
            await relay.NotifyAsync(topic, factory?.Name ?? "Handloom Factory", SubmittedText(worker.FullName, entry.SareeCount), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A notice is a convenience: the entry itself was sent.
        }
    }

    /// <summary>Notifications go through the same relay server as sync (ntfy.sh by default).</summary>
    private string DefaultRelay => NtfyRelayFactory.DefaultServer;
}
