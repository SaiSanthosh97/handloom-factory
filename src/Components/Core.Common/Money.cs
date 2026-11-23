using System.Globalization;

namespace HandloomFactory.Core.Common;

/// <summary>
/// An amount of Indian rupees stored as whole <b>paise</b> (₹1 = 100 paise). Integer arithmetic only, so totals
/// never drift by rounding.
/// </summary>
public readonly record struct Money(long Paise) : IComparable<Money>
{
    public static readonly Money Zero = new(0);

    public static Money FromRupees(long rupees) => new(checked(rupees * 100));

    public static Money operator +(Money a, Money b) => new(checked(a.Paise + b.Paise));

    public static Money operator -(Money a, Money b) => new(checked(a.Paise - b.Paise));

    public static Money operator *(Money a, int count) => new(checked(a.Paise * count));

    public static bool operator <(Money a, Money b) => a.Paise < b.Paise;

    public static bool operator >(Money a, Money b) => a.Paise > b.Paise;

    public static bool operator <=(Money a, Money b) => a.Paise <= b.Paise;

    public static bool operator >=(Money a, Money b) => a.Paise >= b.Paise;

    public int CompareTo(Money other) => Paise.CompareTo(other.Paise);

    public bool IsNegative => Paise < 0;

    /// <summary>
    /// Reads what a person typed: "500", "500.5", "500.50", "₹ 1,200". At most two decimals, no negatives.
    /// </summary>
    public static bool TryParse(string? text, out Money money)
    {
        money = Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Replace("₹", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Replace("Rs", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rupees)
            || rupees < 0 || rupees > 10_000_000m || decimal.Round(rupees, 2) != rupees)
        {
            return false;
        }

        money = new Money((long)(rupees * 100));
        return true;
    }

    /// <summary>"₹21,000" or "₹1,23,456.50": Indian digit grouping, paise only when not zero.</summary>
    public override string ToString() => (Paise < 0 ? "-₹" : "₹") + Format(this);

    /// <summary>The number without the ₹ sign, e.g. for an input box: "500" or "450.50".</summary>
    public string ToPlainString()
    {
        var abs = Math.Abs(Paise);
        var text = (abs / 100).ToString(CultureInfo.InvariantCulture) + (abs % 100 == 0 ? "" : "." + (abs % 100).ToString("00", CultureInfo.InvariantCulture));
        return Paise < 0 ? "-" + text : text;
    }

    private static string Format(Money money)
    {
        var abs = Math.Abs(money.Paise);
        var rupees = (abs / 100).ToString(CultureInfo.InvariantCulture);
        var paise = abs % 100;

        // Indian grouping: last three digits, then groups of two (12,34,567).
        string grouped;
        if (rupees.Length <= 3)
        {
            grouped = rupees;
        }
        else
        {
            var head = rupees[..^3];
            var tail = rupees[^3..];
            var parts = new List<string>();
            while (head.Length > 2)
            {
                parts.Insert(0, head[^2..]);
                head = head[..^2];
            }

            parts.Insert(0, head);
            grouped = string.Join(',', parts) + "," + tail;
        }

        return paise == 0 ? grouped : grouped + "." + paise.ToString("00", CultureInfo.InvariantCulture);
    }
}
