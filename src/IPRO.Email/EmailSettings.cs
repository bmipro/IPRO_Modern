namespace IPRO.Email;

public class EmailSettings
{
    public string SendGridApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "no-reply@iproadvisers.com";
    public string FromName { get; set; } = "IPRO Advisers";
    public string ReplyToEmail { get; set; } = "support@iproadvisers.com";
}
