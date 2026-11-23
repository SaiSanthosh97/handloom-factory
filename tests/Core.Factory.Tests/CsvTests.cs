using System.Text;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Reports;

namespace HandloomFactory.Core.Factory.Tests;

public class CsvTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static (PeriodReport Report, Employee Ramesh) WeekReport()
    {
        var ramesh = new Employee("a", "E001", "Ramesh, K", null, Role.Employee, true, T0, T0);
        var suma = new Employee("b", "E002", "=Suma", null, Role.Employee, true, T0, T0);
        var rates = new RateBook([
            new RateChange("r1", "a", new Money(45050), new DateOnly(2026, 11, 1), T0),
            new RateChange("r2", "b", Money.FromRupees(400), new DateOnly(2026, 11, 1), T0)]);
        var entries = new[]
        {
            new ProductionEntry("e1", "a", new DateOnly(2026, 11, 23), 3, EnteredBy.Admin, T0, T0),
            new ProductionEntry("e2", "b", new DateOnly(2026, 11, 24), 2, EnteredBy.Admin, T0, T0),
        };
        return (ReportBuilder.Period(Periods.PayWeek(new DateOnly(2026, 11, 23)), [ramesh, suma], rates, entries), ramesh);
    }

    [Fact]
    public void Weekly_csv_opens_in_excel_with_totals_and_payments()
    {
        var (report, ramesh) = WeekReport();
        var paid = new WeekPayment(ramesh.Id, report.Period, PaymentStatus.Paid, 3, new Money(135150), Money.Zero, new Money(135150), new DateOnly(2026, 11, 28), Money.Zero);
        var bytes = ReportCsv.Write("Weekly report", report, new Dictionary<string, WeekPayment> { [ramesh.Id] = paid });

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]); // BOM: Excel reads UTF-8
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("Weekly report", lines[0]);
        Assert.Equal("2026-11-22 - 2026-11-28", lines[1]);
        Assert.StartsWith("Code,Worker,Sun 22-11,Mon 23-11", lines[3], StringComparison.Ordinal);
        Assert.EndsWith("Sarees,Rate per saree (Rs),Earnings (Rs),Payment,Paid date,Paid amount (Rs)", lines[3], StringComparison.Ordinal);
        Assert.Equal("E001,\"Ramesh, K\",,3,,,,,,3,450.5,1351.5,Paid,2026-11-28,1351.5", lines[5]); // rows sort by name
        Assert.Equal("E002,'=Suma,,,2,,,,,2,400,800,,,", lines[4]); // formula-looking name neutralised
        Assert.Equal(",Total,0,3,2,0,0,0,0,5,,2151.5", lines[6]);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("-5", "-5")]
    [InlineData("@x", "'@x")]
    public void Cells_are_escaped(string cell, string expected) => Assert.Equal(expected, ReportCsv.Escape(cell));

    [Fact]
    public void Amounts_are_plain_numbers() => Assert.Equal(["21000", "450.5", "0"], new[] { Money.FromRupees(21000), new Money(45050), Money.Zero }.Select(ReportCsv.Number));
}
