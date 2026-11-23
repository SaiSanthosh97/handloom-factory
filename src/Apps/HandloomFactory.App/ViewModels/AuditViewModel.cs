using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Reports;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.ViewModels;

public sealed record AuditItem(string When, string Title, string Details)
{
    public bool HasDetails => Details.Length > 0;
}

/// <summary>Change history (audit log, owner only): who changed what and when, newest first; can be exported as CSV.</summary>
public sealed partial class AuditViewModel(AuditService audit) : ObservableObject
{
    private IReadOnlyList<AuditLine> _lines = [];

    public ObservableCollection<AuditItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public async Task LoadAsync()
    {
        _lines = (await audit.RecentAsync()).Value ?? [];
        Items.Clear();
        foreach (var line in _lines)
        {
            var who = line.Who.Length > 0 ? line.Who : AppResources.Audit_System;
            Items.Add(new AuditItem(
                line.At.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.CurrentCulture),
                $"{Describe(line.Action)} · {who}",
                Details(line)));
        }

        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private Task ExportAsync()
    {
        var csv = ReportCsv.WriteLog(
            _lines.Select(l => (l.At.ToLocalTime(), l.Who, Describe(l.Action), l.Before, l.After)),
            [AppResources.Audit_When, AppResources.Audit_Who, AppResources.Audit_What, AppResources.Audit_Before, AppResources.Audit_After]);
        return ShareFiles.ShareAsync(csv, $"handloom-history-{DateTime.Now:yyyy-MM-dd}.csv", "text/csv", AppResources.Audit_Title);
    }

    /// <summary>Friendly names for the recorded actions.</summary>
    public static string Describe(string action) => action switch
    {
        AuditActions.FactorySetUp => AppResources.Audit_FactorySetUp,
        AuditActions.EmployeeCreated => AppResources.Audit_EmployeeCreated,
        AuditActions.EmployeeUpdated => AppResources.Audit_EmployeeUpdated,
        AuditActions.EmployeeDisabled => AppResources.Audit_EmployeeDisabled,
        AuditActions.EmployeeEnabled => AppResources.Audit_EmployeeEnabled,
        AuditActions.RateChanged => AppResources.Audit_RateChanged,
        AuditActions.EntrySaved => AppResources.Audit_EntrySaved,
        AuditActions.EntryDeleted => AppResources.Audit_EntryDeleted,
        AuditActions.PinChanged => AppResources.Audit_PinChanged,
        AuditActions.PaymentMarked => AppResources.Audit_PaymentMarked,
        AuditActions.PaymentUnmarked => AppResources.Audit_PaymentUnmarked,
        AuditActions.DataExported => AppResources.Audit_DataExported,
        AuditActions.DataImported => AppResources.Audit_DataImported,
        AuditActions.PhoneLinked => AppResources.Audit_PhoneLinked,
        AuditActions.FactoryUpdated => AppResources.Audit_FactoryUpdated,
        AuditActions.OwnerWeaves => AppResources.Audit_OwnerWeaves,
        AuditActions.PinReset => AppResources.Audit_PinReset,
        _ => action,
    };

    private static string Details(AuditLine line) => (line.Before, line.After) switch
    {
        ({ } before, { } after) => $"{Clean(before)} → {Clean(after)}",
        (null, { } after) => Clean(after),
        ({ } before, null) => Clean(before),
        _ => "",
    };

    /// <summary>Records are stored as small JSON objects; show them as "key: value" text.</summary>
    private static string Clean(string text) =>
        text.StartsWith('{')
            ? text.Trim('{', '}').Replace("\"", "", StringComparison.Ordinal).Replace(":", ": ", StringComparison.Ordinal).Replace(",", ", ", StringComparison.Ordinal)
            : text;
}
