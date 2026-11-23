using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Security;

namespace HandloomFactory.Core.Sync.Tests;

public class LinkingTests
{
    [Fact]
    public async Task Worker_phone_links_and_gets_only_its_own_data()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);

        Assert.Equal(PhoneRole.Worker, await ramesh.Sync.RoleAsync());
        Assert.Equal(PhoneRole.Owner, await f.Owner.Sync.RoleAsync());
        Assert.Equal("Sri Lakshmi Handlooms", (await ramesh.Inner.GetFactoryAsync())?.Name);
        var people = await ramesh.Inner.GetEmployeesAsync();
        Assert.Equal(["Ramesh"], people.Select(e => e.FullName)); // not Suma, not the owner
        Assert.Equal(Money.FromRupees(500), Assert.Single(await ramesh.Inner.GetRateChangesAsync()).Rate);

        // The worker logs in with their PIN and sees their dashboard.
        Assert.True(ramesh.Session.IsSignedIn);
        Assert.False(ramesh.Session.IsAdmin);
        var dashboard = (await ramesh.Worker.DashboardAsync()).Value!;
        Assert.Equal("Sri Lakshmi Handlooms", dashboard.FactoryName);
        Assert.Contains(await f.Owner.Inner.GetAuditAsync(20), a => a.Action == AuditActions.PhoneLinked);
    }

    [Fact]
    public async Task Expired_or_reused_invites_are_refused()
    {
        using var f = await new Factory().SetUpAsync();
        var invite = await f.Owner.Sync.CreateInviteAsync(f.Ramesh.Id, Factory.Relay);

        f.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(ErrorCode.InviteExpired, (await f.NewPhone().Sync.JoinAsync(invite.ToText())).Error?.Code);
        Assert.Equal(ErrorCode.InviteInvalid, (await f.NewPhone().Sync.JoinAsync("hello")).Error?.Code);

        // A fresh invite can be used once: a second phone with the same QR is never confirmed.
        invite = await f.Owner.Sync.CreateInviteAsync(f.Ramesh.Id, Factory.Relay);
        var first = f.NewPhone();
        var second = f.NewPhone();
        Assert.True((await first.Sync.JoinAsync(invite.ToText())).Ok);
        Assert.True((await second.Sync.JoinAsync(invite.ToText())).Ok);
        await f.Owner.SyncAsync();
        await first.SyncAsync();
        await second.SyncAsync();
        Assert.Equal(ChannelState.Linked, await first.Sync.WorkerStateAsync());
        Assert.Equal(ChannelState.Joining, await second.Sync.WorkerStateAsync());
        Assert.Null(await second.Inner.GetFactoryAsync());
    }

    [Fact]
    public async Task Invite_text_survives_a_whatsapp_message_around_it()
    {
        using var f = await new Factory().SetUpAsync();
        var invite = await f.Owner.Sync.CreateInviteAsync(f.Ramesh.Id, Factory.Relay);
        var parsed = Invite.TryParse($"Ramesh, open Handloom Factory and paste this: {invite.ToText()} \nThanks");
        Assert.Equal(invite.Topic, parsed?.Topic);
    }

    [Fact]
    public async Task Owner_phone_cannot_join_as_a_worker()
    {
        using var f = await new Factory().SetUpAsync();
        var invite = await f.Owner.Sync.CreateInviteAsync(f.Ramesh.Id, Factory.Relay);
        Assert.Equal(ErrorCode.AlreadySetUp, (await f.Owner.Sync.JoinAsync(invite.ToText())).Error?.Code);
    }
}

