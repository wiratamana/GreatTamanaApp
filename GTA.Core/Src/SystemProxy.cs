using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Core
{
    /// <summary>
    /// Automatic corporate-proxy support. NOTHING is configured in init.ini:
    /// the proxy address comes from the Windows system settings (including PAC / auto-detect),
    /// and the proxy login is the CURRENT Windows login (NTLM / single sign-on).
    /// If Windows says "no proxy" for a target, everything goes direct.
    /// </summary>
    public static class SystemProxy
    {
        private static readonly Uri ProbeUri = new("https://www.microsoft.com/");

        // One shared instance: it caches the Windows settings and follows changes by itself.
        // (HttpClient.DefaultProxy = Windows system proxy incl. PAC / auto-detect; honors HTTP_PROXY env vars first.)
        private static readonly IWebProxy _shared = CreateSharedProxy();

        private static IWebProxy CreateSharedProxy()
        {
            IWebProxy proxy = HttpClient.DefaultProxy;
            proxy.Credentials = CredentialCache.DefaultCredentials; // NTLM SSO with the current Windows login
            return proxy;
        }

        /// <summary>For HttpClient handlers: Windows system proxy + current Windows login. Direct when Windows says no proxy.</summary>
        public static IWebProxy CreateWebProxy() => _shared;

        /// <summary>The proxy Windows would use for <paramref name="target"/> (a generic external HTTPS address if null). Null = direct.</summary>
        public static Uri? GetProxyUri(Uri? target = null)
        {
            try
            {
                Uri t = target ?? ProbeUri;
                if (_shared.IsBypassed(t)) return null;
                Uri? proxy = _shared.GetProxy(t);
                if (proxy == null || proxy.Equals(t)) return null;
                return proxy;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Local, unauthenticated CONNECT relay on 127.0.0.1 for CHILD PROCESSES (git, cmake, dotnet, python, node ...).
    /// Those programs cannot do NTLM single sign-on themselves. The relay logs in to the real corporate proxy
    /// with the current Windows login and hands them a plain "http://127.0.0.1:port" proxy.
    /// Started on demand, only when Windows actually uses a proxy. Only HTTPS (CONNECT) is relayed;
    /// plain http:// requests get "501 Not Implemented".
    /// </summary>
    public static class ProxyBridge
    {
        private static readonly object _lock = new();
        private static TcpListener? _listener;
        private static int _port;

        /// <summary>"http://127.0.0.1:port" of the local relay, or null when no proxy is needed (direct connection).</summary>
        public static string? LocalProxyUrl
        {
            get
            {
                if (SystemProxy.GetProxyUri() == null) return null;

                lock (_lock)
                {
                    if (_listener == null)
                    {
                        try
                        {
                            var listener = new TcpListener(IPAddress.Loopback, 0);
                            listener.Start();
                            _listener = listener;
                            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                            _ = Task.Run(() => AcceptLoopAsync(listener));
                        }
                        catch
                        {
                            return null;
                        }
                    }
                    return $"http://127.0.0.1:{_port}";
                }
            }
        }

        /// <summary>Environment variables to give to a child process so it uses the proxy. Empty when no proxy is needed.</summary>
        public static Dictionary<string, string> BuildChildProcessEnvironment()
        {
            var vars = new Dictionary<string, string>();
            string? url = LocalProxyUrl;
            if (url == null) return vars;

            vars["HTTP_PROXY"]  = url;
            vars["HTTPS_PROXY"] = url;
            vars["http_proxy"]  = url;
            vars["https_proxy"] = url;

            // Node.js 24+ ignores HTTP(S)_PROXY for its built-in http/https modules unless this is set.
            vars["NODE_USE_ENV_PROXY"]           = "1";

            string noProxy = "127.0.0.1,localhost,::1";
            string existing = Environment.GetEnvironmentVariable("NO_PROXY")
                              ?? Environment.GetEnvironmentVariable("no_proxy") ?? "";
            if (existing.Length > 0) noProxy = existing.TrimEnd(',') + "," + noProxy;
            vars["NO_PROXY"] = noProxy;
            vars["no_proxy"] = noProxy;
            return vars;
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  THE ONE GLOBAL METHOD that prepares ANY child process for the corporate proxy.
        //  Every tool that starts a process (run_shell, cmake, inline_python, dotnet ...) must call
        //  ApplyToStartInfo() instead of copying proxy variables by itself. New tool-specific
        //  adapters (like Scoop below) go HERE, so all tools get them at once.
        //  (C# port of run-with-proxy-tunnel-v2.ps1, including its "Scoop adapter".)
        // ══════════════════════════════════════════════════════════════════════════

        // "scoop", "scoop.cmd", "C:\...\shims\scoop.ps1" ... used as a command (not as part of a folder name).
        private static readonly Regex ScoopCommandRegex = new(
            @"(?<![\w.\-])scoop(?:\.cmd|\.ps1|\.exe)?(?=[\s""'&|]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private const string ScoopTempDirPrefix = "gta-proxy-scoop-cfg-";

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new();
            public void Dispose() { }
        }

        private sealed class TempDirScope : IDisposable
        {
            private readonly string _dir;
            public TempDirScope(string dir) => _dir = dir;
            public void Dispose()
            {
                try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Prepares <paramref name="psi"/> (UseShellExecute must be false) so the child process uses the corporate proxy:
        /// HTTP(S)_PROXY / NO_PROXY variables pointing to the local relay, plus tool-specific adapters.
        /// <paramref name="commandHint"/> = the command line text, used to detect tools that need an adapter (e.g. Scoop).
        /// Does nothing (direct connection) when Windows uses no proxy.
        /// RETURNS a scope: dispose it AFTER the child process has ended (it removes temporary files).
        /// </summary>
        public static IDisposable ApplyToStartInfo(ProcessStartInfo psi, string? commandHint = null)
        {
            Dictionary<string, string> vars = BuildChildProcessEnvironment();
            if (vars.Count == 0) return NoScope.Instance;

            foreach (var kv in vars)
                psi.EnvironmentVariables[kv.Key] = kv.Value;

            // Adapter: Scoop. It downloads with .NET WebRequest, which IGNORES HTTP(S)_PROXY, and only reads
            // its proxy from its own config file. So: temporary COPY of the Scoop config + "proxy" entry,
            // activated for this one process with XDG_CONFIG_HOME. The real config.json is never touched.
            if (!string.IsNullOrEmpty(commandHint) && ScoopCommandRegex.IsMatch(commandHint))
            {
                string? tempRoot = TryApplyScoopAdapter(psi, vars["HTTP_PROXY"]);
                if (tempRoot != null) return new TempDirScope(tempRoot);
            }

            return NoScope.Instance;
        }

        private static string? TryApplyScoopAdapter(ProcessStartInfo psi, string localProxyUrl)
        {
            try
            {
                int port = new Uri(localProxyUrl).Port;

                // Housekeeping: remove copies left behind by runs that were killed (app closed during a command).
                try
                {
                    foreach (string old in Directory.EnumerateDirectories(Path.GetTempPath(), ScoopTempDirPrefix + "*"))
                    {
                        if (Directory.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-12))
                            try { Directory.Delete(old, recursive: true); } catch { /* in use or no access */ }
                    }
                }
                catch { /* best effort */ }

                string tempRoot = Path.Combine(Path.GetTempPath(), ScoopTempDirPrefix + Guid.NewGuid().ToString("N")[..8]);
                string tempScoopDir = Path.Combine(tempRoot, "scoop");
                Directory.CreateDirectory(tempScoopDir);

                // Where Scoop reads its real config: $XDG_CONFIG_HOME\scoop\config.json, else ~\.config\scoop\config.json.
                string? configHome = psi.EnvironmentVariables["XDG_CONFIG_HOME"];
                if (string.IsNullOrWhiteSpace(configHome))
                    configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                string realConfig = Path.Combine(configHome, "scoop", "config.json");

                JsonObject cfg = new();
                if (File.Exists(realConfig))
                {
                    try { cfg = JsonNode.Parse(File.ReadAllText(realConfig)) as JsonObject ?? new JsonObject(); }
                    catch { /* unreadable: start from an empty config */ }
                }
                cfg["proxy"] = $"127.0.0.1:{port}";

                File.WriteAllText(
                    Path.Combine(tempScoopDir, "config.json"),
                    cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));

                psi.EnvironmentVariables["XDG_CONFIG_HOME"] = tempRoot;
                return tempRoot;
            }
            catch
            {
                return null; // adapter failed: Scoop downloads may still get 407, but nothing else breaks
            }
        }

        private static async Task AcceptLoopAsync(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // listener stopped
                }
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            using TcpClient clientLifetime = client;
            TcpClient? upstream = null;
            try
            {
                NetworkStream clientStream = client.GetStream();

                string? requestLine = await ReadLineAsync(clientStream, CancellationToken.None);
                if (string.IsNullOrEmpty(requestLine)) return;

                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await ReadLineAsync(clientStream, CancellationToken.None))) { }

                string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 501 Not Implemented\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }

                string target = parts[1];
                int colon = target.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out int targetPort))
                {
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
                string targetHost = target[..colon].Trim('[', ']');

                Uri? proxyUri = null;
                try { proxyUri = SystemProxy.GetProxyUri(new Uri($"https://{target}")); } catch { /* treat as direct */ }

                upstream = new TcpClient { NoDelay = true };
                using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    if (proxyUri == null)
                        await upstream.ConnectAsync(targetHost, targetPort, connectCts.Token);
                    else
                        await upstream.ConnectAsync(proxyUri.Host, proxyUri.Port, connectCts.Token);
                }

                NetworkStream upstreamStream = upstream.GetStream();

                if (proxyUri != null)
                {
                    using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    bool ok = await ConnectViaNtlmAsync(upstreamStream, target, proxyUri.Host, authCts.Token);
                    if (!ok)
                    {
                        await WriteAsciiAsync(clientStream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }
                }

                await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection established\r\n\r\n");

                Task clientToUpstream = clientStream.CopyToAsync(upstreamStream, 16 * 1024);
                Task upstreamToClient = upstreamStream.CopyToAsync(clientStream, 16 * 1024);
                await Task.WhenAny(clientToUpstream, upstreamToClient);
            }
            catch
            {
                // Best effort: client or upstream disconnected, timeout, etc.
            }
            finally
            {
                try { upstream?.Dispose(); } catch { }
            }
        }

        // HTTP CONNECT through an NTLM proxy, logging in with the CURRENT Windows account (single sign-on).
        private static async Task<bool> ConnectViaNtlmAsync(NetworkStream stream, string target, string proxyHost, CancellationToken ct)
        {
            using var auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package    = "NTLM",
                TargetName = "HTTP/" + proxyHost,
                Credential = CredentialCache.DefaultNetworkCredentials,
            });

            // Message 1 (NEGOTIATE)
            byte[]? type1 = auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out NegotiateAuthenticationStatusCode status1);
            if (type1 == null || (status1 != NegotiateAuthenticationStatusCode.ContinueNeeded &&
                                  status1 != NegotiateAuthenticationStatusCode.Completed))
                return false;

            await SendConnectAsync(stream, target, "NTLM " + Convert.ToBase64String(type1), ct);
            (string? replyStatus1, string? challenge) = await ReadProxyReplyAsync(stream, ct);
            if (replyStatus1 == null) return false;
            if (replyStatus1.Contains(" 200")) return true; // proxy did not need a login
            if (challenge == null) return false;

            // Message 3 (AUTHENTICATE), answering the proxy's challenge (message 2)
            byte[]? type3 = auth.GetOutgoingBlob(Convert.FromBase64String(challenge), out NegotiateAuthenticationStatusCode status3);
            if (type3 == null || (status3 != NegotiateAuthenticationStatusCode.Completed &&
                                  status3 != NegotiateAuthenticationStatusCode.ContinueNeeded))
                return false;

            await SendConnectAsync(stream, target, "NTLM " + Convert.ToBase64String(type3), ct);
            (string? replyStatus2, _) = await ReadProxyReplyAsync(stream, ct);
            return replyStatus2 != null && replyStatus2.Contains(" 200");
        }

        private static Task SendConnectAsync(NetworkStream stream, string target, string proxyAuthorization, CancellationToken ct)
        {
            string request =
                $"CONNECT {target} HTTP/1.1\r\n" +
                $"Host: {target}\r\n" +
                $"Proxy-Authorization: {proxyAuthorization}\r\n" +
                "Proxy-Connection: Keep-Alive\r\n\r\n";
            return stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).AsTask();
        }

        private static readonly Regex NtlmChallengeRegex =
            new(@"^Proxy-Authenticate:\s*NTLM\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ContentLengthRegex =
            new(@"^Content-Length:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Reads the proxy's reply: status line, headers, and (for non-200 replies) the body, so the
        // connection stays usable for the next message. Returns the status line and the NTLM challenge (if any).
        private static async Task<(string? Status, string? Challenge)> ReadProxyReplyAsync(NetworkStream stream, CancellationToken ct)
        {
            string? status = await ReadLineAsync(stream, ct);
            if (status == null) return (null, null);

            string? challenge = null;
            int contentLength = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await ReadLineAsync(stream, ct)))
            {
                Match m = NtlmChallengeRegex.Match(line);
                if (m.Success) challenge = m.Groups[1].Value;

                m = ContentLengthRegex.Match(line);
                if (m.Success) int.TryParse(m.Groups[1].Value, out contentLength);
            }

            if (contentLength > 0 && !status.Contains(" 200"))
            {
                byte[] buffer = new byte[4096];
                int drained = 0;
                while (drained < contentLength)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, contentLength - drained)), ct);
                    if (n <= 0) break;
                    drained += n;
                }
            }
            return (status, challenge);
        }

        // Byte-by-byte on purpose: we must never read past the end of the headers (tunnel data may follow).
        private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>();
            byte[] one = new byte[1];
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(one.AsMemory(0, 1), ct); }
                catch { return null; }

                if (n == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                if (one[0] == (byte)'\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                bytes.Add(one[0]);
            }
        }

        private static Task WriteAsciiAsync(NetworkStream stream, string text) =>
            stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();
    }
}
