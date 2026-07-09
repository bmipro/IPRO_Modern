namespace IPRO.Billing;

public class PayPalWebhookHeaders
{
    public string TransmissionId { get; set; } = string.Empty;
    public string TransmissionTime { get; set; } = string.Empty;
    public string TransmissionSignature { get; set; } = string.Empty;
    public string CertificateUrl { get; set; } = string.Empty;
    public string AuthenticationAlgorithm { get; set; } = string.Empty;
}
