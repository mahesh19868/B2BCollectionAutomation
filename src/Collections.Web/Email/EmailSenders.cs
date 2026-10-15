using System.Globalization;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Collections.Web.Email;

public sealed record EmailMessage(string To, string Subject, string TextBody, string FromDisplayName, string? ReplyTo);

public sealed record EmailSendResult(bool Succeeded, string? ProviderMessageId, string? Error)
{
    public static EmailSendResult Success(string? messageId) => new(true, messageId, null);
    public static EmailSendResult Failure(string sanitisedError) => new(false, null, sanitisedError);
}

/// <summary>Provider abstraction. Implementations must return sanitised errors that never contain credentials.</summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";
    public const string CaptureProvider = "Capture";
    public const string SmtpProvider = "Smtp";

    /// <summary>"Capture" (writes messages to a local folder; never sends) or "Smtp".</summary>
    public string? Provider { get; set; }

    /// <summary>Envelope sender address of the application; the distributor appears as display name and reply-to.</summary>
    public string FromAddress { get; set; } = "no-reply@localhost";
    public string CaptureDirectory { get; set; } = "App_Data/outbox";
    public SmtpSettings Smtp { get; set; } = new();

    public sealed class SmtpSettings
    {
        public string? Host { get; set; }
        public int Port { get; set; } = 587;
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool UseStartTls { get; set; } = true;
    }
}

/// <summary>Development/test sender: writes each message to a text file and never contacts a mail server.</summary>
public sealed class CaptureEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<CaptureEmailSender> _logger;

    public CaptureEmailSender(IOptions<EmailOptions> options, IWebHostEnvironment environment, ILogger<CaptureEmailSender> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var directory = Path.IsPathRooted(_options.CaptureDirectory)
            ? _options.CaptureDirectory
            : Path.Combine(_environment.ContentRootPath, _options.CaptureDirectory);
        Directory.CreateDirectory(directory);

        var id = $"capture-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
        var content = new StringBuilder()
            .AppendLine($"From: \"{message.FromDisplayName}\" <{_options.FromAddress}>")
            .AppendLine($"Reply-To: {message.ReplyTo}")
            .AppendLine($"To: {message.To}")
            .AppendLine($"Subject: {message.Subject}")
            .AppendLine()
            .AppendLine(message.TextBody)
            .ToString();

        await File.WriteAllTextAsync(Path.Combine(directory, id + ".txt"), content, Encoding.UTF8, cancellationToken);
        _logger.LogInformation("Captured email {MessageId} (not sent externally)", id);
        return EmailSendResult.Success(id);
    }
}

/// <summary>Production sender using any SMTP relay (credentials from environment variables or a secret store).</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromDisplayName, _options.FromAddress));
        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        }

        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        try
        {
            using var client = new SmtpClient();
            var security = _options.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            await client.ConnectAsync(_options.Smtp.Host ?? string.Empty, _options.Smtp.Port, security, cancellationToken);
            if (!string.IsNullOrEmpty(_options.Smtp.Username))
            {
                await client.AuthenticateAsync(_options.Smtp.Username, _options.Smtp.Password ?? string.Empty, cancellationToken);
            }

            var response = await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return EmailSendResult.Success(Truncate(mime.MessageId ?? response, 200));
        }
        catch (AuthenticationException ex)
        {
            _logger.LogError(ex, "SMTP authentication failed");
            return EmailSendResult.Failure("The mail server rejected the configured credentials.");
        }
        catch (SmtpCommandException ex)
        {
            _logger.LogWarning(ex, "SMTP command failed with status {StatusCode}", ex.StatusCode);
            return EmailSendResult.Failure($"The mail server rejected the message (SMTP {(int)ex.StatusCode}).");
        }
        catch (Exception ex) when (ex is SmtpProtocolException or ServiceNotConnectedException or System.Net.Sockets.SocketException or IOException)
        {
            _logger.LogWarning(ex, "SMTP connection failed");
            return EmailSendResult.Failure("Could not connect to the mail server. Try again later.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected email delivery error");
            return EmailSendResult.Failure("Unexpected email delivery error. See the application log for details.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
