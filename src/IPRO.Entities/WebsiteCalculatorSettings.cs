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
    // Second wave, ported 2026-08-15 from the owner's cal1 library (Canada-appropriate subset --
    // the US-only ones like Roth, estate tax and ARM-vs-fixed were deliberately left behind).
    public const string Affordability = "Affordability";
    public const string BiWeeklyPayments = "BiWeeklyPayments";
    public const string MortgagePrepayment = "MortgagePrepayment";
    public const string LandTransferTax = "LandTransferTax";
    public const string SavingsGrowth = "SavingsGrowth";
    public const string SavingsGoal = "SavingsGoal";
    public const string AfterTaxReturn = "AfterTaxReturn";

    public static readonly string[] All =
    {
        MortgagePayment, Refinance, RentVsBuy, Retirement, TaxAdvantageComparison, LoanAmortization, Apr,
        Affordability, BiWeeklyPayments, MortgagePrepayment, LandTransferTax, SavingsGrowth, SavingsGoal, AfterTaxReturn
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
        Affordability => "How Much Home Can I Afford?",
        BiWeeklyPayments => "Accelerated Bi-Weekly Payments",
        MortgagePrepayment => "Mortgage Prepayment Savings",
        LandTransferTax => "Land Transfer Tax (Canada)",
        SavingsGrowth => "Savings Growth",
        SavingsGoal => "Savings Goal Timeline",
        AfterTaxReturn => "After-Tax Return",
        _ => kind
    };

    public static string Normalize(string? kind)
    {
        var value = kind?.Trim() ?? string.Empty;
        return All.Contains(value) ? value : MortgagePayment;
    }
}
