using System.Text;
using MimeKit;

namespace MX.MailServer.Outbound;

/// <summary>Builds RFC 3464 delivery status notifications (bounces) for failed recipients.</summary>
public static class BounceBuilder
{
    public static MimeMessage Build(string reportingHost, string originalSender, string? originalSubject, string? originalMessageId,
        IReadOnlyList<(string Address, string Response)> failures, DateTimeOffset arrivalDate)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mail Delivery System", $"mailer-daemon@{reportingHost}"));
        message.To.Add(MailboxAddress.Parse(originalSender));
        message.Subject = $"Undelivered Mail Returned to Sender{(originalSubject is null ? "" : $": {originalSubject}")}";
        message.Headers.Add("Auto-Submitted", "auto-replied"); // RFC 3834: tells other systems not to reply
        message.MessageId = $"{Guid.CreateVersion7():N}@{reportingHost}"; // never the machine's host name

        var human = new StringBuilder()
            .AppendLine($"This is the mail system at {reportingHost}.")
            .AppendLine()
            .AppendLine("Your message could not be delivered to one or more recipients:")
            .AppendLine();
        foreach (var (address, response) in failures) human.AppendLine($"  <{address}>: {response}");

        var status = new StringBuilder()
            .Append($"Reporting-MTA: dns; {reportingHost}\r\n")
            .Append($"Arrival-Date: {arrivalDate:r}\r\n");
        foreach (var (address, response) in failures)
        {
            var code = ExtractEnhancedStatus(response) ?? "5.0.0";
            status.Append("\r\n")
                .Append($"Final-Recipient: rfc822; {address}\r\n")
                .Append("Action: failed\r\n")
                .Append($"Status: {code}\r\n")
                .Append($"Diagnostic-Code: smtp; {Sanitize(response)}\r\n");
        }

        var report = new MultipartReport("delivery-status")
        {
            new TextPart("plain") { Text = human.ToString() },
            new MimePart("message", "delivery-status") { Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(status.ToString()))) },
        };
        if (originalMessageId is not null)
        {
            report.Add(new TextPart("rfc822-headers") { Text = $"Message-ID: <{originalMessageId}>\r\nSubject: {originalSubject}\r\n" });
        }
        message.Body = report;
        return message;
    }

    private static string? ExtractEnhancedStatus(string response)
    {
        foreach (var token in response.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('.');
            if (parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsDigit)) && token[0] is '4' or '5') return token;
        }
        return null;
    }

    private static string Sanitize(string value) => new(value.Where(c => c >= 32 && c < 127).Take(900).ToArray());
}
