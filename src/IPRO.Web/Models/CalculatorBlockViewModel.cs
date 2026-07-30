namespace IPRO.Web.Models;

public class CalculatorBlockViewModel
{
    public int BlockId { get; set; }
    public string CalculatorKind { get; set; } = IPRO.Entities.CalculatorKinds.MortgagePayment;
}
