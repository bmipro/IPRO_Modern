using IPRO.Entities;

namespace IPRO.Web.Infrastructure;

// Which calculators each vertical's Resources section carries, in display order. One map shared by
// WebsiteStarterResourcesHelper (real provisioning) and ProspectWebsitePreviewBuilder (the zero-write
// /Preview mirror) so the two cannot drift. Sourced from the owner's cal1 library, 2026-08-15;
// the Canada-inappropriate ones (Roth IRA, US estate tax, ARM vs fixed, points) were left unported.
public static class VerticalCalculatorCatalog
{
    public record CalculatorEntry(string Kind, string Blurb);

    public static IReadOnlyList<CalculatorEntry> ForBusinessType(string? businessType) => businessType switch
    {
        "Mortgage" => new[]
        {
            new CalculatorEntry(CalculatorKinds.MortgagePayment, "Estimate your monthly payment, total interest, and the bi-weekly equivalent for any purchase price."),
            new CalculatorEntry(CalculatorKinds.Affordability, "See the maximum home price standard Canadian lending ratios support on your income and debts."),
            new CalculatorEntry(CalculatorKinds.BiWeeklyPayments, "How much time and interest accelerated bi-weekly payments shave off a mortgage."),
            new CalculatorEntry(CalculatorKinds.MortgagePrepayment, "What an extra monthly payment or a lump sum does to your payoff date and total interest."),
            new CalculatorEntry(CalculatorKinds.LandTransferTax, "Estimate land transfer tax on a purchase in any Canadian province, including Toronto's municipal tax."),
            new CalculatorEntry(CalculatorKinds.Refinance, "Whether refinancing saves money once closing costs are counted, and how fast you break even."),
            new CalculatorEntry(CalculatorKinds.RentVsBuy, "Compare the long-run cost of renting against buying the same home."),
            new CalculatorEntry(CalculatorKinds.LoanAmortization, "A full month-by-month amortization schedule for any loan."),
        },
        "Insurance / Financial" => new[]
        {
            new CalculatorEntry(CalculatorKinds.Retirement, "Whether your current savings rate funds the retirement you want, and how long the money lasts."),
            new CalculatorEntry(CalculatorKinds.SavingsGrowth, "What a starting amount plus steady monthly deposits grows into, in future and today's dollars."),
            new CalculatorEntry(CalculatorKinds.SavingsGoal, "How long it takes to reach a savings goal, honestly adjusted for inflation."),
            new CalculatorEntry(CalculatorKinds.TaxAdvantageComparison, "Whether a tax-deferred (RRSP-style) or tax-free (TFSA-style) account leaves you further ahead."),
            new CalculatorEntry(CalculatorKinds.AfterTaxReturn, "What an interest rate is really worth after your marginal tax rate and inflation."),
            new CalculatorEntry(CalculatorKinds.RentVsBuy, "Compare the long-run cost of renting against buying the same home."),
        },
        "Accountants" => new[]
        {
            new CalculatorEntry(CalculatorKinds.AfterTaxReturn, "What an interest rate is really worth after your marginal tax rate and inflation."),
            new CalculatorEntry(CalculatorKinds.LoanAmortization, "A full month-by-month amortization schedule for any business or personal loan."),
            new CalculatorEntry(CalculatorKinds.Apr, "The true cost of a loan once fees and closing costs are included."),
            new CalculatorEntry(CalculatorKinds.SavingsGrowth, "What a starting amount plus steady monthly deposits grows into, in future and today's dollars."),
            new CalculatorEntry(CalculatorKinds.TaxAdvantageComparison, "Whether a tax-deferred (RRSP-style) or tax-free (TFSA-style) account leaves you further ahead."),
        },
        _ => new[]
        {
            new CalculatorEntry(CalculatorKinds.MortgagePayment, "Estimate your monthly payment, total interest, and the bi-weekly equivalent for any purchase price."),
            new CalculatorEntry(CalculatorKinds.SavingsGrowth, "What a starting amount plus steady monthly deposits grows into, in future and today's dollars."),
            new CalculatorEntry(CalculatorKinds.Retirement, "Whether your current savings rate funds the retirement you want, and how long the money lasts."),
        },
    };
}
