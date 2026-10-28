namespace IPRO.Web.Models;

// 593: the pages of two-step sign-in.

// The code page between the password and the portal.
public class TwoStepVerifyViewModel
{
    public string Method { get; set; } = IPRO.Entities.TwoStepMethods.App;
    public string MaskedEmail { get; set; } = string.Empty;
    public bool IsTeamMember { get; set; }
    public string? Error { get; set; }
    public string? Notice { get; set; }
}

// The person's own two-step page (signed in).
public class TwoStepStatusViewModel
{
    public bool IsOn { get; set; }
    public string Method { get; set; } = string.Empty;
    public DateTime? EnabledAt { get; set; }
    public int RecoveryCodesLeft { get; set; }
    public bool Required { get; set; }
    public bool IsTeamMember { get; set; }
    public string MaskedEmail { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
}

// Setting it up: choose, then the QR code (app) or the emailed code (email), then confirm.
public class TwoStepSetupViewModel
{
    // null = choose a way; "App"; "Email".
    public string? Method { get; set; }
    public string SetupToken { get; set; } = string.Empty;
    public string QrSvg { get; set; } = string.Empty;
    public string ManualKey { get; set; } = string.Empty;
    public bool EmailSent { get; set; }
    public string MaskedEmail { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? Error { get; set; }
}

// Shown once: right after set-up, or after new ones are made.
public class TwoStepRecoveryCodesViewModel
{
    public IReadOnlyList<string> Codes { get; set; } = Array.Empty<string>();
    public bool JustTurnedOn { get; set; }
}
