using System.Globalization;
using System.Text;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Reports;

/// <summary>
/// Reports as CSV files that open in Excel (D: "Reports can be exported to CSV"): UTF-8 with a byte-order mark (so
/// Excel shows every script correctly), comma-separated, quoted where needed, amounts as plain rupee numbers
/// (21000.50) so Excel can add them up.
/// </summary>
public static class ReportCsv
{
    /// <summary>Column titles (the app passes translated ones; English by default).</summary>
    public sealed record Headings(
        string Code = "Code",
        string Name = "Worker",
        string Sarees = "Sarees",
        string Rate = "Rate per saree (Rs)",
        string Earnings = "Earnings (Rs)",
        string Status = "Payment",
        string PaidDate = "Paid date",
        string PaidAmount = "Paid amount (Rs)",
        string Total = "Total",
        string Varies = "varies",
        string Paid = "Paid",
        string Pending = "To pay");

    /// <param name="payments">For a weekly report: each worker's payment line (by employee id), or null.</param>
    public static byte[] Write(string title, PeriodReport report, IReadOnlyDictionary<string, WeekPayment>? payments = null, Headings? headings = null)
    {
        var h = headings ?? new Headings();
        var sb = new StringBuilder();
        Line(sb, title);
        Line(sb, $"{report.Period.Start:yyyy-MM-dd} - {report.Period.End:yyyy-MM-dd}");
        sb.Append("\r\n");

        var header = new List<string> { h.Code, h.Name };
        header.AddRange(report.Days.Select(d => d.ToString("ddd dd-MM", CultureInfo.InvariantCulture)));
        header.AddRange([h.Sarees, h.Rate, h.Earnings]);
        if (payments is not null)
        {
            header.AddRange([h.Status, h.PaidDate, h.PaidAmount]);
        }

        Line(sb, [.. header]);
        foreach (var row in report.Rows)
        {
            var cells = new List<string> { row.Code, row.Name };
            cells.AddRange(row.DailyCounts.Select(c => c?.ToString(CultureInfo.InvariantCulture) ?? ""));
            cells.Add(row.Sarees.ToString(CultureInfo.InvariantCulture));
            cells.Add(row.RateVaries ? h.Varies : row.Rate is { } r ? Number(r) : "");
            cells.Add(Number(row.Earnings));
            if (payments is not null)
            {
                payments.TryGetValue(row.EmployeeId, out var p);
                cells.Add(p?.Status switch { PaymentStatus.Paid => h.Paid, PaymentStatus.Pending => h.Pending, _ => "" });
                cells.Add(p?.PaidDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
                cells.Add(p is { Status: not PaymentStatus.Nothing } ? Number(p.Total) : "");
            }

            Line(sb, [.. cells]);
        }

        var totals = new List<string> { "", h.Total };
        totals.AddRange(report.Days.Select((_, i) => report.Rows.Sum(r => r.DailyCounts[i] ?? 0).ToString(CultureInfo.InvariantCulture)));
        totals.AddRange([report.TotalSarees.ToString(CultureInfo.InvariantCulture), "", Number(report.TotalEarnings)]);
        Line(sb, [.. totals]);

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>The change history as CSV.</summary>
    public static byte[] WriteLog(IEnumerable<(DateTimeOffset At, string Who, string What, string? Before, string? After)> lines, string[] headings)
    {
        var sb = new StringBuilder();
        Line(sb, headings);
        foreach (var l in lines)
        {
            Line(sb, l.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), l.Who, l.What, l.Before ?? "", l.After ?? "");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>Rupees with up to two decimals, no grouping: 21000 or 450.5 (Excel reads it as a number).</summary>
    public static string Number(Money money) => (money.Paise / 100m).ToString("0.##", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder sb, params string[] cells)
    {
        sb.Append(string.Join(',', cells.Select(Escape)));
        sb.Append("\r\n");
    }

    /// <summary>
    /// Quotes cells with commas, quotes or line breaks, and neutralises cells starting with = + - @ so a name can't run
    /// as an Excel formula (CSV injection).
    /// </summary>
    public static string Escape(string cell)
    {
        if (cell.Length > 0 && "=+-@".Contains(cell[0], StringComparison.Ordinal) && !decimal.TryParse(cell, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            cell = "'" + cell;
        }

        return cell.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
    }
}
