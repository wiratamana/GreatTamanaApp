
using System.Text.RegularExpressions;
using GTA.Core;
using GTA.Tools.Impl.IbmCloud;
using GTA.Tools.Impl.Outlook;

namespace GTA.Tools.Impl.Common;

public sealed class IbmCloudAutoLoginClient : IDisposable
{
    /// <summary>Sender address of the IBM Cloud OTP email (ini key ibm_otp_sender_address).</summary>
    public string OtpSenderAddress { get; set; } = InitSettings.Get("ibm_otp_sender_address");

    /// <summary>Substring that must appear in the OTP email's subject. Configurable in case IBM changes it later.</summary>
    public string OtpSubjectContains { get; set; } = "One-time Password";

    /// <summary>How often to re-check the mailbox while waiting for the OTP email.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(7);

    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(110);

    /// <summary>Small backward clock-skew allowance applied when comparing a message's ReceivedDateTime against the login-trigger timestamp.</summary>
    public TimeSpan ClockSkewSlack { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly Regex OtpCodeRegex = new(@"\b\d{4}-\d{6}\b", RegexOptions.Compiled);

    private readonly string _ibmEmail;
    private readonly string _ibmPassword;
    private readonly string _sessionFilePath;
    private readonly string _challengeFilePath;
    private readonly string _coralogixTokenFilePath;
    private readonly Action<string>? _log;

    private readonly IbmCloudOtpLoginClient _ibm;
    private readonly OutlookMailboxClient _mailbox;
    private readonly CoralogixLoginClient _coralogix;

    public IbmCloudAutoLoginClient(
        string ibmEmail,
        string ibmPassword,
        string outlookEmail,
        string outlookPassword,
        string? ibmSessionFilePath = null,
        string? outlookSessionFilePath = null,
        string? coralogixTokenFilePath = null,
        Action<string>? logger = null,
        IDiagnosticDumper? dumper = null)
    {
        _ibmEmail = ibmEmail;
        _ibmPassword = ibmPassword;
        _sessionFilePath = ibmSessionFilePath ?? Path.Combine(CredentialManager.CredentialsFolder, "ibm_cloud_session.json");
        _challengeFilePath = Path.Combine(
            Path.GetDirectoryName(_sessionFilePath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory,
            "ibm_cloud_otp_challenge.json");
        _coralogixTokenFilePath = coralogixTokenFilePath ?? Path.Combine(CredentialManager.CredentialsFolder, "coralogix_token.json");
        _log = logger;

        _ibm = new IbmCloudOtpLoginClient(logger: _log, dumper: dumper);
        _mailbox = new OutlookMailboxClient(outlookEmail, outlookPassword, sessionFilePath: outlookSessionFilePath, logger: _log, dumper: dumper);
        _coralogix = new CoralogixLoginClient(logger: _log);
    }

    public string CoralogixAccountId { get => _coralogix.AccountId; set => _coralogix.AccountId = value; }
    public string CoralogixServiceInstanceId { get => _coralogix.ServiceInstanceId; set => _coralogix.ServiceInstanceId = value; }
    public int CoralogixTeamId { get => _coralogix.TeamId; set => _coralogix.TeamId = value; }
    public string CoralogixRegion { get => _coralogix.Region; set => _coralogix.Region = value; }

    private void Log(string msg) => _log?.Invoke(msg);

    /// <summary>True if the last LoginAsync() call was satisfied entirely from a cached, still-valid session (no password/OTP used).</summary>
    public bool LastLoginWasFromCache { get; private set; }

    public bool LastCoralogixTokenWasFromCache { get; private set; }

    public async Task<LoggedInSession> LoginAsync(bool forceFreshLogin = false)
    {
        LastLoginWasFromCache = false;

        if (!forceFreshLogin && TryLoadSessionFromDisk(out var cached))
        {
            Log("Found a cached IBM Cloud session on disk -- validating it server-side (no login attempt)...");
            var check = await RetryTransientAsync(() => _ibm.IsSessionValidAsync(cached!), nameof(IbmCloudOtpLoginClient.IsSessionValidAsync));
            if (check.IsValid)
            {
                Log($"Cached session is still accepted by IBM (user=\"{check.IbmIdUser}\"). Skipping full login+OTP entirely.");
                LastLoginWasFromCache = true;
                return cached!;
            }
            Log("Cached session is no longer valid/accepted -- proceeding to a full login+OTP cycle.");
        }
        else
        {
            Log("No usable cached IBM Cloud session found -- proceeding to a full login+OTP cycle.");
        }

        var session = await FullLoginWithOtpAsync();
        SaveSessionToDisk(session);
        return session;
    }

    public async Task<CoralogixToken> GetCoralogixTokenAsync(bool forceRefresh = false)
    {
        LastCoralogixTokenWasFromCache = false;

        if (!forceRefresh && TryLoadCoralogixTokenFromDisk(out var cached) && !cached!.IsLocallyExpired)
        {
            Log($"Found a cached Coralogix token on disk (minted {cached.AcquiredAtUtc:u}, expires {cached.ExpiresAtUtc:u}) -- reusing it.");
            LastCoralogixTokenWasFromCache = true;
            return cached;
        }

        var session = await LoginAsync();
        Log("Trading the IBM Cloud session for a Coralogix log-search API token...");

        var triggerTimeUtc = DateTimeOffset.UtcNow;
        var initial = await RetryTransientAsync(
            () => _coralogix.GetTokenOrChallengeAsync(session), nameof(CoralogixLoginClient.GetTokenOrChallengeAsync));

        CoralogixToken token;
        if (!initial.RequiresMfa)
        {
            token = initial.Token!;
        }
        else
        {
            Log("Coralogix hand-off requires a fresh step-up MFA (email OTP) challenge for this relying party -- " +
                "ensuring the mailbox is logged in and polling it for the code...");
            await _mailbox.LoginAsync();
            var (code, foundMessage) = await PollForOtpCodeAsync(triggerTimeUtc);
            Log($"Extracted step-up MFA OTP code {MaskOtp(code)} from message received {foundMessage.ReceivedDateTime:u} (subject: \"{foundMessage.Subject}\").");
            token = await _coralogix.CompleteMfaAsync(initial.Challenge!, code);
        }

        SaveCoralogixTokenToDisk(token);
        return token;
    }

    private static readonly string[] TransientNetworkKeywords =
    {
        "強制的に切断",
        "接続はリモート",
        "forcibly closed",
        "An existing connection",
        "connection reset",
        "connection was reset",
        "ReceiveFailure",
        "SendFailure",
        "ConnectFailure",
        "Connection refused",
        "remote party closed",
        "The remote end closed",
        "SocketException",
        "Unable to read data from the transport",
    };

    private static bool IsTransientNetworkException(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
            case System.Net.Http.HttpRequestException:
            case System.Net.Sockets.SocketException:
                return true;
        }

        string? msg = ex.Message;
        if (!string.IsNullOrEmpty(msg))
            foreach (string kw in TransientNetworkKeywords)
                if (msg.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;

        return ex.InnerException != null && IsTransientNetworkException(ex.InnerException);
    }

    private async Task<T> RetryTransientAsync<T>(Func<Task<T>> action, string opName, int maxAttempts = 3, int baseDelayMs = 1500)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientNetworkException(ex))
            {
                int delayMs = baseDelayMs * attempt;
                Log($"  [{opName}] transient network error (attempt {attempt}/{maxAttempts}): {ex.Message} -- retrying in {delayMs}ms...");
                await Task.Delay(delayMs);
            }
        }

