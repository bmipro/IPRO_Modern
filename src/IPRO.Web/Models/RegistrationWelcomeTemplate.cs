using System.Net;
using System.Text;

namespace IPRO.Web.Models;

public static class RegistrationWelcomeTemplate
{
    public static string BuildHtml(RegistrationWelcomeModel model)
    {
        var name = WebUtility.HtmlEncode(model.FullName);
        var userName = WebUtility.HtmlEncode(model.UserName);
        var password = WebUtility.HtmlEncode(model.TemporaryPassword);
        var domain = WebUtility.HtmlEncode(model.SetupDomain);
        var domainUrl = $"http://{domain}";
        var trainingEmail = WebUtility.HtmlEncode(model.TrainingEmail);
        var websiteUrl = WebUtility.HtmlEncode(model.WebsiteUrl);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
</head>
<body style="margin:0;background:#edf2f8;color:#172033;font-family:Segoe UI,Roboto,Arial,sans-serif;">
<div style="display:none;max-height:0;overflow:hidden;">Your IPRO Advisers account has been created. Save your temporary username and password.</div>
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
        <p style="margin:10px 0 0;color:#667085;font-size:14px;line-height:1.45;">You can use this temporary domain right away and later attach your own registered domain from the control panel.</p>
      </div>

      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;margin:22px 0;">
        <tr>
          <td style="width:50%;padding:16px;border:1px solid #dce3ef;border-radius:12px;background:#fbfcff;">
            <div style="font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#667085;">Username</div>
            <div style="font-size:18px;font-weight:800;margin-top:6px;color:#172033;">{{userName}}</div>
          </td>
          <td style="width:14px;"></td>
          <td style="width:50%;padding:16px;border:1px solid #dce3ef;border-radius:12px;background:#fbfcff;">
            <div style="font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#667085;">Temporary Password</div>
            <div style="font-size:18px;font-weight:800;margin-top:6px;color:#172033;">{{password}}</div>
          </td>
        </tr>
      </table>

      <p style="font-size:15px;line-height:1.55;color:#344054;margin:0 0 16px;">For your security, you will be asked to change this temporary password the first time you sign in.</p>
      <p style="font-size:15px;line-height:1.55;color:#344054;margin:0 0 24px;">We recommend visiting the video tutorials in each admin section to get familiar with your tools. For training, contact <a href="mailto:{{trainingEmail}}" style="color:#1457d9;">{{trainingEmail}}</a>.</p>

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

    public static string BuildText(RegistrationWelcomeModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CONGRATULATIONS!");
        builder.AppendLine();
        builder.AppendLine($"Dear {model.FullName}");
        builder.AppendLine();
        builder.AppendLine("We are pleased that you have decided to use one of the most exciting and unique set of tools available on the Internet today for professional advisors.");
        builder.AppendLine();
        builder.AppendLine($"You can access your web site at this URL address: http://{model.SetupDomain}");
        builder.AppendLine();
        builder.AppendLine("This is your temporary website domain but you can add your own domain or use your temporary one as much as you want.");
        builder.AppendLine();
        builder.AppendLine("Please log in to your admin section of your website.");
        builder.AppendLine();
        builder.AppendLine($"Your username. : {model.UserName}");
        builder.AppendLine($"Your Password: {model.TemporaryPassword}");
        builder.AppendLine();
        builder.AppendLine("Please contact our training department through: training@IProAdvisers.com in order to book a seat for our next available training session.");
        builder.AppendLine();
        builder.AppendLine("IPro Management");
        builder.AppendLine(model.WebsiteUrl);
        return builder.ToString();
    }

    public static RegistrationWelcomeModel Sample() => new()
    {
        FullName = "Masoud Zangeneh",
        Email = "bmotamed@yahoo.com",
        UserName = "MasoudZangeneh",
        TemporaryPassword = "zangeneh",
        SetupDomain = "MasoudZangeneh.247advisers.com"
    };
}