public class EntrySyncTests
{
    [Fact]
    public async Task Worker_entry_reaches_the_owner()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);

        Assert.True((await ramesh.Production.SaveOwnTodayAsync(3)).Ok);
        Assert.Equal(1, await ramesh.Sync.PendingAsync());
        await ramesh.SyncAsync();
        Assert.Equal(0, await ramesh.Sync.PendingAsync());
        await f.Owner.SyncAsync();

        var entry = Assert.Single(await f.Owner.EntriesAsync());
        Assert.Equal((f.Ramesh.Id, Factory.Today, 3, EnteredBy.Employee), (entry.EmployeeId, entry.Date, entry.SareeCount, entry.EnteredBy));
        f.Owner.Session.SignIn((await f.Owner.Inner.GetEmployeesAsync()).Single(e => e.Role == Role.Admin));
        Assert.Equal(3, (await f.Owner.Reports.DashboardAsync()).Value!.TodaySarees);
        Assert.Contains(await f.Owner.Inner.GetAuditAsync(10), a => a.NewValue?.Contains("from worker's phone", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Owner_correction_wins_and_reaches_the_worker()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        await ramesh.Production.SaveOwnTodayAsync(3);
        await ramesh.SyncAsync();
        await f.Owner.SyncAsync();

        await f.Owner.Production.SaveDayAsync(Factory.Today, new Dictionary<string, int?> { [f.Ramesh.Id] = 2 });
        await f.Owner.SyncAsync();
        await ramesh.SyncAsync();

        var entry = Assert.Single(await ramesh.EntriesAsync());
        Assert.Equal((2, EnteredBy.Admin), (entry.SareeCount, entry.EnteredBy));
        Assert.True((await ramesh.Worker.DashboardAsync()).Value!.TodayLocked);
        Assert.Equal(ErrorCode.NotAllowed, (await ramesh.Production.SaveOwnTodayAsync(5)).Error?.Code);
    }

    [Fact]
    public async Task Worker_edit_after_owner_correction_is_ignored_by_the_owner()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        await ramesh.Production.SaveOwnTodayAsync(3);       // worker: 3, not synced yet
        await f.Owner.Production.SaveDayAsync(Factory.Today, new Dictionary<string, int?> { [f.Ramesh.Id] = 2 }); // owner: 2

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await ramesh.SyncAsync();   // the worker's 3 arrives at the owner…
        await f.Owner.SyncAsync();  // …and is ignored; the owner's 2 goes out
        await ramesh.SyncAsync();

        Assert.Equal(2, Assert.Single(await f.Owner.EntriesAsync()).SareeCount);
        Assert.Equal(2, Assert.Single(await ramesh.EntriesAsync()).SareeCount);
    }

    [Fact]
    public async Task Owner_added_and_deleted_days_reach_the_worker()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        var sunday = new DateOnly(2026, 11, 22);
        await f.Owner.Production.SaveDayAsync(sunday, new Dictionary<string, int?> { [f.Ramesh.Id] = 1 });
        await f.Owner.SyncAsync();
        await ramesh.SyncAsync();
        Assert.Single(await ramesh.EntriesAsync());

        await f.Owner.Production.SaveDayAsync(sunday, new Dictionary<string, int?> { [f.Ramesh.Id] = null });
        await f.Owner.SyncAsync();
        await ramesh.SyncAsync();
        Assert.Empty(await ramesh.EntriesAsync());
    }

    [Fact]
    public async Task Late_upload_of_yesterday_is_accepted_but_an_entry_for_another_day_is_not()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        await ramesh.Production.SaveOwnTodayAsync(4);   // Monday, phone offline
        f.Relay1.Online = false;
        await ramesh.SyncAsync();
        f.Clock.Advance(TimeSpan.FromHours(20));         // Tuesday morning: internet is back
        f.Relay1.Online = true;
        await ramesh.SyncAsync();
        await f.Owner.SyncAsync();
        Assert.Equal(4, Assert.Single(await f.Owner.EntriesAsync()).SareeCount); // Monday's entry, sent on Tuesday

        // A forged entry (worker says "Sunday" but typed it on Tuesday) is ignored by the owner.
        var forged = new ProductionEntry(ProductionEntry.IdFor(f.Ramesh.Id, new DateOnly(2026, 11, 22)), f.Ramesh.Id, new DateOnly(2026, 11, 22), 9, EnteredBy.Employee, f.Clock.UtcNow, f.Clock.UtcNow);
        await ramesh.SyncStore.AddOutboxAsync([OutboxStore.Item(f.Ramesh.Id, RecordTypes.Entry, forged)]);
        await ramesh.SyncAsync();
        await f.Owner.SyncAsync();
        Assert.Single(await f.Owner.EntriesAsync());
    }
}

