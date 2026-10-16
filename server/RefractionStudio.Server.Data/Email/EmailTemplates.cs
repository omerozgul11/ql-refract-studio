using System.Globalization;
using System.Net;
using System.Text;

namespace RefractionStudio.Server.Data.Email;

/// <summary>Branding and links used in customer e-mails (section "Email").</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Company name in signatures.</summary>
    public string CompanyName { get; set; } = "QuakeLogic Inc.";

    /// <summary>Support address shown to customers.</summary>
    public string SupportAddress { get; set; } = "support@quakelogic.net";

    /// <summary>Where customers download the installer.</summary>
    public string DownloadUrl { get; set; } = "https://www.quakelogic.net/refraction-studio/download";

    /// <summary>Days before the end of a trial when the reminder is sent.</summary>
    public int TrialReminderDays { get; set; } = 3;
}

/// <summary>A rendered e-mail.</summary>
public sealed record EmailContent(string Template, string Subject, string Text, string Html, bool Sensitive = false);

/// <summary>Template names.</summary>
public static class EmailTemplateNames
{
    public const string PurchaseConfirmation = "purchase_confirmation";
    public const string LicenseKey = "license_key";
    public const string ActivationInstructions = "activation_instructions";
    public const string TrialExpirationReminder = "trial_expiration_reminder";
    public const string LicenseRenewal = "license_renewal";
    public const string DeviceDeactivated = "device_deactivated";
}

