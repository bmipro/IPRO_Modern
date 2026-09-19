using System.Net;
using System.Text;

namespace IPRO.Web.Models;

public static class RegistrationWelcomeTemplate
{
    public static string BuildHtml(RegistrationWelcomeModel model)
    {
        var name = WebUtility.HtmlEncode(model.FullName);
        var userName = WebUtility.HtmlEncode(model.UserName);
        // Self-signups choose their own password on the registration form, so this email carries no
        // credentials for them (empty TemporaryPassword). Admin-created accounts still get one.
        var hasTempPassword = !string.IsNullOrWhiteSpace(model.TemporaryPassword);
        var password = WebUtility.HtmlEncode(model.TemporaryPassword);
        var passwordCellTitle = hasTempPassword ? "Temporary Password" : "Password";
        var passwordCellValue = hasTempPassword ? password : "The one you chose at signup";
        var passwordNote = hasTempPassword
            ? "For your security, you will be asked to change this temporary password the first time you sign in."
            // 501: the box above shows a USERNAME and the sign-in page asks for one; the email address
            // works as well (AgentService.AuthenticateAsync tries both). The sentence said only "email".
            : "You sign in with your username or your email address, and the password you chose when you registered. It was never sent by email.";
        var domain = WebUtility.HtmlEncode(model.SetupDomain);
        // 501: https. Every temporary domain is served under the wildcard certificate and the app is
        // HTTPS-only, so http:// worked only by redirect -- and read as dated in a welcome email.
        var domainUrl = $"https://{domain}";
        var trainingEmail = WebUtility.HtmlEncode(model.TrainingEmail);
        var supportPhone = IPRO.Web.Infrastructure.PlatformContact.SupportPhone;   // 496
        var websiteUrl = WebUtility.HtmlEncode(model.WebsiteUrl);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
</head>
<body style="margin:0;background:#edf2f8;color:#172033;font-family:Segoe UI,Roboto,Arial,sans-serif;">
<div style="display:none;max-height:0;overflow:hidden;">Your IPRO Advisers account has been created.</div>
<div style="max-width:720px;margin:0 auto;padding:28px 14px;">
  <div style="background:#fff;border:1px solid #dce3ef;border-radius:18px;overflow:hidden;box-shadow:0 18px 50px rgba(18,38,73,.14);">
    <div style="padding:28px 32px;background:linear-gradient(135deg,#0c1d38,#1556d7);color:#fff;">
      <div style="font-size:13px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#b9d2ff;">IPRO Advisers</div>
      <h1 style="margin:8px 0 0;font-size:28px;line-height:1.15;">Welcome, {{name}}</h1>
      <p style="margin:8px 0 0;color:#dce8ff;font-size:15px;">Your account registration is complete.</p>
    </div>
    <div style="padding:30px 32px;">
      <p style="font-size:16px;line-height:1.55;margin:0 0 16px;">We are pleased that you have decided to use IPRO Advisers. Your account gives you access to tools designed to help you manage, follow up, prospect, service, and attract new clients.</p>

      <div style="background:#f6f9ff;border:1px solid #d8e5ff;border-radius:14px;padding:18px;margin:22px 0;">
        <div style="font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#556987;margin-bottom:6px;">Temporary Website</div>
        <a href="{{domainUrl}}" style="font-size:17px;font-weight:800;color:#1457d9;text-decoration:none;">{{domainUrl}}</a>
        <p style="margin:10px 0 0;color:#667085;font-size:14px;line-height:1.45;">Your website is already written and published at this address. It goes live the moment your subscription is active, and you can attach your own registered domain later from the control panel.</p>
      </div>

      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;margin:22px 0;">
        <tr>
          <td style="width:50%;padding:16px;border:1px solid #dce3ef;border-radius:12px;background:#fbfcff;">
            <div style="font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#667085;">Username</div>
            <div style="font-size:18px;font-weight:800;margin-top:6px;color:#172033;">{{userName}}</div>
          </td>
          <td style="width:14px;"></td>
          <td style="width:50%;padding:16px;border:1px solid #dce3ef;border-radius:12px;background:#fbfcff;">
            <div style="font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#667085;">{{passwordCellTitle}}</div>
            <div style="font-size:18px;font-weight:800;margin-top:6px;color:#172033;">{{passwordCellValue}}</div>
          </td>
        </tr>
      </table>

      <p style="font-size:15px;line-height:1.55;color:#344054;margin:0 0 16px;">{{passwordNote}}</p>
      <p style="font-size:15px;line-height:1.55;color:#344054;margin:0 0 24px;">Each section of your portal has a help guide to get you started. For help, call {{supportPhone}}; for training, contact <a href="mailto:{{trainingEmail}}" style="color:#1457d9;">{{trainingEmail}}</a>.</p>

      <div style="text-align:center;margin:28px 0;">
        <a href="{{domainUrl}}" style="display:inline-block;background:#1457d9;color:#fff;text-decoration:none;font-weight:800;border-radius:999px;padding:13px 24px;">Open Your Temporary Website</a>
      </div>

      <p style="margin:28px 0 0;color:#667085;font-size:14px;">IPRO Advisers Management<br/><a href="https://{{websiteUrl}}" style="color:#1457d9;">{{websiteUrl}}</a></p>
    </div>
  </div>
</div>
</body>
</html>
""";
    }

    // 501: the plain-text alternative says what the HTML says. Until now it was still the legacy letter
    // ("CONGRATULATIONS! ... one of the most exciting and unique set of tools available on the Internet
    // today for professional advisors"), with an http:// link, no phone number and a promise of a
    // training "session". Few mail clients show this part, but filters read both, and a text part that
    // disagrees with the HTML is a small mark against the message.
    public static string BuildText(RegistrationWelcomeModel model)
    {
        var hasTempPassword = !string.IsNullOrWhiteSpace(model.TemporaryPassword);
        var builder = new StringBuilder();
        builder.AppendLine($"Welcome, {model.FullName}");
        builder.AppendLine();
        builder.AppendLine("Your IPRO Advisers account registration is complete. Your account gives you access to tools designed to help you manage, follow up, prospect, service, and attract new clients.");
        builder.AppendLine();
        builder.AppendLine($"Your temporary website: https://{model.SetupDomain}");
        builder.AppendLine("Your website is already written and published at this address. It goes live the moment your subscription is active, and you can attach your own registered domain later from the control panel.");
        builder.AppendLine();
        builder.AppendLine($"Username: {model.UserName}");
        builder.AppendLine(hasTempPassword
            ? $"Temporary password: {model.TemporaryPassword}"
            : "Password: the one you chose at signup");
        builder.AppendLine();
        builder.AppendLine(hasTempPassword
            ? "For your security, you will be asked to change this temporary password the first time you sign in."
            : "You sign in with your username or your email address, and the password you chose when you registered. It was never sent by email.");
        builder.AppendLine();
        builder.AppendLine($"Each section of your portal has a help guide to get you started. For help, call {IPRO.Web.Infrastructure.PlatformContact.SupportPhone}; for training, contact {model.TrainingEmail}.");
        builder.AppendLine();
        builder.AppendLine("IPRO Advisers Management");
        builder.AppendLine(model.WebsiteUrl);
        return builder.ToString();
    }

    public static RegistrationWelcomeModel Sample() => new()
    {
        FullName = "Firstname Lastname",
        Email = "user@example.com",
        UserName = "FirstnameLastname",
        TemporaryPassword = "Lastname",
        SetupDomain = "FirstnameLastname.245Advisers.com"
    };
}