public class PrivacyAndSecurityTests
{
    [Fact]
    public async Task Workers_never_receive_each_others_data()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        var suma = await f.LinkAsync(f.Suma);
        await ramesh.Production.SaveOwnTodayAsync(3);
        await suma.Production.SaveOwnTodayAsync(5);
        await f.Owner.Production.SaveDayAsync(new DateOnly(2026, 11, 22), new Dictionary<string, int?> { [f.Ramesh.Id] = 1, [f.Suma.Id] = 2 });
        await f.Owner.Employees.ChangeRateAsync(f.Suma.Id, Money.FromRupees(480), Factory.Today);
        for (var i = 0; i < 2; i++)
        {
            await ramesh.SyncAsync();
            await suma.SyncAsync();
            await f.Owner.SyncAsync();
        }

        Assert.All(await ramesh.EntriesAsync(), e => Assert.Equal(f.Ramesh.Id, e.EmployeeId));
        Assert.All(await suma.EntriesAsync(), e => Assert.Equal(f.Suma.Id, e.EmployeeId));
        Assert.All(await ramesh.Inner.GetRateChangesAsync(), r => Assert.Equal(f.Ramesh.Id, r.EmployeeId));
        Assert.Equal(["Ramesh"], (await ramesh.Inner.GetEmployeesAsync()).Select(e => e.FullName));
        Assert.Equal(4, (await f.Owner.EntriesAsync()).Count);

        // Different topics and keys: Ramesh's channel key can't open Suma's messages.
        var channels = await f.Owner.SyncStore.GetChannelsAsync();
        Assert.Equal(2, channels.Select(c => c.Topic).Distinct().Count());
        var sumaChannel = channels.Single(c => c.EmployeeId == f.Suma.Id);
        var rameshChannel = channels.Single(c => c.EmployeeId == f.Ramesh.Id);
        Assert.All(f.Relay1.Messages(sumaChannel.Topic), m => Assert.Null(WireCodec.Decode(m.Text, f.Suma.Id, rameshChannel.Key, null)));
    }

    [Fact]
    public async Task Junk_wrong_key_and_replayed_messages_are_ignored()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        var channel = Assert.Single(await f.Owner.SyncStore.GetChannelsAsync());

        await ramesh.Production.SaveOwnTodayAsync(3);
        await ramesh.SyncAsync();
        await f.Owner.SyncAsync();

        // Junk, a message signed by a stranger's key, and a replay of the worker's real message.
        var real = f.Relay1.Messages(channel.Topic).Last(m => WireCodec.Decode(m.Text, f.Ramesh.Id, channel.Key, channel.WorkerKey) is not null);
        await f.Relay1.PublishAsync(channel.Topic, "hello");
        using var stranger = SigningKey.Create();
        var forged = Envelope.Create(f.Ramesh.Id, Senders.Worker, 999, f.Clock.UtcNow, Kinds.Changes, new ChangesBody([]));
        await f.Relay1.PublishAsync(channel.Topic, WireCodec.Encode(forged, channel.Key, stranger));
        await f.Relay1.PublishAsync(channel.Topic, real.Text);

        var report = await f.Owner.SyncAsync();
        Assert.Equal(0, report.Received);
        Assert.Single(await f.Owner.EntriesAsync());
    }

    [Fact]
    public async Task Messages_fit_the_relay_and_hide_their_size()
    {
        using var f = await new Factory().SetUpAsync();
        // A long history: 300 days for Ramesh, so the snapshot needs several messages.
        var day = new DateOnly(2026, 1, 1);
        await f.Owner.Employees.ChangeRateAsync(f.Ramesh.Id, Money.FromRupees(500), day);
        for (; day < Factory.Today; day = day.AddDays(1))
        {
            await f.Owner.Production.SaveDayAsync(day, new Dictionary<string, int?> { [f.Ramesh.Id] = day.Day % 5 });
        }

        var ramesh = await f.LinkAsync(f.Ramesh);
        Assert.Equal((await f.Owner.Inner.GetEntriesAsync(new DateRange(DateOnly.MinValue, DateOnly.MaxValue), f.Ramesh.Id)).Count, (await ramesh.EntriesAsync()).Count);
        var topic = Assert.Single(await f.Owner.SyncStore.GetChannelsAsync()).Topic;
        Assert.True(f.Relay1.Messages(topic).Count > 2);
        Assert.All(f.Relay1.Messages(topic), m => Assert.True(m.Text.Length <= 3800));
        Assert.True(f.Relay1.Messages(topic).Select(m => m.Text.Length).Distinct().Count() <= 4); // padding buckets
    }
}

