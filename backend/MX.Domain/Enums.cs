namespace MX.Domain;

public enum OrganizationStatus { Active, Suspended, Deprovisioned }
public enum DomainVerificationStatus { Pending, Verified, Failed }
public enum MailServerType { Stub, Postfix, Exchange, Stalwart, Other, Native }
public enum OutboundSource { TestMessage, Submission, Bounce }
public enum DeliveryStatus { Queued, Sending, Deferred, Delivered, Failed, Cancelled }
public enum MailFolder { Inbox, Junk, Sent, Drafts, Trash, Archive }
/// <summary>Outcome of an SPF, DKIM or DMARC check, as recorded in Authentication-Results.</summary>
public enum AuthCheckResult { None, Pass, Fail, SoftFail, Neutral, TempError, PermError }
public enum MailServerStatus { Online, Degraded, Offline, Maintenance }
public enum MailboxType { User, Shared, Resource }
public enum MailboxStatus { Active, Disabled, Provisioning, Deleted }
public enum RoutingAction { Deliver, Redirect, Reject, Quarantine, AddHeader }
public enum AuthenticationOutcome { Success, InvalidCredentials, LockedOut, MfaRequired, Disabled }
public enum AlertSeverity { Low, Medium, High, Critical }
public enum AlertStatus { Open, Acknowledged, Resolved, Dismissed }
public enum QuarantineReason { Spam, Malware, Phishing, Policy }
public enum QuarantineStatus { Held, Released, Deleted }
public enum RetentionAction { Retain, Delete, Archive }
public enum LegalHoldStatus { Active, Released }
public enum DiscoveryStatus { Draft, Running, Completed, Failed, Cancelled }
public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }
public enum BackupScope { Database, Mailboxes, Full }
public enum NotificationLevel { Info, Success, Warning, Error }
public enum SubscriptionPlan { Starter, Business, Enterprise }
public enum SubscriptionStatus { Trial, Active, PastDue, Cancelled }
