namespace MX.MailServer;

public sealed class MailServerOptions
{
    public const string Section = "MailServer";

    /// <summary>Public host name announced in the SMTP greeting and Received headers, e.g. mail.example.com.</summary>
    public string Hostname { get; set; } = "localhost";

    /// <summary>Inbound SMTP port. 25 in production; an unprivileged port such as 2525 in development. -1 disables it.</summary>
    public int SmtpPort { get; set; } = 2525;

    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>PFX certificate for STARTTLS. STARTTLS is not offered when unset.</summary>
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }

    public long MaxMessageSizeBytes { get; set; } = 35L * 1024 * 1024;
    public int MaxRecipients { get; set; } = 100;
    public int MaxConnections { get; set; } = 200;
    public int MaxConnectionsPerIp { get; set; } = 10;
    public int MaxErrorsPerSession { get; set; } = 10;
    public int CommandTimeoutSeconds { get; set; } = 300;

    /// <summary>Accept mail for domains that have not passed DNS ownership verification (development only).</summary>
    public bool AcceptUnverifiedDomains { get; set; }

    /// <summary>Reject mail failing DMARC when the sender's policy is p=reject.</summary>
    public bool EnforceDmarcReject { get; set; } = true;

    /// <summary>Skip SPF/DKIM/DMARC network lookups (tests and offline development).</summary>
    public bool DisableAuthenticationChecks { get; set; }

    /// <summary>Authenticated submission with STARTTLS (RFC 6409). -1 disables it; 0 picks a free port (tests).</summary>
    public int SubmissionPort { get; set; } = 587;

    /// <summary>Authenticated submission over implicit TLS (RFC 8314). -1 disables it. Requires a certificate.</summary>
    public int SubmissionTlsPort { get; set; } = 465;

    /// <summary>Allow AUTH before TLS. Never enable in production: passwords would cross the network in clear text.</summary>
    public bool AllowPlaintextAuth { get; set; }

    /// <summary>Messages one mailbox may submit per rolling hour (limits damage from a stolen password).</summary>
    public int MaxSubmissionsPerHour { get; set; } = 200;

    /// <summary>Failed logins from one IP within the window before that IP is temporarily refused.</summary>
    public int MaxAuthFailuresPerIp { get; set; } = 5;

    /// <summary>Failed logins for one account within the window before it is temporarily locked.</summary>
    public int MaxAuthFailuresPerAccount { get; set; } = 10;

    public int AuthFailureWindowMinutes { get; set; } = 15;

    /// <summary>IMAP with STARTTLS (RFC 3501). -1 disables it; 0 picks a free port (tests).</summary>
    public int ImapPort { get; set; } = 143;

    /// <summary>IMAP over implicit TLS (RFC 8314). -1 disables it. Requires a certificate.</summary>
    public int ImapTlsPort { get; set; } = 993;

    /// <summary>How often an IDLE session checks for new mail.</summary>
    public int ImapIdlePollSeconds { get; set; } = 2;

    /// <summary>Inactivity before an IMAP session is closed (RFC 3501 requires at least 30 minutes).</summary>
    public int ImapAutoLogoutMinutes { get; set; } = 30;
}

public sealed class MessageStoreOptions
{
    public const string Section = "MailStore";

    /// <summary>Root directory for message files. Must be on durable, backed-up storage.</summary>
    public string Root { get; set; } = "mailstore";
}
