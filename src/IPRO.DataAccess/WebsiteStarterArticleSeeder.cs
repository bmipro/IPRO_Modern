using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// A small, real starter set per vertical. The Accountants vertical draws on the real content
// library at X:\ipro_related\IPro_accountants (see comment further below); Insurance/Financial and
// Mortgage remain the original small hand-written set. Exists so a brand-new agent's "Resources"
// nav item is never empty on day one.
public static class WebsiteStarterArticleSeeder
{
    public static async Task SeedAsync(IPRODbContext db, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        await SeedGuard.RunAsync(db, "WebsiteStarterArticles", logger, async () =>
        {
            // Per-article, not "table has any rows" -- the table was already seeded with the
            // original 8 articles before the Accountants expansion below was written, so a
            // blanket AnyAsync() guard would have silently skipped inserting any of the new ones
            // on every environment that had already run this seeder once.
            var existingKeys = (await db.WebsiteStarterArticles
                    .Select(a => new { a.BusinessType, a.Title })
                    .ToListAsync())
                .Select(a => (a.BusinessType, a.Title))
                .ToHashSet();

            var desired = new[]
            {
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

                // Accountants vertical: adapted from the real content library at X:\ipro_related\IPro_accountants
                // (a genuine former IPRO accountant client's site, authored 2013), grouped into the same four
                // sections its original side_menu.doc defined. "Accounts Payable" below is newly written, not
                // adapted -- the source .doc for it actually contained Accounts Receivable content, a leftover
                // copy-paste error from the original authoring. The six "Info Centre" pieces are newly written
                // in first-person practitioner voice rather than adapted -- the originals were verbatim,
                // unattributed-in-context Wikipedia paragraphs, some now stale (CGA merged into CPA in the 2010s).

                Article("Accountants", "Personal Financial Planning",
                    "A plan built around your actual goals and timeline, not a generic template.",
                    "<p>A financial plan is only useful if it reflects your actual life, not a generic template. Our team works with you to build a plan around your specific goals, timeline, and circumstances &mdash; one that adapts as your situation changes rather than sitting untouched in a drawer.</p>" +
                    "<p>Some of the most common moments worth planning around:</p>" +
                    "<ul>" +
                    "<li>A change in income or job loss</li>" +
                    "<li>Retirement planning</li>" +
                    "<li>The birth of a child or grandchild</li>" +
                    "<li>Major life events &mdash; marriage, divorce, a new home</li>" +
                    "<li>Large purchases, like a home or cottage</li>" +
                    "<li>Insurance coverage</li>" +
                    "<li>Saving and investing</li>" +
                    "<li>Managing short- and long-term debt</li>" +
                    "</ul>" +
                    "<p>None of these need to be handled alone. A short conversation is usually enough to see where you actually stand and what's worth prioritizing next.</p>",
                    0, "Personal Accounting"),
                Article("Accountants", "Personal Tax Preparation",
                    "A practical checklist of the slips and receipts to gather before your appointment, so nothing gets missed.",
                    "<p>We handle personal tax preparation for individuals and families, and we make sure you're claiming everything you're entitled to &mdash; not just what's easiest to file. Below is a practical checklist of what to bring to your appointment.</p>" +
                    "<p><strong>Slips</strong></p>" +
                    "<ul>" +
                    "<li>Employment income (T4)</li>" +
                    "<li>Employment insurance benefits (T4E)</li>" +
                    "<li>Interest, dividends, and investment income (T3, T5)</li>" +
                    "<li>Tuition paid</li>" +
                    "<li>Pensions and annuities (T4A)</li>" +
                    "<li>Social assistance received (T5007)</li>" +
                    "<li>Any other employment, bank, or government slips</li>" +
                    "<li>Rental income</li>" +
                    "</ul>" +
                    "<p><strong>Receipts</strong></p>" +
                    "<ul>" +
                    "<li>RRSP contribution slips</li>" +
                    "<li>Child support</li>" +
                    "<li>Professional fees or dues</li>" +
                    "<li>Charitable donations</li>" +
                    "<li>Childcare expenses</li>" +
                    "<li>Children's art, camp, and sports program fees</li>" +
                    "<li>Transit pass credit</li>" +
                    "<li>Medical expenses</li>" +
                    "<li>Political contributions</li>" +
                    "</ul>" +
                    "<p>Not sure if something on your desk counts? Bring it anyway &mdash; it's easier for us to set aside what you don't need than to chase down what's missing.</p>",
                    1, "Personal Accounting"),

                Article("Accountants", "General Ledger &amp; Chart of Accounts",
                    "The backbone of your books &mdash; every account, transaction, and balance an accounting system relies on.",
                    "<p>A general ledger is the record that holds every account related to your business's assets, liabilities, owner's equity, revenue, and expenses. In any accounting system, it's the central repository everything else feeds into &mdash; accounts payable, accounts receivable, payroll, and more.</p>" +
                    "<p>The list of account names is your chart of accounts. Pulling the balance of each one produces a trial balance, which exists to confirm one thing before financial statements are prepared: that total debits equal total credits.</p>" +
                    "<p>A well-kept general ledger usually breaks into seven main categories &mdash; assets, liabilities, owner's equity, revenue, expenses, gains, and losses &mdash; each of which can be further divided into sub-ledgers for things like cash, receivables, or payables.</p>" +
                    "<p>It sounds simple in principle. In practice, keeping it accurate as a business grows is exactly the kind of ongoing work we take off your plate.</p>",
                    0, "Business Accounting"),
                Article("Accountants", "Accounts Receivable",
                    "Getting paid on time depends on a system, not memory. Here's how we manage it for you.",
                    "<p>Accounts receivable is the money owed to your business for goods or services already delivered &mdash; typically tracked through invoices sent to customers, who then pay within agreed terms.</p>" +
                    "<p>Staying on top of receivables means more than sending invoices. It means knowing what's outstanding, following up before it becomes a problem, and applying payments accurately as they come in. In practice, we handle:</p>" +
                    "<ul>" +
                    "<li>Recording and accounting for revenue</li>" +
                    "<li>Preparing client bills and statements</li>" +
                    "<li>Credit and collections</li>" +
                    "<li>Entering and verifying receivable transactions</li>" +
                    "<li>Sending client-approved statements to end customers</li>" +
                    "<li>Maintaining pricing information and customer accounts</li>" +
                    "<li>Applying payments and resolving short pays</li>" +
                    "</ul>" +
                    "<p>Monthly reporting keeps you aware of what's outstanding before it becomes a cash flow issue &mdash; not after.</p>",
                    1, "Business Accounting"),
                Article("Accountants", "Accounts Payable",
                    "What you owe, tracked and paid on time &mdash; protecting your credit and your vendor relationships.",
                    "<p>Accounts payable is the flip side of receivables: it's what your business owes to suppliers and vendors for goods or services you've already received, but haven't yet paid for.</p>" +
                    "<p>Managed well, accounts payable protects two things at once &mdash; your cash flow, by making sure you're not paying earlier than you need to, and your vendor relationships, by making sure you're never paying later than you should. Both matter more than they might seem.</p>" +
                    "<p>In practice, we help you with:</p>" +
                    "<ul>" +
                    "<li>Recording and verifying vendor invoices against purchase orders</li>" +
                    "<li>Scheduling payments to match your cash flow, not just due dates</li>" +
                    "<li>Capturing early-payment discounts when they're worth taking</li>" +
                    "<li>Avoiding late fees and preserving your credit standing with suppliers</li>" +
                    "<li>Reconciling vendor statements against what's actually been paid</li>" +
                    "</ul>" +
                    "<p>A business that pays late erodes trust with the people it depends on. A business that pays too early gives up cash it could be using elsewhere. Getting the timing right is the whole job.</p>",
                    2, "Business Accounting"),
                Article("Accountants", "Bank Reconciliation",
                    "Making sure your books and your bank statement actually agree &mdash; and catching it quickly when they don't.",
                    "<p>A bank reconciliation explains the difference between the balance your bank shows and the balance your own books show at a given point in time. Differences are normal &mdash; a cheque that hasn't cleared yet, a bank fee that hasn't been recorded, or, occasionally, an error on either side.</p>" +
                    "<p>The value of reconciling regularly isn't just accuracy for its own sake. It's catching a problem &mdash; a missed transaction, a duplicate charge, an error &mdash; while it's still easy to trace, instead of months later when the trail has gone cold.</p>" +
                    "<p>Beyond your main operating account, we also reconcile:</p>" +
                    "<ul>" +
                    "<li>Inter-company and intra-company transactions</li>" +
                    "<li>Fixed assets and their associated ledger entries</li>" +
                    "<li>Accounts receivable</li>" +
                    "<li>Accounts payable</li>" +
                    "</ul>" +
                    "<p>We handle this on a regular cadence so small discrepancies never get the chance to become big ones.</p>",
                    3, "Business Accounting"),
                Article("Accountants", "Financial Statements",
                    "The three reports that show, clearly, how your business is actually performing.",
                    "<p>A financial statement is a formal record of your business's financial activity, prepared to give a clear picture of its position and performance &mdash; useful to you, to lenders, and to anyone else who needs to make a decision based on the numbers.</p>" +
                    "<p>Three statements do most of the work:</p>" +
                    "<ul>" +
                    "<li><strong>The income statement (P&amp;L)</strong> &mdash; what you earned and spent over a period</li>" +
                    "<li><strong>The balance sheet</strong> &mdash; what you own and owe at a point in time</li>" +
                    "<li><strong>The cash flow statement</strong> &mdash; how cash actually moved in and out</li>" +
                    "</ul>" +
                    "<p>We prepare these monthly, quarterly, or annually depending on what you need &mdash; and we're glad to walk through what they actually mean for your business, not just hand you the numbers.</p>",
                    4, "Business Accounting"),
                Article("Accountants", "Customized Reporting",
                    "The same bookkeeping data, shaped into whatever report actually helps you make a decision.",
                    "<p>Every transaction entered into your bookkeeping system can be pulled back out in a form that's actually useful to you &mdash; not just a standard template.</p>" +
                    "<p>We build custom reports for tracking and analysis, including detailed trial balances and general ledger views to support tax reporting, along with whatever specific breakdown helps you see your business clearly &mdash; by client, by project, by month, or however makes the most sense for how you actually run things.</p>" +
                    "<p>If a standard report isn't answering the question you're actually asking, tell us the question &mdash; we'll build the report around it.</p>",
                    5, "Business Accounting"),
                Article("Accountants", "Tax Preparation",
                    "Personal, business, and payroll tax filing &mdash; handled by people who take the time to understand your situation first.",
                    "<p>We provide a full range of tax services, from personal returns to business filing, tax planning, and representation. Before we file anything, we meet with you to understand your situation and what actually matters for your specific business.</p>" +
                    "<p>Services we offer include:</p>" +
                    "<ul>" +
                    "<li>Personal tax returns for business owners</li>" +
                    "<li>Sales and use tax returns for retail clients</li>" +
                    "<li>Monthly or quarterly returns for payroll clients</li>" +
                    "<li>Support for all necessary tax forms and schedules</li>" +
                    "</ul>" +
                    "<p>Have a question about your personal or business taxes? That's exactly what we're here for &mdash; reach out any time, not just at filing deadlines.</p>",
                    6, "Business Accounting"),
                Article("Accountants", "Payroll",
                    "One of the most time-consuming parts of running a business, handled accurately and on time, every time.",
                    "<p>Payroll is consistently one of the most time-consuming tasks in a business &mdash; and one of the least forgiving of mistakes. Outsourcing it frees up your time for work that actually grows the business, while making sure your team is paid accurately and on schedule.</p>" +
                    "<p>We manage:</p>" +
                    "<ul>" +
                    "<li>Calculating payroll remittances and net pay</li>" +
                    "<li>Setting up accounts and distributing direct deposits</li>" +
                    "<li>Generating and distributing pay stubs by email</li>" +
                    "<li>Filing and distributing T4 slips to you and your employees</li>" +
                    "<li>Year-end T4 filing</li>" +
                    "</ul>" +
                    "<p>Good employee morale depends on payroll being right, every time, without exception &mdash; that's the standard we hold ourselves to.</p>",
                    7, "Business Accounting"),
                Article("Accountants", "Should You Incorporate Your Small Business?",
                    "There's no universal right answer &mdash; but here are the real advantages worth weighing for your situation.",
                    "<p>Every small business owner eventually asks whether they should incorporate. There's no universal answer &mdash; it depends on your specific goals and circumstances &mdash; but here are the advantages worth weighing:</p>" +
                    "<ul>" +
                    "<li>Limited liability protection</li>" +
                    "<li>Continuity &mdash; the corporation carries on independent of its owners</li>" +
                    "<li>Easier access to raising capital</li>" +
                    "<li>More control over how and when you take income</li>" +
                    "<li>Potential tax deferral</li>" +
                    "<li>Income splitting opportunities</li>" +
                    "<li>Access to the small business tax deduction</li>" +
                    "<li>Added credibility that can support business growth</li>" +
                    "</ul>" +
                    "<p>Whether these advantages outweigh the added complexity depends entirely on your situation. We're glad to sit down and work through it with you before you decide.</p>",
                    8, "Business Accounting"),
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
                    "<p>None of this needs to take long, but doing it in December rather than April makes a real difference.</p>",
                    9, "Business Accounting"),
                Article("Accountants", "Bookkeeping Habits That Pay Off at Tax Time",
                    "A handful of small, consistent habits through the year make tax season faster, cheaper, and far less stressful.",
                    "<p>Good bookkeeping isn't about doing more work &mdash; it's about doing a small amount of consistent work throughout the year, instead of a large amount all at once in April.</p>" +
                    "<p>A few habits make an outsized difference: keeping business and personal expenses in separate accounts, recording expenses as they happen rather than reconstructing them later, and holding onto receipts in a single, consistent place rather than several inconsistent ones.</p>" +
                    "<p>It's also worth reviewing your numbers monthly, even briefly. Catching an error or an unusual expense in the month it happened is far easier than tracing it back twelve months later. The same goes for invoicing: sending invoices promptly and following up on overdue ones keeps your books &mdash; and your cash flow &mdash; accurate in real time.</p>" +
                    "<p>None of this requires new software or a big process overhaul. Small, consistent habits are what actually make tax season faster, cheaper, and considerably less stressful.</p>",
                    10, "Business Accounting"),

                Article("Accountants", "What Is a Chartered Professional Accountant (CPA)?",
                    "What the CA, CGA, and CMA designations became, and what it means when you see \"CPA\" after someone's name.",
                    "<p>If you've come across the designations CA, CGA, or CMA, you've seen the predecessors to today's unified Canadian accounting credential: the Chartered Professional Accountant, or CPA. The three legacy bodies merged into a single national designation over the course of the 2010s.</p>" +
                    "<p>A CPA has met rigorous education, examination, and experience requirements, and is required to maintain ongoing professional development to stay current &mdash; not a one-time credential, but an ongoing standard.</p>" +
                    "<p>CPAs work across every area of business and finance: audit, taxation, financial management, and general business advisory. Working with a CPA means working with someone held to a professional standard, not just someone who's good with numbers.</p>",
                    0, "Info Centre"),
                Article("Accountants", "What Is the CRA?",
                    "The federal agency behind Canada's tax system &mdash; and the one you're actually filing with every year.",
                    "<p>The Canada Revenue Agency (CRA) is the federal agency responsible for administering tax laws across Canada, for the federal government and most provinces and territories. Its role goes beyond collecting taxes &mdash; it also oversees charity registration, international trade rules, and various social and economic benefit programs delivered through the tax system.</p>" +
                    "<p>For most individuals and small businesses, the CRA is who you're actually dealing with at tax time &mdash; whether that's filing a return, responding to a request for information, or claiming a benefit you're entitled to. Having someone who understands how the CRA actually operates makes that relationship considerably less stressful.</p>",
                    1, "Info Centre"),
                Article("Accountants", "What Is the IRS?",
                    "The U.S. equivalent of the CRA &mdash; relevant if you have any American income, property, or business ties.",
                    "<p>The Internal Revenue Service (IRS) is the U.S. federal government's revenue agency, responsible for collecting taxes and enforcing the U.S. tax code. If you have American income, own U.S. property, or run a business with cross-border ties, IRS rules may apply to you even as a Canadian resident.</p>" +
                    "<p>Cross-border tax situations are genuinely more complicated than domestic ones &mdash; the two systems don't always align, and filing requirements can catch people off guard. If any part of your financial life crosses the border, it's worth a conversation before it becomes a surprise.</p>",
                    2, "Info Centre"),
                Article("Accountants", "What Is Payroll?",
                    "More than a paycheck &mdash; it's a legal and financial responsibility with real consequences for getting it wrong.",
                    "<p>Payroll is the total of what a business pays its employees &mdash; wages, salaries, bonuses, and the deductions that come off each paycheck. It sounds administrative, but it carries real weight: payroll and payroll taxes directly affect your net income, and they're governed by specific legal requirements around withholding and remittance.</p>" +
                    "<p>There's a human side too. Employees notice payroll errors immediately, and inconsistent or late pay is one of the fastest ways to damage morale. Getting payroll right &mdash; accurately, and on time, every time &mdash; is a baseline expectation, not a nice-to-have.</p>",
                    3, "Info Centre"),
                Article("Accountants", "What Is Tax, Really?",
                    "Less about the definition, more about what it actually means for how you run your business day to day.",
                    "<p>In the simplest terms, a tax is a mandatory payment to government, used to fund everything from roads and schools to healthcare and public services. That definition, though, isn't usually what a business owner actually needs to know.</p>" +
                    "<p>What matters in practice is how tax touches your business at every stage &mdash; income tax on profit, payroll tax on wages, sales tax on what you sell, and the timing decisions that affect how much of it you owe and when. None of that is optional, but a surprising amount of it is plannable if you're paying attention before the deadline, not just at it.</p>" +
                    "<p>That planning &mdash; not just the filing &mdash; is where we spend most of our time with clients.</p>",
                    4, "Info Centre"),

                Article("Accountants", "Which Calculator Do You Actually Need?",
                    "A quick guide to picking the right tool from our calculators &mdash; and what each one actually tells you.",
                    "<p>We've built a set of interactive calculators directly into this website to help you get real numbers, not just general advice. Here's a quick guide to which one answers which question.</p>" +
                    "<ul>" +
                    "<li><strong>Planning a purchase?</strong> A loan or mortgage calculator shows the real monthly cost, not just the sticker price.</li>" +
                    "<li><strong>Comparing loan offers?</strong> An APR calculator accounts for fees, not just the headline interest rate &mdash; the number that actually determines which offer is cheaper.</li>" +
                    "<li><strong>Deciding between payment schedules?</strong> A bi-weekly mortgage calculator shows how extra payments accelerate payoff and reduce total interest.</li>" +
                    "<li><strong>Thinking about savings or investments?</strong> A compound interest calculator shows how growth compounds over time &mdash; often surprising people about how much time itself is worth.</li>" +
                    "</ul>" +
                    "<p>The number a calculator gives you is a starting point, not a final answer &mdash; the real value comes from talking through what it means for your specific situation. That's what we're here for.</p>",
                    0, "Calculators")
            };

            var missing = desired.Where(a => !existingKeys.Contains((a.BusinessType, a.Title))).ToList();
            if (missing.Count == 0) return;

            db.WebsiteStarterArticles.AddRange(missing);
            await db.SaveChangesAsync();
        });

    private static WebsiteStarterArticle Article(string businessType, string title, string summary, string content, int order, string? category = null) => new()
    {
        BusinessType = businessType,
        Category = category,
        Title = title,
        Summary = summary,
        Content = content,
        IsActive = true,
        SortOrder = order
    };
}