public class OfflineTests
{
    [Fact]
    public async Task Worker_away_two_days_catches_up_through_a_state_request()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);

        // While Ramesh's phone is off, the owner enters a day, changes his rate and pays him.
        await f.Owner.Production.SaveDayAsync(new DateOnly(2026, 11, 22), new Dictionary<string, int?> { [f.Ramesh.Id] = 2 });
        await f.Owner.Employees.ChangeRateAsync(f.Ramesh.Id, Money.FromRupees(550), Factory.Today);
        await f.Owner.Payments.MarkPaidAsync(f.Ramesh.Id, new DateOnly(2026, 11, 22), Factory.Today);
        await f.Owner.SyncAsync();

        f.Clock.Advance(TimeSpan.FromDays(2)); // messages expired on the relay
        await ramesh.SyncAsync();     // nothing left to fetch → asks for everything
        await f.Owner.SyncAsync();    // answers with a snapshot
        await ramesh.SyncAsync();

        Assert.Single(await ramesh.EntriesAsync());
        Assert.Equal(2, (await ramesh.Inner.GetRateChangesAsync()).Count);
        Assert.Single(await ramesh.Inner.GetPaymentsAsync());
        var week = (await ramesh.Worker.DashboardAsync()).Value!.Weeks.Single(w => w.Week.Start == new DateOnly(2026, 11, 22));
        Assert.Equal(PaymentStatus.Paid, week.Status);
    }

    [Fact]
    public async Task Owner_away_two_days_gets_the_workers_entries_again()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        await ramesh.Production.SaveOwnTodayAsync(3);
        await ramesh.SyncAsync();             // sent, but the owner's phone is off…

        f.Clock.Advance(TimeSpan.FromDays(2)); // …until after the relay forgot it
        await f.Owner.SyncAsync();            // asks Ramesh's phone for everything
        await ramesh.SyncAsync();             // answers with its own recent entries
        await f.Owner.SyncAsync();

        Assert.Equal(3, Assert.Single(await f.Owner.EntriesAsync()).SareeCount);
    }

    [Fact]
    public async Task Nothing_is_lost_while_offline()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        f.Relay1.Online = false;
        await ramesh.Production.SaveOwnTodayAsync(3);
        Assert.NotNull((await ramesh.SyncAsync()).Error);
        Assert.Equal(1, await ramesh.Sync.PendingAsync());

        f.Relay1.Online = true;
        await ramesh.SyncAsync();
        await f.Owner.SyncAsync();
        Assert.Single(await f.Owner.EntriesAsync());
    }
}

public class UnlinkTests
{
    [Fact]
    public async Task Disabling_a_worker_wipes_their_phone()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        await f.Owner.Employees.SetActiveAsync(f.Ramesh.Id, false);
        await f.Owner.SyncAsync();

