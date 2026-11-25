namespace IPRO.Web.Infrastructure;

// 678 (audit 2026-11-24 D-4, the owner 2026-11-25 "Double opt-in YES"): what a visitor reads after a newsletter
// sign-up or a Did You Know request. They are not subscribed, and no article is sent, until they press the button in
// the email that has just gone to them (SignupConfirmations), so the page says so.
public static class SignupThanks
{
    public const string Newsletter =
        "Almost done: we have sent you an email. Press the button in it to confirm your subscription.";

    public const string NewsletterFrench =
        "Presque terminé : nous venons de vous envoyer un courriel. Appuyez sur le bouton qu'il contient pour confirmer votre inscription.";

    public const string DidYouKnow =
        "Thanks! We have sent you an email: press the button in it, and the article(s) you asked for follow, one at a time.";
}