        throw new InvalidOperationException($"RetryTransientAsync({opName}): exhausted all attempts without returning or throwing -- unreachable.");
    }

    private async Task<LoggedInSession> FullLoginWithOtpAsync()
    {
        Log("Ensuring the Outlook mailbox is logged in (needed to read the OTP email)...");
        await _mailbox.LoginAsync();

        // Recorded BEFORE triggering the OTP so we can reject any stale/prior OTP email during the poll.
        var triggerTimeUtc = DateTimeOffset.UtcNow;
        Log($"Trigger time recorded (UTC): {triggerTimeUtc:O}");

        Log("Submitting IBM Cloud credentials and triggering a fresh email OTP...");
        var challenge = await _ibm.LoginAndRequestEmailOtpAsync(_ibmEmail, _ibmPassword);

        if (challenge.SkippedMfa)
        {
            Log("IBM Cloud skipped email-OTP MFA this run (recent-MFA grace window) -- " +
                "login is ALREADY complete from the password step alone. No OTP poll needed.");
            return new LoggedInSession { Cookies = challenge.Cookies };
        }

        SaveChallengeToDiskBestEffort(challenge);
        Log($"Email OTP triggered (method=\"{challenge.MailMethodName}\", correlation={challenge.Correlation}). Now polling the mailbox for it...");

        var (code, foundMessage) = await PollForOtpCodeAsync(triggerTimeUtc);
        Log($"Extracted OTP code {MaskOtp(code)} from message received {foundMessage.ReceivedDateTime:u} (subject: \"{foundMessage.Subject}\").");

        Log("Submitting the OTP code to IBM Cloud to complete the login...");
        var session = await _ibm.SubmitOtpAsync(challenge, code);
        Log("IBM Cloud login complete -- real, reusable session cookies captured.");
        return session;
    }

    public async Task<LoggedInSession> ResumeFromPersistedChallengeAsync(DateTimeOffset triggerTimeUtc, TimeSpan? pollTimeout = null)
    {
        if (!File.Exists(_challengeFilePath))
            throw new FileNotFoundException($"No persisted OTP challenge found at \"{_challengeFilePath}\" -- nothing to resume.", _challengeFilePath);

        var challenge = OtpChallenge.FromJson(File.ReadAllText(_challengeFilePath));
        Log($"Resuming from persisted challenge (correlation={challenge.Correlation}, method=\"{challenge.MailMethodName}\")...");

        await _mailbox.LoginAsync();

        var originalTimeout = PollTimeout;
        if (pollTimeout.HasValue) PollTimeout = pollTimeout.Value;
        try
        {
            var (code, foundMessage) = await PollForOtpCodeAsync(triggerTimeUtc);
            Log($"Extracted OTP code {MaskOtp(code)} from message received {foundMessage.ReceivedDateTime:u} (subject: \"{foundMessage.Subject}\").");

            Log("Submitting the OTP code to IBM Cloud to complete the login...");
            var session = await _ibm.SubmitOtpAsync(challenge, code);
            Log("IBM Cloud login complete -- real, reusable session cookies captured.");
            SaveSessionToDisk(session);
            return session;
        }
        finally
        {
            PollTimeout = originalTimeout;
        }
    }

    private async Task<(string Code, EmailMessage Message)> PollForOtpCodeAsync(DateTimeOffset triggerTimeUtc)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        int attempt = 0;

        while (true)
        {
            attempt++;
            Log($"  [poll #{attempt}] checking mailbox for a new OTP email from \"{OtpSenderAddress}\"...");

            List<EmailMessage> messages;
            try
            {
                messages = await _mailbox.FetchLatestAsync(top: 15);
            }
            catch (Exception ex)
            {
                Log($"  [poll #{attempt}] inbox fetch failed ({ex.Message}) -- this is just a read, safe to retry.");
                messages = new List<EmailMessage>();
            }

            var candidate = messages
                .Where(m => m.ReceivedDateTime.HasValue
                         && m.ReceivedDateTime.Value >= triggerTimeUtc - ClockSkewSlack
                         && string.Equals(m.From?.Address, OtpSenderAddress, StringComparison.OrdinalIgnoreCase)
                         && (m.Subject?.Contains(OtpSubjectContains, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(m => m.ReceivedDateTime)
                .FirstOrDefault();

            if (candidate is not null)
            {
                string? code = TryExtractCode(candidate.Body) ?? TryExtractCode(candidate.BodyPreview);
                if (code is not null)
                {
                    Log($"  [poll #{attempt}] found a fresh, matching OTP email (received {candidate.ReceivedDateTime:u}) -- code extracted.");
                    return (code, candidate);
                }
                Log($"  [poll #{attempt}] found a fresh, matching message but could not extract a code from its body -- will keep polling.");
            }
            else
            {
                Log($"  [poll #{attempt}] no fresh OTP email yet ({messages.Count} recent message(s) checked).");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {PollTimeout.TotalSeconds:0}s waiting for a new IBM Cloud OTP email " +
                    $"(from \"{OtpSenderAddress}\", subject containing \"{OtpSubjectContains}\", received after {triggerTimeUtc:O}). " +
                    "The OTP challenge itself may still be valid -- check the mailbox manually and consider calling SubmitOtpAsync directly " +
                    "on a fresh IbmCloudOtpLoginClient using the persisted ibm_cloud_otp_challenge.json if so.");
            }

            await Task.Delay(PollInterval);
        }
    }

    private static string? TryExtractCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = OtpCodeRegex.Match(text);
        return m.Success ? m.Value : null;
    }

    private static string MaskOtp(string code) => $"<masked, {code.Length} chars>";

    private bool TryLoadSessionFromDisk(out LoggedInSession? session)
    {
        session = null;
        try
        {
            if (!File.Exists(_sessionFilePath)) return false;
            session = LoggedInSession.FromJson(File.ReadAllText(_sessionFilePath));
            return session is not null;
        }
        catch (Exception ex)
        {
            Log($"Could not read cached IBM Cloud session file ({ex.Message}); ignoring cache.");
            return false;
        }
    }

    private static void EnsureParentDirExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private void SaveSessionToDisk(LoggedInSession session)
    {
        try
        {
            EnsureParentDirExists(_sessionFilePath);
            File.WriteAllText(_sessionFilePath, session.ToJson());
            Log($"IBM Cloud session cached to: {_sessionFilePath}");
        }
        catch (Exception ex)
        {
            Log($"Could not persist IBM Cloud session to disk ({ex.Message}) -- continuing in-memory only.");
        }
    }

    private void SaveChallengeToDiskBestEffort(OtpChallenge challenge)
    {
        try { EnsureParentDirExists(_challengeFilePath); File.WriteAllText(_challengeFilePath, challenge.ToJson()); }
        catch { }
    }

    private bool TryLoadCoralogixTokenFromDisk(out CoralogixToken? token)
    {
        token = null;
        try
        {
            if (!File.Exists(_coralogixTokenFilePath)) return false;
            token = CoralogixToken.FromJson(File.ReadAllText(_coralogixTokenFilePath));
            return token is not null;
        }
        catch (Exception ex)
        {
            Log($"Could not read cached Coralogix token file ({ex.Message}); ignoring cache.");
            return false;
        }
    }

    private void SaveCoralogixTokenToDisk(CoralogixToken token)
    {
        try
        {
            EnsureParentDirExists(_coralogixTokenFilePath);
            File.WriteAllText(_coralogixTokenFilePath, token.ToJson());
            Log($"Coralogix token cached to: {_coralogixTokenFilePath}");
        }
        catch (Exception ex)
        {
            Log($"Could not persist Coralogix token to disk ({ex.Message}) -- continuing in-memory only.");
        }
    }

    public void ClearCachedSession()
    {
        try { if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath); } catch { }
    }

    /// <summary>Deletes any cached Coralogix token file, forcing the next GetCoralogixTokenAsync() to mint a brand new one.</summary>
    public void ClearCachedCoralogixToken()
    {
        try { if (File.Exists(_coralogixTokenFilePath)) File.Delete(_coralogixTokenFilePath); } catch { }
    }

    public void Dispose()
    {
        _ibm.Dispose();
        _mailbox.Dispose();
        _coralogix.Dispose();
    }
}
