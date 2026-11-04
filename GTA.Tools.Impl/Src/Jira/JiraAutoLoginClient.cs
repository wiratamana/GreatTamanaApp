
using System.Text.RegularExpressions;
using GTA.Core;
using GTA.Tools.Impl.Common;
using GTA.Tools.Impl.Outlook;

namespace GTA.Tools.Impl.Jira;

public sealed class JiraAutoLoginClient : IDisposable
{
    public string OtpSenderContains { get; set; } = "atlassian";

    /// <summary>Best-effort GUESS at the real OTP email's subject substring -- UNCONFIRMED, see class header.</summary>
    public string OtpSubjectContains { get; set; } = "verification code";

    /// <summary>How often to re-check the mailbox while waiting for the OTP email.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(7);

    /// <summary>Total time budget for the poll loop before giving up.</summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(110);

    public TimeSpan ClockSkewSlack { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly Regex OtpCodeRegex = new(@"\b[A-Z0-9]{6}\b", RegexOptions.Compiled);

    private readonly string _jiraEmail;
    private readonly string _jiraPassword;
    private readonly string _jiraSiteUrl;
    private readonly string _protectedPath;
    private readonly string _sessionFilePath;
    private readonly string _challengeFilePath;
    private readonly Action<string>? _log;

    private readonly JiraOtpLoginClient _jira;
    private readonly OutlookMailboxClient _mailbox;

    public JiraAutoLoginClient(
        string jiraEmail,
        string jiraPassword,
        string jiraSiteUrl,
        string protectedPath,
        string outlookEmail,
        string outlookPassword,
        string? jiraSessionFilePath = null,
        string? outlookSessionFilePath = null,
        Action<string>? logger = null,
        IDiagnosticDumper? dumper = null,
        Func<string, Task<string>>? recaptchaTokenProvider = null)
    {
        _jiraEmail = jiraEmail;
        _jiraPassword = jiraPassword;
        _jiraSiteUrl = jiraSiteUrl;
        _protectedPath = protectedPath;
        _sessionFilePath = jiraSessionFilePath ?? Path.Combine(CredentialManager.CredentialsFolder, "jira_session.json");
        _challengeFilePath = Path.Combine(
            Path.GetDirectoryName(_sessionFilePath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory,
            "jira_otp_challenge.json");
        _log = logger;

        _jira = new JiraOtpLoginClient(logger: _log, dumper: dumper, recaptchaTokenProvider: recaptchaTokenProvider);
        _mailbox = new OutlookMailboxClient(outlookEmail, outlookPassword, sessionFilePath: outlookSessionFilePath, logger: _log, dumper: dumper);
    }

    private void Log(string msg) => _log?.Invoke(msg);

    public bool LastLoginWasFromCache { get; private set; }

    public async Task<JiraSession> LoginAsync(bool forceFreshLogin = false)
    {
        LastLoginWasFromCache = false;

        if (!forceFreshLogin && TryLoadSessionFromDisk(out var cached))
        {
            Log("Found a cached Jira session on disk -- validating it server-side (no login attempt)...");
            var check = await _jira.IsSessionValidAsync(cached!);
            if (check.IsValid)
            {
                Log($"Cached session is still accepted by Jira (accountId=\"{check.AccountId}\", displayName=\"{check.DisplayName}\"). Skipping full login+OTP entirely.");
                LastLoginWasFromCache = true;
                return cached!;
            }
            Log("Cached session is no longer valid/accepted -- proceeding to a full login+OTP cycle.");
        }
        else
        {
            Log("No usable cached Jira session found -- proceeding to a full login+OTP cycle.");
        }

        var session = await FullLoginWithOtpAsync();
        SaveSessionToDisk(session);
        return session;
    }

    private async Task<JiraSession> FullLoginWithOtpAsync()
    {
        Log("Ensuring the Outlook mailbox is logged in (needed to read the OTP email)...");
        await _mailbox.LoginAsync();

        Log("=== Trial 1: preparing the login (no credentials sent yet) ===");
        var prep = await _jira.LoginAndPrepareAsync(_jiraSiteUrl, _protectedPath, _jiraEmail);

        // Recorded BEFORE submitting the password so we can reject any stale/prior OTP email during the poll.
        var triggerTimeUtc = DateTimeOffset.UtcNow;
        Log($"Trigger time recorded (UTC): {triggerTimeUtc:O}");

        Log("=== Trial 2/3: submitting credentials (this WILL trigger a real email OTP if MFA is required) ===");
        var challenge = await _jira.SubmitPasswordAsync(prep, _jiraPassword);

        if (challenge.SkippedMfa)
        {
            Log("Jira/Atlassian ID skipped email-OTP MFA this run -- unexpected but handled; treating the current cookie jar as already logged in. " +
                "NOTE: this path has not been observed to actually carry tenant.session.token yet -- verify with IsSessionValidAsync before trusting it.");
            return new JiraSession { SiteUrl = _jiraSiteUrl, Cookies = challenge.Cookies };
        }

        SaveChallengeToDiskBestEffort(challenge);
        Log("Email OTP triggered (mfa_required). Now polling the mailbox for it...");

        var (code, foundMessage) = await PollForOtpCodeAsync(triggerTimeUtc);
        Log($"Extracted OTP code \"{code}\" from message received {foundMessage.ReceivedDateTime:u} (from: \"{foundMessage.From}\", subject: \"{foundMessage.Subject}\").");

        Log("Submitting the OTP code and driving the full post-MFA session hand-off...");
        var session = await _jira.SubmitOtpAsync(challenge, code);
        Log("Jira login complete -- real, reusable tenant.session.token captured.");
        return session;
    }

    private async Task<(string Code, EmailMessage Message)> PollForOtpCodeAsync(DateTimeOffset triggerTimeUtc)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        int attempt = 0;

        while (true)
        {
            attempt++;
            Log($"  [poll #{attempt}] checking mailbox for a new Jira/Atlassian OTP email...");

            List<EmailMessage> messages;
            try { messages = await _mailbox.FetchLatestAsync(top: 15); }
            catch (Exception ex)
            {
                Log($"  [poll #{attempt}] inbox fetch failed ({ex.Message}) -- this is just a read, safe to retry.");
                messages = new List<EmailMessage>();
            }

            var freshMessages = messages
                .Where(m => m.ReceivedDateTime.HasValue && m.ReceivedDateTime.Value >= triggerTimeUtc - ClockSkewSlack)
                .OrderByDescending(m => m.ReceivedDateTime)
                .ToList();

            var strictCandidate = freshMessages.FirstOrDefault(m =>
                (m.From?.Address?.Contains(OtpSenderContains, StringComparison.OrdinalIgnoreCase) ?? false) &&
                (m.Subject?.Contains(OtpSubjectContains, StringComparison.OrdinalIgnoreCase) ?? false));

            var candidate = strictCandidate;
            if (candidate is null)
            {
                candidate = freshMessages.FirstOrDefault(m =>
                    ((m.From?.Address?.Contains("atlassian", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (m.Subject?.Contains("atlassian", StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (m.Body?.Contains("atlassian", StringComparison.OrdinalIgnoreCase) ?? false)) &&
                    (TryExtractCode(m.Body) ?? TryExtractCode(m.BodyPreview)) is not null);
                if (candidate is not null)
                    Log($"  [poll #{attempt}] strict sender/subject match failed -- using LENIENT fallback match instead (see class header: sender/subject are unconfirmed guesses).");
            }

            if (candidate is not null)
            {
                string? code = TryExtractCode(candidate.Body) ?? TryExtractCode(candidate.BodyPreview);
                if (code is not null)
                {
                    Log($"  [poll #{attempt}] found a fresh, matching OTP email (received {candidate.ReceivedDateTime:u}) -- code extracted.");
                    return (code, candidate);
                }
                Log($"  [poll #{attempt}] found a fresh, matching message but could not extract a 6-character code from its body -- will keep polling.");
            }
            else
            {
                Log($"  [poll #{attempt}] no fresh OTP email yet ({freshMessages.Count} fresh message(s) checked out of {messages.Count} fetched).");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {PollTimeout.TotalSeconds:0}s waiting for a new Jira/Atlassian OTP email (received after {triggerTimeUtc:O}). " +
                    "The MfaChallenge itself may still be valid (Atlassian JWTs in the capture had a ~30 min exp) -- check the mailbox manually and " +
                    "consider calling JiraOtpLoginClient.SubmitOtpAsync directly on a fresh challenge loaded from the persisted jira_otp_challenge.json if so. " +
                    "Also consider: OtpSenderContains/OtpSubjectContains on this class are UNCONFIRMED best-effort guesses (see class header) -- if a real " +
                    "email did arrive, inspect it and adjust those properties.");
            }

            await Task.Delay(PollInterval);
        }
    }

    private static string? TryExtractCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = OtpCodeRegex.Match(text.ToUpperInvariant());
        return m.Success ? m.Value : null;
    }

    public async Task<JiraSession> ResumeFromPersistedChallengeAsync(DateTimeOffset triggerTimeUtc, TimeSpan? pollTimeout = null)
    {
        if (!File.Exists(_challengeFilePath))
            throw new FileNotFoundException($"No persisted OTP challenge found at \"{_challengeFilePath}\" -- nothing to resume.", _challengeFilePath);

        var challenge = JiraOtpChallenge.FromJson(File.ReadAllText(_challengeFilePath));
        Log("Resuming from a persisted Jira OTP challenge...");

        await _mailbox.LoginAsync();

        var originalTimeout = PollTimeout;
        if (pollTimeout.HasValue) PollTimeout = pollTimeout.Value;
        try
        {
            var (code, foundMessage) = await PollForOtpCodeAsync(triggerTimeUtc);
            Log($"Extracted OTP code \"{code}\" from message received {foundMessage.ReceivedDateTime:u}.");
            var session = await _jira.SubmitOtpAsync(challenge, code);
            Log("Jira login complete -- real, reusable tenant.session.token captured.");
            SaveSessionToDisk(session);
            return session;
        }
        finally { PollTimeout = originalTimeout; }
    }

    private bool TryLoadSessionFromDisk(out JiraSession? session)
    {
        session = null;
        try
        {
            if (!File.Exists(_sessionFilePath)) return false;
            session = JiraSession.FromJson(File.ReadAllText(_sessionFilePath));
            return session is not null;
        }
        catch (Exception ex)
        {
            Log($"Could not read cached Jira session file ({ex.Message}); ignoring cache.");
            return false;
        }
    }

    private static void EnsureParentDirExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private void SaveSessionToDisk(JiraSession session)
    {
        try
        {
            EnsureParentDirExists(_sessionFilePath);
            File.WriteAllText(_sessionFilePath, session.ToJson());
            Log($"Jira session cached to: {_sessionFilePath}");
        }
        catch (Exception ex) { Log($"Could not persist Jira session to disk ({ex.Message}) -- continuing in-memory only."); }
    }

    private void SaveChallengeToDiskBestEffort(JiraOtpChallenge challenge)
    {
        try { EnsureParentDirExists(_challengeFilePath); File.WriteAllText(_challengeFilePath, challenge.ToJson()); }
        catch { /* best effort only */ }
    }

    public void ClearCachedSession()
    {
        try { if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        _jira.Dispose();
        _mailbox.Dispose();
    }
}
