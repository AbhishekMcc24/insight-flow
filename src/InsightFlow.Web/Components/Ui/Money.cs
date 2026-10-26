using System.Globalization;

namespace InsightFlow.Web.Components.Ui;

/// <summary>Formats small US-dollar amounts such as the cost of an AI call: enough digits that a fraction of a cent is still visible.</summary>
public static class Money
{
    public static string Format(decimal usd) => usd switch
    {
        0m => "$0.00",
        < 0.01m => "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture),
        < 1m => "$" + usd.ToString("0.000", CultureInfo.InvariantCulture),
        _ => "$" + usd.ToString("N2", CultureInfo.InvariantCulture),
    };
}
