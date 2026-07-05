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
<html>
<head>
<meta charset="utf-8"/>
<style>
body{font-family:"Times New Roman",Times,serif;font-size:13px;color:#000;background:#fff;}
.letter{width:680px;margin:0 auto;padding:24px 16px;}
.green{display:inline-block;background:#d8ef57;font-weight:bold;padding:1px 3px;}
.bar{height:12px;background:#000;margin:12px 0;}
.orange{height:12px;background:#c53500;margin:8px 0 18px;}
.creds{font-weight:bold;display:flex;gap:80px;margin:16px 0;}
.footer{height:12px;background:#c53500;margin-top:18px;color:#fff;font-weight:bold;padding:1px 4px;}
p{line-height:1.25;margin:12px 0;}
</style>
</head>
<body>
<div class="letter">
  <div><span class="green">CONGRATULATIONS!</span></div>
  <div class="bar"></div>
  <div class="orange"></div>

  <p>Dear {{name}}</p>

  <p>We are pleased that you have decided to use one of the most exciting and unique set of tools available on the Internet today for professional advisors. iPro exceeds the functions of a regular website to provide you with features designed to create the best solution for your day to day challenges. You will get an effective partner that will help you manage, follow-up, prospect, service and attract new clients.</p>

  <p>Please take the time to explore the different tools available to you in your iPro package and experience firsthand how this tool box can help eliminate your challenges.</p>

  <p>Remember, you can always upgrade your package to a higher package in order to take advantage of the most advanced functions.</p>

  <p>You can access your web site at this URL address: <a href="{{domainUrl}}">{{domainUrl}}</a></p>

  <p>This is your temporary website domain but you can add your own domain or use your temporary one as much as you want.</p>

  <p>Please log in to your admin section of your website.</p>

  <div class="bar"></div>
  <div class="creds">
    <div>Your username. : {{userName}}</div>
    <div>Your Password: {{password}}</div>
  </div>
  <div class="bar"></div>

  <p>We recommend that you visit the video tutorials in every section of the admin section in order to get some idea about your fantastic set of tools.</p>

  <p>Please contact our training department through: <a href="mailto:{{trainingEmail}}">{{trainingEmail}}</a> in order to book a seat for our next available training session.</p>

  <p style="margin-top:36px;">IPro <em>Management</em></p>
  <div class="footer">{{websiteUrl}}</div>
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