        var report = await ramesh.SyncAsync();
        Assert.True(report.Unlinked);
        Assert.Null(await ramesh.Inner.GetFactoryAsync());
        Assert.Empty(await ramesh.Inner.GetEmployeesAsync());
        Assert.Equal(PhoneRole.None, await ramesh.Sync.RoleAsync());
        Assert.Empty(await f.Owner.SyncStore.GetChannelsAsync());
    }

    [Fact]
    public async Task Relinking_cuts_off_the_old_phone()
    {
        using var f = await new Factory().SetUpAsync();
        var oldPhone = await f.LinkAsync(f.Ramesh);
        var newPhone = await f.LinkAsync(f.Ramesh); // e.g. a new phone or a forgotten PIN

        Assert.True((await oldPhone.SyncAsync()).Unlinked);
        await f.Owner.Production.SaveDayAsync(Factory.Today, new Dictionary<string, int?> { [f.Ramesh.Id] = 2 });
        await f.Owner.SyncAsync();
        await newPhone.SyncAsync();
        Assert.Single(await newPhone.EntriesAsync());
        Assert.Null(await oldPhone.Inner.GetFactoryAsync());
    }

    [Fact]
    public async Task Import_on_the_owner_phone_resends_snapshots()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);
        await f.Owner.Production.SaveDayAsync(Factory.Today, new Dictionary<string, int?> { [f.Ramesh.Id] = 2 });
        var file = (await f.Owner.Data.ExportAsync("0.3.0")).Value!;
        await f.Owner.SyncAsync();
        await ramesh.SyncAsync();

        await f.Owner.Production.SaveDayAsync(Factory.Today, new Dictionary<string, int?> { [f.Ramesh.Id] = 7 });
        await f.Owner.Data.ImportAsync(f.Owner.Data.Preview(file).Value!.Data); // back to 2 (direct DB replace: no outbox)
        await f.Owner.Sync.ResendAllAsync();
        await f.Owner.SyncAsync();
        await ramesh.SyncAsync();
        Assert.Equal(2, Assert.Single(await ramesh.EntriesAsync()).SareeCount);
    }
}

public class NotificationTests
{
    [Fact]
    public async Task Worker_submission_notifies_the_owner_when_turned_on()
    {
        using var f = await new Factory().SetUpAsync();
        var ramesh = await f.LinkAsync(f.Ramesh);

        // Off by default: nothing is sent.
        await ramesh.Production.SaveOwnTodayAsync(2);
        await ramesh.SyncAsync();
        Assert.Empty(f.Relay1.Notifications);

        // The owner turns it on after linking: the topic reaches the worker's phone.
        var topic = await f.Owner.Sync.SetNotificationsAsync(true);
        Assert.StartsWith("hf-", topic, StringComparison.Ordinal);
        await f.Owner.SyncAsync();
        await ramesh.SyncAsync();

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await ramesh.Production.SaveOwnTodayAsync(3);
        await ramesh.SyncAsync();
        var notice = Assert.Single(f.Relay1.Notifications);
        Assert.Equal((topic, "Sri Lakshmi Handlooms", "Ramesh submitted 3 sarees today"), notice);

        // Turned off again: no more notices.
        await f.Owner.Sync.SetNotificationsAsync(false);
        await f.Owner.SyncAsync();
        await ramesh.SyncAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await ramesh.Production.SaveOwnTodayAsync(4);
        await ramesh.SyncAsync();
        Assert.Single(f.Relay1.Notifications);
    }

    [Fact]
    public async Task Weekly_payments_notice_on_saturday_evening_once()
    {
        using var f = await new Factory().SetUpAsync();
        await f.Owner.Production.SaveDayAsync(Factory.Today, new Dictionary<string, int?> { [f.Ramesh.Id] = 2, [f.Suma.Id] = 1 });
        await f.Owner.Sync.SetNotificationsAsync(true);
        await f.Owner.SyncAsync(); // becomes the owner phone

        Assert.False(await f.Owner.Sync.SendWeeklyNoticeIfDueAsync()); // Monday
        f.Clock.Set(new DateTimeOffset(2026, 11, 28, 17, 0, 0, TimeSpan.FromHours(5.5)));
        Assert.False(await f.Owner.Sync.SendWeeklyNoticeIfDueAsync()); // Saturday, before 18:00
        f.Clock.Set(new DateTimeOffset(2026, 11, 28, 18, 30, 0, TimeSpan.FromHours(5.5)));
        Assert.True(await f.Owner.Sync.SendWeeklyNoticeIfDueAsync());
        Assert.False(await f.Owner.Sync.SendWeeklyNoticeIfDueAsync()); // once per week

        var notice = Assert.Single(f.Relay1.Notifications);
        Assert.Equal("Weekly payments ready: ₹1,450 for 2 workers", notice.Message);
    }
}
