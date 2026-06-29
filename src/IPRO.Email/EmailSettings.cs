namespace IPRO.Email;

public class EmailSettings
{
    public string SendGridApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "noreply@iprosystem.com";
    public string FromName { get; set; } = "IPRO System";
    public string ReplyToEmail { get; set; } = "support@iprosystem.com";
}