/// <summary>
/// Customer e-mails: professional plain text plus a simple HTML alternative. Every value inserted in the
/// HTML is encoded.
/// </summary>
public sealed class EmailTemplates(EmailOptions options)
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static string FormatDate(DateTimeOffset? date) => date is { } d ? d.UtcDateTime.ToString("d MMMM yyyy", En) : "no end date (perpetual)";

    public static string FormatAmount(long minorUnits, string currency)
    {
        var upper = currency.ToUpperInvariant();
        var zeroDecimal = upper is "JPY" or "KRW" or "VND" or "CLP" or "ISK" or "UGX" or "XAF" or "XOF";
        var value = zeroDecimal ? minorUnits : minorUnits / 100m;
        return value.ToString(zeroDecimal ? "#,0" : "#,0.00", En) + " " + upper;
    }

    public EmailContent PurchaseConfirmation(string customerName, string productName, string edition, int seats, long amount, string currency, string orderReference)
    {
        var b = new Builder(options, $"Thank you for purchasing {productName}");
        b.Greeting(customerName);
        b.Paragraph($"Thank you for your order. Your payment has been received and your {productName} licence has been created.");
        b.Facts(("Product", $"{productName} {edition}"), ("Computers (seats)", seats.ToString(En)), ("Amount paid", FormatAmount(amount, currency)), ("Order reference", orderReference));
        b.Paragraph("Your licence key is sent in a separate e-mail. Please keep both messages for your records.");
        b.Paragraph("No card details are stored by QuakeLogic; payments are processed by our payment provider.");
        return b.Build(EmailTemplateNames.PurchaseConfirmation, $"Your {productName} order confirmation");
    }

    public EmailContent LicenseKey(string customerName, string productName, string licenseKey, string edition, int seats, DateTimeOffset? expiresAt, DateTimeOffset? maintenanceUntil)
    {
        var b = new Builder(options, $"Your {productName} licence key");
        b.Greeting(customerName);
        b.Paragraph($"Here is your {productName} licence key. Keep it somewhere safe: for your security, QuakeLogic stores only a fingerprint of the key and cannot show it to you again.");
        b.Key(licenseKey);
        b.Facts(("Edition", edition), ("Computers (seats)", seats.ToString(En)), ("Licence valid until", FormatDate(expiresAt)), ("Updates included until", FormatDate(maintenanceUntil)));
        ActivationSteps(b, productName);
        return b.Build(EmailTemplateNames.LicenseKey, $"Your {productName} licence key", sensitive: true);
    }

    public EmailContent ActivationInstructions(string customerName, string productName, string keyHint, int seats)
    {
        var b = new Builder(options, $"Activating {productName}");
        b.Greeting(customerName);
        b.Paragraph($"These are the steps to activate {productName} with your licence key ending in {keyHint}. The licence can be active on {seats} computer{(seats == 1 ? "" : "s")} at a time.");
        ActivationSteps(b, productName);
        b.Paragraph("To move the licence to a new computer, open Help → Licence on the old computer and choose Deactivate this computer first. Self-service transfers are limited per 30 days; contact support if you need more.");
        return b.Build(EmailTemplateNames.ActivationInstructions, $"How to activate {productName}");
    }

    public EmailContent TrialExpirationReminder(string productName, DateTimeOffset trialEnd)
    {
        var b = new Builder(options, $"Your {productName} trial ends soon");
        b.Greeting(null);
        b.Paragraph($"Your free {productName} trial ends on {FormatDate(trialEnd)}.");
        b.Paragraph("After the trial ends your projects stay on your computer and can still be opened and viewed. To keep editing, processing and exporting, purchase a licence and enter the key in Help → Licence.");
        b.Paragraph($"Questions about licensing or volume pricing? Reply to this message or write to {options.SupportAddress}.");
        return b.Build(EmailTemplateNames.TrialExpirationReminder, $"Your {productName} trial ends on {FormatDate(trialEnd)}");
    }

    public EmailContent LicenseRenewal(string customerName, string productName, string keyHint, DateTimeOffset? expiresAt, DateTimeOffset? maintenanceUntil)
    {
        var b = new Builder(options, $"Your {productName} licence has been renewed");
        b.Greeting(customerName);
        b.Paragraph($"Your {productName} licence (key ending in {keyHint}) has been renewed.");
        b.Facts(("Licence valid until", FormatDate(expiresAt)), ("Updates included until", FormatDate(maintenanceUntil)));
        b.Paragraph("Nothing else is needed: activated computers pick up the new dates automatically the next time they connect to the internet.");
        return b.Build(EmailTemplateNames.LicenseRenewal, $"Your {productName} licence has been renewed");
    }

    public EmailContent DeviceDeactivated(string customerName, string productName, string keyHint, string? machineName, DateTimeOffset when)
    {
        var b = new Builder(options, $"{productName} deactivated on a computer");
        b.Greeting(customerName);
        var computer = string.IsNullOrWhiteSpace(machineName) ? "a computer" : $"the computer “{machineName}”";
        b.Paragraph($"{productName} (licence key ending in {keyHint}) was deactivated on {computer} on {FormatDate(when)} at {when.UtcDateTime.ToString("HH:mm", En)} UTC. The seat is free and the licence can now be activated on another computer.");
        b.Paragraph($"If you did not do this, contact {options.SupportAddress} so we can secure your licence.");
        return b.Build(EmailTemplateNames.DeviceDeactivated, $"{productName} was deactivated on a computer");
    }

    private void ActivationSteps(Builder b, string productName)
    {
        b.Paragraph("To activate:");
        b.Steps(
            $"Download and install {productName} from {options.DownloadUrl}.",
            "Start the application and open Help → Licence.",
            "Choose Activate licence, paste the key and select Activate. An internet connection is needed for the activation.",
            "After activation the application works offline; it checks the licence online from time to time.");
    }

    /// <summary>Accumulates text and HTML in parallel.</summary>
    private sealed class Builder
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _html = new();
        private readonly EmailOptions _options;

        public Builder(EmailOptions options, string heading)
        {
            _options = options;
            _html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"></head>")
                 .Append("<body style=\"margin:0;padding:0;background:#f3f4f6;font-family:Segoe UI,Arial,sans-serif;color:#1f2937;\">")
                 .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\" style=\"padding:24px 12px;\">")
                 .Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;background:#ffffff;border-radius:6px;\">")
                 .Append("<tr><td style=\"background:#1f2937;color:#ffffff;padding:18px 28px;font-size:18px;font-weight:600;border-radius:6px 6px 0 0;\">")
                 .Append(Encode(heading)).Append("</td></tr><tr><td style=\"padding:24px 28px;font-size:15px;line-height:1.55;\">");
        }

        public void Greeting(string? name)
        {
            var g = string.IsNullOrWhiteSpace(name) ? "Hello," : $"Dear {name},";
            _text.AppendLine(g).AppendLine();
            _html.Append("<p>").Append(Encode(g)).Append("</p>");
        }

        public void Paragraph(string text)
        {
            _text.AppendLine(text).AppendLine();
            _html.Append("<p>").Append(Encode(text)).Append("</p>");
        }

        public void Key(string key)
        {
            _text.AppendLine("    " + key).AppendLine();
            _html.Append("<p style=\"font-family:Consolas,Menlo,monospace;font-size:20px;letter-spacing:1px;background:#f3f4f6;border:1px solid #d1d5db;border-radius:4px;padding:12px;text-align:center;\">")
                 .Append(Encode(key)).Append("</p>");
        }

        public void Facts(params (string Label, string Value)[] facts)
        {
            _html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:8px 0 16px;border-collapse:collapse;\">");
            foreach (var (label, value) in facts)
            {
                _text.AppendLine($"  {label}: {value}");
                _html.Append("<tr><td style=\"padding:4px 16px 4px 0;color:#6b7280;\">").Append(Encode(label))
                     .Append("</td><td style=\"padding:4px 0;font-weight:600;\">").Append(Encode(value)).Append("</td></tr>");
            }
            _text.AppendLine();
            _html.Append("</table>");
        }

        public void Steps(params string[] steps)
        {
            _html.Append("<ol>");
            for (var i = 0; i < steps.Length; i++)
            {
                _text.AppendLine($"  {i + 1}. {steps[i]}");
                _html.Append("<li>").Append(Encode(steps[i])).Append("</li>");
            }
            _text.AppendLine();
            _html.Append("</ol>");
        }

        public EmailContent Build(string template, string subject, bool sensitive = false)
        {
            _text.AppendLine("Kind regards,").AppendLine($"The Refraction Studio team, {_options.CompanyName}").AppendLine(_options.SupportAddress);
            _html.Append("<p>Kind regards,<br>The Refraction Studio team, ").Append(Encode(_options.CompanyName)).Append("<br>")
                 .Append("<a href=\"mailto:").Append(Encode(_options.SupportAddress)).Append("\" style=\"color:#b45309;\">").Append(Encode(_options.SupportAddress)).Append("</a></p>")
                 .Append("</td></tr></table></td></tr></table></body></html>");
            return new EmailContent(template, subject, _text.ToString(), _html.ToString(), sensitive);
        }

        private static string Encode(string s) => WebUtility.HtmlEncode(s);
    }
}
