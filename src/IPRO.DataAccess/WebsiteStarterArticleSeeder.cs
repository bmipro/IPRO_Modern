using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// A small, real starter set per vertical -- not a port of the full Word-doc content library
// (X:\ipro_related), which is a separate, much larger effort. This exists so a brand-new agent's
// "Resources" nav item is never empty on day one.
public static class WebsiteStarterArticleSeeder
{
    public static async Task SeedAsync(IPRODbContext db, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        await SeedGuard.RunAsync(db, "WebsiteStarterArticles", logger, async () =>
        {
            if (await db.WebsiteStarterArticles.AnyAsync()) return;

            db.WebsiteStarterArticles.AddRange(
                Article("All", "How to Get the Most Out of Your First Meeting",
                    "A few minutes of preparation before your first meeting means a more useful conversation and a clearer next step.",
                    "<p>Your first meeting is really just a conversation about where things stand and where you'd like to go. You don't need to have everything figured out beforehand &mdash; that's what we're here to help with &mdash; but a little preparation goes a long way.</p>" +
                    "<p>Before we meet, it helps to jot down what prompted you to reach out. Is there a specific decision on the horizon, or are you looking for a general check-in? Having a rough list of questions, even informal ones, keeps the conversation focused on what actually matters to you.</p>" +
                    "<p>It's also useful to think about your priorities in plain terms &mdash; not the \"right\" technical answer, just what feels most important to you right now. We'll bring the expertise; you bring the context only you have.</p>" +
                    "<p>There's no such thing as a question that's too basic. The goal of that first conversation is simply to understand your situation clearly enough to recommend a sensible next step &mdash; nothing more.</p>", 0),
                Article("All", "Questions Worth Asking Before You Choose an Advisor",
                    "A short list of questions that help you evaluate any professional you're considering working with, including us.",
                    "<p>Choosing who to work with on something important is worth a little diligence, whatever kind of advice you're looking for. Here are a few questions that tend to matter more than they first appear.</p>" +
                    "<ul>" +
                    "<li><strong>How do you communicate, and how often?</strong> Some relationships need frequent check-ins; others just need to know someone is paying attention.</li>" +
                    "<li><strong>What happens after the first meeting?</strong> A good next step should be concrete, not vague.</li>" +
                    "<li><strong>How are you compensated?</strong> Understanding this up front avoids surprises later.</li>" +
                    "<li><strong>What do you do when something doesn't go as planned?</strong> How someone handles the unexpected tells you a lot.</li>" +
                    "</ul>" +
                    "<p>We're always happy to answer these directly &mdash; feel free to ask any of them when we speak.</p>", 1),

                Article("Insurance / Financial", "Do You Actually Have Enough Life Insurance?",
                    "Most coverage amounts are guesses inherited from an old policy. Here's a simple way to sanity-check yours.",
                    "<p>Most people's life insurance coverage traces back to a number chosen years ago &mdash; a multiple of salary suggested at a previous job, or a round number that felt sufficient at the time. Life changes; policies often don't keep up.</p>" +
                    "<p>A useful starting point is to add up what your family would actually need to cover: remaining debts like a mortgage, a number of years of household expenses, and any future costs you'd want fully funded, such as education. Then subtract what's already covered by savings, existing policies, or workplace benefits.</p>" +
                    "<p>The gap between those two numbers is a much more grounded starting point than a generic rule of thumb. It's also worth revisiting after any major life event &mdash; a new home, a new child, a change in income &mdash; since each of these shifts the number meaningfully.</p>" +
                    "<p>If it's been a few years since you last looked at this, it's worth ten minutes of your time. We're glad to walk through it together.</p>", 0),
                Article("Insurance / Financial", "RRSP or TFSA: Which Should You Prioritize?",
                    "Both accounts are useful, but the right order to fund them depends on your income today versus your expected income later.",
                    "<p>The RRSP-versus-TFSA question comes up often, and the honest answer is: it depends on your situation, particularly your income now compared to your expected income when you'd withdraw the money.</p>" +
                    "<p>An RRSP contribution reduces your taxable income today and grows tax-deferred, but withdrawals are taxed later. That makes it especially effective when you're in a higher tax bracket now than you expect to be in retirement &mdash; the deduction is worth more today than the tax owed later.</p>" +
                    "<p>A TFSA works differently: contributions don't reduce your income today, but growth and withdrawals are entirely tax-free. That flexibility makes it a strong choice when you're in a lower tax bracket now, or when you might need access to the money before retirement without a tax consequence.</p>" +
                    "<p>In practice, many people benefit from using both, in a mix that shifts over time as income changes. There isn't a single correct answer &mdash; only the answer that fits your own numbers, which we're happy to work through with you.</p>", 1),

                Article("Mortgage", "Fixed or Variable: Choosing the Right Mortgage Rate",
                    "The right rate type depends less on predicting interest rates and more on how much certainty you want in your monthly budget.",
                    "<p>The fixed-versus-variable decision is one of the most common questions we hear, and it's less about predicting where rates are headed than about how much certainty you want in your monthly budget.</p>" +
                    "<p>A fixed rate locks your payment for the term, which makes budgeting simple and removes the stress of rate movements entirely. The tradeoff is that you don't benefit if rates fall, and breaking a fixed-rate mortgage early can carry a larger penalty.</p>" +
                    "<p>A variable rate moves with the market. Historically it has often cost less over the life of a mortgage, but that comes with genuine month-to-month uncertainty, and payments can rise if rates do. Many variable products also offer more flexibility if you need to break the term early.</p>" +
                    "<p>There's no universally right choice &mdash; it comes down to your tolerance for payment fluctuation and your broader financial picture. We're glad to walk through both scenarios with your actual numbers so the decision feels less like a guess.</p>", 0),
                Article("Mortgage", "What First-Time Buyers Should Know About Pre-Approval",
                    "Pre-approval is more than a formality &mdash; it tells you what you can actually afford before you start looking.",
                    "<p>If you're buying your first home, getting pre-approved before you start house-hunting is one of the most useful early steps you can take &mdash; and one of the most commonly skipped.</p>" +
                    "<p>Pre-approval gives you a realistic sense of what you can actually afford, based on your real income, debts, and credit &mdash; not a rough estimate from an online calculator. It also typically locks in a rate for a set period, protecting you if rates move while you're searching.</p>" +
                    "<p>Just as importantly, a pre-approval signals to sellers that you're a serious, qualified buyer, which can matter in a competitive market. It's worth noting that pre-approval isn't a guarantee &mdash; final approval still depends on the specific property and a full review of your finances &mdash; but it removes most of the uncertainty going in.</p>" +
                    "<p>If you're starting to think about buying, this is a good first conversation to have, well before you've found a place you love.</p>", 1),

                Article("Accountants", "A Simple Year-End Checklist for Small Business Owners",
                    "A short list of things worth reviewing before the calendar year closes, while there's still time to act on them.",
                    "<p>Year-end goes smoothly when a few things are handled before the calendar actually closes, rather than after. Here's a short list worth reviewing while there's still time to act.</p>" +
                    "<ul>" +
                    "<li><strong>Reconcile your accounts.</strong> Make sure your books actually match your bank and credit card statements.</li>" +
                    "<li><strong>Review outstanding invoices.</strong> Chase anything overdue before it becomes next year's problem.</li>" +
                    "<li><strong>Confirm expense categorization.</strong> Miscategorized expenses are one of the most common sources of avoidable tax cost.</li>" +
                    "<li><strong>Check in on major purchases.</strong> Timing a purchase before or after year-end can matter for tax planning.</li>" +
                    "<li><strong>Set aside time to talk with us.</strong> A short planning conversation before year-end often creates options that simply don't exist afterward.</li>" +
                    "</ul>" +
                    "<p>None of this needs to take long, but doing it in December rather than April makes a real difference.</p>", 0),
                Article("Accountants", "Bookkeeping Habits That Pay Off at Tax Time",
                    "A handful of small, consistent habits through the year make tax season faster, cheaper, and far less stressful.",
                    "<p>Good bookkeeping isn't about doing more work &mdash; it's about doing a small amount of consistent work throughout the year, instead of a large amount all at once in April.</p>" +
                    "<p>A few habits make an outsized difference: keeping business and personal expenses in separate accounts, recording expenses as they happen rather than reconstructing them later, and holding onto receipts in a single, consistent place rather than several inconsistent ones.</p>" +
                    "<p>It's also worth reviewing your numbers monthly, even briefly. Catching an error or an unusual expense in the month it happened is far easier than tracing it back twelve months later. The same goes for invoicing: sending invoices promptly and following up on overdue ones keeps your books &mdash; and your cash flow &mdash; accurate in real time.</p>" +
                    "<p>None of this requires new software or a big process overhaul. Small, consistent habits are what actually make tax season faster, cheaper, and considerably less stressful.</p>", 1)
            );

            await db.SaveChangesAsync();
        });

    private static WebsiteStarterArticle Article(string businessType, string title, string summary, string content, int order) => new()
    {
        BusinessType = businessType,
        Title = title,
        Summary = summary,
        Content = content,
        IsActive = true,
        SortOrder = order
    };
}
