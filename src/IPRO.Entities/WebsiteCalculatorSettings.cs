using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteCalculatorSettings
{
    public string CalculatorKind { get; set; } = CalculatorKinds.MortgagePayment;

    public static WebsiteCalculatorSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<WebsiteCalculatorSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            settings.CalculatorKind = CalculatorKinds.Normalize(settings.CalculatorKind);
            return settings;
        }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

public static class CalculatorKinds
{
    public const string MortgagePayment = "MortgagePayment";
    public const string Refinance = "Refinance";
    public const string RentVsBuy = "RentVsBuy";
    public const string Retirement = "Retirement";
    public const string TaxAdvantageComparison = "TaxAdvantageComparison";
    public const string LoanAmortization = "LoanAmortization";
    public const string Apr = "Apr";

    public static readonly string[] All =
    {
        MortgagePayment, Refinance, RentVsBuy, Retirement, TaxAdvantageComparison, LoanAmortization, Apr
    };

    public static string DisplayName(string kind) => kind switch
    {
        MortgagePayment => "Mortgage Payment",
        Refinance => "Refinance Break-Even",
        RentVsBuy => "Rent vs. Buy",
        Retirement => "Retirement Savings",
        TaxAdvantageComparison => "Tax-Deferred vs. Tax-Free Savings",
        LoanAmortization => "Loan Amortization Schedule",
        Apr => "APR / Closing Cost",
        _ => kind
    };

    public static string Normalize(string? kind)
    {
        var value = kind?.Trim() ?? string.Empty;
        return All.Contains(value) ? value : MortgagePayment;
    }
}
