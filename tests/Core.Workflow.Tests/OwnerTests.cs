using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Workflow.Tests;

/// <summary>0.6.0: the owner also weaves, edits the factory details, and resets a forgotten PIN.</summary>
public class OwnerTests
{
    [Fact]
    public async Task Owner_who_weaves_is_counted_and_paid_like_a_worker()
    {
        using var phone = new Phone();
        var owner = await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);

        // Off by default: only Ramesh is in the lists.
        Assert.Single((await phone.Employees.ListAsync()).Value!);
        Assert.Single((await phone.Production.GetDayAsync(Phone.Today)).Value!.Rows);

        // A rate is needed the first time.
        Assert.Equal(ErrorCode.RateInvalid, (await phone.Accounts.SetOwnerWeavesAsync(true)).Error?.Code);
        Assert.True((await phone.Accounts.SetOwnerWeavesAsync(true, Money.FromRupees(400), new DateOnly(2026, 11, 1))).Ok);
        Assert.True(phone.Session.User!.Weaves);
        Assert.True(phone.Session.IsAdmin); // still the owner

        Assert.Equal(2, (await phone.Employees.ListAsync()).Value!.Count);
        await phone.EnterAsync(owner, Phone.Today, 2);
        await phone.EnterAsync(ramesh, Phone.Today, 1);

        var week = (await phone.Payments.WeekAsync(Phone.Today)).Value!;
        Assert.Equal(2, week.Rows.Count);
        Assert.Equal(Money.FromRupees(800), week.Rows.Single(r => r.Payment.EmployeeId == owner.Id).Payment.Total);

        // The owner can't be disabled (it would lock everyone out) and can't have a phone linked.
        Assert.Equal(ErrorCode.NotAllowed, (await phone.Employees.SetActiveAsync(owner.Id, false)).Error?.Code);

        // Turned off: history stays, no rate needed to turn it on again.
        Assert.True((await phone.Accounts.SetOwnerWeavesAsync(false)).Ok);
        Assert.Single((await phone.Employees.ListAsync()).Value!);
        Assert.True(await phone.Accounts.OwnerHasRateAsync());
        Assert.True((await phone.Accounts.SetOwnerWeavesAsync(true)).Ok);
        Assert.Equal(2, (await phone.Employees.ListAsync()).Value!.Count);
    }

    [Fact]
    public async Task Owner_weaving_survives_restart_and_export_import()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        await phone.Accounts.SetOwnerWeavesAsync(true, Money.FromRupees(400), new DateOnly(2026, 11, 1));

        Assert.True(phone.Owner.Weaves); // read back from the database

        var file = (await phone.Data.ExportAsync("0.6.0")).Value!;
        using var other = new Phone();
        var preview = other.Data.Preview(file);
        Assert.True((await other.Data.ImportAsync(preview.Value!.Data)).Ok);
        Assert.True(other.Owner.Weaves);
    }

    [Fact]
    public async Task Factory_and_owner_names_can_be_changed()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();

        Assert.Equal(ErrorCode.NameRequired, (await phone.Accounts.UpdateFactoryAsync(" ", "Santhosh")).Error?.Code);
        Assert.True((await phone.Accounts.UpdateFactoryAsync("Sri Lakshmi Silks", "Sai Santhosh")).Ok);

        var factory = (await phone.Store.GetFactoryAsync())!;
        Assert.Equal("Sri Lakshmi Silks", factory.Name);
        Assert.Equal("Sai Santhosh", factory.OwnerName);
        Assert.Equal("Sai Santhosh", phone.Owner.FullName);
        Assert.Equal("Sai Santhosh", phone.Session.User!.FullName);
        Assert.Contains(await phone.Store.GetAuditAsync(5), a => a.Action == AuditActions.FactoryUpdated);

        // Workers can't.
        var ramesh = await phone.AddAsync("Ramesh", 500);
        phone.Session.SignIn(ramesh);
        Assert.Equal(ErrorCode.NotAllowed, (await phone.Accounts.UpdateFactoryAsync("X", "Y")).Error?.Code);
    }

    [Fact]
    public async Task Forgotten_owner_pin_is_reset()
    {
        using var phone = new Phone();
        var owner = await phone.SetUpAsync();
        phone.Accounts.Logout();

        Assert.Equal(ErrorCode.PinsDoNotMatch, (await phone.Accounts.ResetOwnerPinAsync("5678", "5679")).Error?.Code);
        Assert.True((await phone.Accounts.ResetOwnerPinAsync("5678", "5678")).Ok);
        Assert.True(phone.Session.IsAdmin);

        phone.Accounts.Logout();
        Assert.False((await phone.Accounts.LoginAsync(owner.Id, "1234")).Success);
        Assert.True((await phone.Accounts.LoginAsync(owner.Id, "5678")).Success);
        Assert.Contains(await phone.Store.GetAuditAsync(5), a => a.Action == AuditActions.PinReset);
    }
}
