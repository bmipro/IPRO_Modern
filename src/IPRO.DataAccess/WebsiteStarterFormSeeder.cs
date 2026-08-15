using System.Linq;
using System.Threading.Tasks;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPRO.DataAccess;

// A small starter library of ready-to-use forms -- a couple universal, a couple per vertical --
// so an agent can attach a Custom Form to a page without building one from scratch first. Adopting
// a template (Forms/AdoptTemplate in IPRO.Web) copies it into a real, independent WebsiteForm the
// agent owns and can freely edit; nothing here is ever referenced live by an agent's copy.
public static class WebsiteStarterFormSeeder
{
    public static async Task SeedAsync(IPRODbContext db, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        await SeedGuard.RunAsync(db, "WebsiteStarterForms", logger, async () =>
        {
            // Per-template, not "table has any rows" -- see WebsiteStarterArticleSeeder for why a
            // blanket AnyAsync() guard would silently block any future template additions once this
            // table is non-empty.
            var existingKeys = (await db.WebsiteStarterForms
                    .Select(f => new { f.BusinessType, f.Title })
                    .ToListAsync())
                .Select(f => (f.BusinessType, f.Title))
                .ToHashSet();

            var desired = new[]
            {
                Template("All", "General Inquiry",
                    "A simple catch-all for visitors who aren't sure what to ask for yet.", 0,
                    Field(WebsiteFormFieldTypes.Textarea, "How can we help you?", required: true),
                    Field(WebsiteFormFieldTypes.Dropdown, "Preferred contact method", required: true, options: new[] { "Email", "Phone", "Either" }),
                    Field(WebsiteFormFieldTypes.Text, "Best time to reach you")),

                Template("All", "Refer a Friend",
                    "Makes it easy for a happy client to send someone your way.", 1,
                    Field(WebsiteFormFieldTypes.Section, "Friend's Information"),
                    Field(WebsiteFormFieldTypes.Text, "Friend's Name", required: true),
                    Field(WebsiteFormFieldTypes.Text, "Friend's Email or Phone", required: true),
                    Field(WebsiteFormFieldTypes.Text, "Your Relationship to Them"),
                    Field(WebsiteFormFieldTypes.Textarea, "Anything else we should know?")),

                Template("Insurance / Financial", "Insurance Quote Request",
                    "Gathers the basics needed before a coverage conversation.", 0,
                    Field(WebsiteFormFieldTypes.Dropdown, "Type of Coverage You're Interested In", required: true, options: new[] { "Life", "Health", "Auto", "Home", "Disability", "Other" }),
                    Field(WebsiteFormFieldTypes.Dropdown, "Do you currently have coverage?", required: true, options: new[] { "Yes", "No", "Not Sure" }),
                    Field(WebsiteFormFieldTypes.Text, "Number of Dependents"),
                    Field(WebsiteFormFieldTypes.Textarea, "Anything specific you'd like us to know?")),

                Template("Insurance / Financial", "Financial Planning Discovery",
                    "A first-pass look at a prospective client's goals before a planning meeting.", 1,
                    Field(WebsiteFormFieldTypes.Dropdown, "Primary Goal", required: true, options: new[] { "Retirement Planning", "Investment Growth", "Debt Reduction", "Estate Planning", "Other" }),
                    Field(WebsiteFormFieldTypes.Text, "Target Timeline"),
                    Field(WebsiteFormFieldTypes.Dropdown, "Do you currently work with a financial advisor?", required: true, options: new[] { "Yes", "No" }),
                    Field(WebsiteFormFieldTypes.Textarea, "Tell us about your current financial situation")),

                Template("Mortgage", "Mortgage Pre-Approval Request",
                    "For a visitor who's ready to start the pre-approval conversation.", 0,
                    Field(WebsiteFormFieldTypes.Dropdown, "I am looking to", required: true, options: new[] { "Buy a Home", "Refinance", "Renew", "Access Home Equity" }),
                    Field(WebsiteFormFieldTypes.Text, "Estimated Purchase Price or Property Value"),
                    Field(WebsiteFormFieldTypes.Text, "Estimated Down Payment"),
                    Field(WebsiteFormFieldTypes.Dropdown, "Are you a first-time home buyer?", required: true, options: new[] { "Yes", "No" }),
                    Field(WebsiteFormFieldTypes.Textarea, "Anything else we should know?")),

                Template("Mortgage", "Renewal / Refinance Inquiry",
                    "For an existing homeowner exploring their options.", 1,
                    Field(WebsiteFormFieldTypes.Text, "Current Mortgage Balance (approximate)"),
                    Field(WebsiteFormFieldTypes.Text, "Current Lender"),
                    Field(WebsiteFormFieldTypes.Text, "Renewal or Maturity Date (if known)"),
                    Field(WebsiteFormFieldTypes.Dropdown, "What are you hoping to achieve?", required: true, options: new[] { "Lower My Rate", "Access Equity", "Consolidate Debt", "Just Exploring Options" }),
                    Field(WebsiteFormFieldTypes.Textarea, "Additional details")),

                Template("Accountants", "New Client Intake",
                    "A first look at a prospective client's business and needs.", 0,
                    Field(WebsiteFormFieldTypes.Section, "Business Information"),
                    Field(WebsiteFormFieldTypes.Text, "Business Name (if applicable)"),
                    Field(WebsiteFormFieldTypes.Dropdown, "Business Structure", required: true, options: new[] { "Sole Proprietor", "Partnership", "Corporation", "Not Applicable", "Not Sure" }),
                    Field(WebsiteFormFieldTypes.Section, "Services Needed"),
                    Field(WebsiteFormFieldTypes.CheckboxGroup, "Which services are you interested in?", required: true, options: new[] { "Bookkeeping", "Payroll", "Tax Preparation", "Financial Statements", "Business Advisory", "Other" }),
                    Field(WebsiteFormFieldTypes.Text, "Current Bookkeeping Software (if any)"),
                    Field(WebsiteFormFieldTypes.Textarea, "Tell us more about your needs")),

                Template("Accountants", "Book a Tax Consultation",
                    "A quick way for a client to request a tax appointment.", 1,
                    Field(WebsiteFormFieldTypes.Dropdown, "Filing Type", required: true, options: new[] { "Personal", "Business", "Both" }),
                    Field(WebsiteFormFieldTypes.Dropdown, "Do you have your tax slips/documents ready?", required: true, options: new[] { "Yes", "No", "Not sure what I need" }),
                    Field(WebsiteFormFieldTypes.Text, "Preferred Appointment Timeframe"),
                    Field(WebsiteFormFieldTypes.Textarea, "Anything specific to flag before we meet?")),

                // "Request a Meeting" family, one per vertical -- adapted from the owner's reference
                // designs (2026-08-15). These are what provisioning wires onto every new agent's
                // /request-meeting page (WebsiteStarterPagesHelper matches on this exact title), so
                // renaming one here without updating the helper downgrades new signups to the plain
                // contact block. Name/email/phone are NOT fields: the public renderer already builds
                // those in. The Insurance and Financial reference forms are deliberately merged --
                // Insurance / Financial is one vertical in this product.
                MeetingTemplate("All",
                    "Tell us what you would like to talk through, pick a time that suits you, and we will confirm your meeting within one business day.",
                    Field(WebsiteFormFieldTypes.Dropdown, "What would you like to discuss?", required: true,
                        options: new[] { "Getting started as a new client", "Reviewing my current situation", "A specific question", "Something else" })),

                MeetingTemplate("Insurance / Financial",
                    "Protect what matters and plan what is next. Tell us what you would like to cover and we will confirm your meeting within one business day.",
                    Field(WebsiteFormFieldTypes.Dropdown, "What would you like to discuss?", required: true,
                        options: new[] { "Life insurance (term or permanent)", "Income protection / disability", "Critical illness coverage", "Reviewing an existing policy", "Retirement planning", "Wealth & investment management", "Estate & generational planning", "Tax-efficient investing", "Something else" }),
                    Field(WebsiteFormFieldTypes.Dropdown, "Who is this for?", required: true,
                        options: new[] { "Myself", "My family", "My business or employees" })),

                MeetingTemplate("Mortgage",
                    "Take 60 seconds to request your mortgage consultation. We will confirm your meeting details within one business day.",
                    Field(WebsiteFormFieldTypes.Dropdown, "What are you looking to do?", required: true,
                        options: new[] { "Buy my first home", "Buy my next home / move", "Refinance or renew", "Invest in property" }),
                    Field(WebsiteFormFieldTypes.Dropdown, "Employment status", required: true,
                        options: new[] { "Full-time employee", "Self-employed / business owner", "Contract / temporary", "Retired" }),
                    Field(WebsiteFormFieldTypes.Text, "Estimated purchase price or property value", placeholder: "e.g. $450,000")),

                MeetingTemplate("Accountants",
                    "Tell us about your business or tax situation and we will confirm your meeting details within one business day.",
                    Field(WebsiteFormFieldTypes.Dropdown, "Business / entity type", required: true,
                        options: new[] { "Individual / employed", "Sole proprietor / freelancer", "Partnership", "Corporation" }),
                    Field(WebsiteFormFieldTypes.Dropdown, "Service needed", required: true,
                        options: new[] { "Annual tax preparation & filing", "Ongoing bookkeeping & payroll", "Corporate advisory / fractional CFO", "CRA / tax authority audit assistance" }),
                    Field(WebsiteFormFieldTypes.Dropdown, "Estimated annual business revenue (if applicable)",
                        options: new[] { "Pre-revenue / startup", "Under $100,000", "$100,000 - $500,000", "$500,000 - $2,000,000", "Over $2,000,000" })),
            };

            var missing = desired.Where(t => !existingKeys.Contains((t.BusinessType, t.Title))).ToList();
            if (missing.Count == 0) return;

            foreach (var template in missing)
            {
                var form = new WebsiteStarterForm
                {
                    BusinessType = template.BusinessType,
                    Title = template.Title,
                    Description = template.Description,
                    SubmitButtonText = template.SubmitButtonText,
                    SuccessMessage = template.SuccessMessage,
                    SortOrder = template.SortOrder,
                    IsActive = true
                };
                db.WebsiteStarterForms.Add(form);
                await db.SaveChangesAsync();

                var fieldOrder = 0;
                foreach (var fieldSeed in template.Fields)
                {
                    var field = new WebsiteStarterFormField
                    {
                        WebsiteStarterFormId = form.Id,
                        FieldType = fieldSeed.FieldType,
                        Label = fieldSeed.Label,
                        Placeholder = fieldSeed.Placeholder,
                        HelpText = fieldSeed.HelpText,
                        IsRequired = fieldSeed.IsRequired,
                        SortOrder = fieldOrder++
                    };
                    db.WebsiteStarterFormFields.Add(field);
                    await db.SaveChangesAsync();

                    if (fieldSeed.Options.Length == 0) continue;

                    var optionOrder = 0;
                    foreach (var optionText in fieldSeed.Options)
                    {
                        db.WebsiteStarterFormFieldOptions.Add(new WebsiteStarterFormFieldOption
                        {
                            WebsiteStarterFormFieldId = field.Id,
                            Text = optionText,
                            SortOrder = optionOrder++
                        });
                    }
                    await db.SaveChangesAsync();
                }
            }
        });

    private static FormTemplate Template(string businessType, string title, string description, int sortOrder, params FieldSeed[] fields) =>
        new(businessType, title, description, sortOrder, fields);

    // Vertical-specific fields go first; every meeting form then shares the same logistics tail
    // (format, preferred time, notes) so the family stays consistent as verticals are added.
    private static FormTemplate MeetingTemplate(string businessType, string description, params FieldSeed[] verticalFields) =>
        new(businessType, MeetingFormTitle, description, 2,
            verticalFields.Concat(new[]
            {
                Field(WebsiteFormFieldTypes.Dropdown, "Preferred meeting format", required: true,
                    options: new[] { "Video call (Zoom / Teams)", "Phone call", "In person at the office" }),
                Field(WebsiteFormFieldTypes.DateTime, "Preferred date & time", required: true,
                    helpText: "We will confirm or suggest the closest available time."),
                Field(WebsiteFormFieldTypes.Textarea, "Anything else we should know?",
                    placeholder: "Timelines, specific concerns, or anything you would like us to prepare for..."),
            }).ToArray())
        {
            SubmitButtonText = "Request my meeting",
            SuccessMessage = "Thank you - your meeting request is in. We will confirm the details by email within one business day."
        };

    public const string MeetingFormTitle = "Request a Meeting";

    private static FieldSeed Field(string fieldType, string label, string placeholder = "", string helpText = "", bool required = false, string[]? options = null) =>
        new(fieldType, label, placeholder, helpText, required, options ?? System.Array.Empty<string>());

    private record FormTemplate(string BusinessType, string Title, string Description, int SortOrder, FieldSeed[] Fields)
    {
        public string SubmitButtonText { get; init; } = "Submit";
        public string SuccessMessage { get; init; } = "Thank you. Your response was sent.";
    }

    private record FieldSeed(string FieldType, string Label, string Placeholder, string HelpText, bool IsRequired, string[] Options);
}
