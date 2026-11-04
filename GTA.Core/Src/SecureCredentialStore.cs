#if CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Core;

/// <summary>Result of the last <see cref="SecureCredentialStore.Load"/>.</summary>
public enum SecureStoreStatus
{
    /// <summary>Load() was not called yet.</summary>
    NotLoaded,
    /// <summary>File read, authenticated and decrypted.</summary>
    Ok,
    /// <summary>Credentials\my_credentials.dat does not exist.</summary>
    Missing,
    /// <summary>File exists but is damaged, was changed, or was built with a different key.</summary>
    Corrupt,
}

/// <summary>
/// CO_WORKER_USE only. Holds the encrypted settings (Credentials\my_credentials.dat) in memory.
///
/// File format (written by tools\Pack-CoWorkerConfig.ps1 AND by this class - keep both in sync):
///   "GTAC" (4 bytes) | version = 1 (1 byte) | IV (16 bytes) | AES-256-CBC/PKCS7 ciphertext | HMAC-SHA256 tag (32 bytes)
///   The tag covers every byte before it (encrypt-then-MAC). Plain text inside = UTF-8 JSON object { "key": "value", ... }.
///   encKey = HMAC-SHA256(masterKey, "GTA|enc|v1"), macKey = HMAC-SHA256(masterKey, "GTA|mac|v1").
///
/// HONEST LIMIT: masterKey is compiled into the exe (build_secrets\CoWorkerKey.g.cs). That keeps the secrets
/// out of plain text, away from casual copy/paste, grep, and file-share leaks. It does NOT stop someone who
/// reverse-engineers the exe: the key must be inside the exe for the app to decrypt without user input.
/// </summary>
public static class SecureCredentialStore
{
    public const string FileName = "my_credentials.dat";

    /// <summary>Keys the user types in the Settings window. They ship EMPTY in a fresh release.</summary>
    public static readonly string[] UserCredentialKeys =
    {
        "cred_jiraapitoken_id",    "cred_jiraapitoken_password",
        "cred_primaryaccount_id",  "cred_primaryaccount_password",
        "cred_mailaccount_id",     "cred_mailaccount_password",
    };

    private static readonly byte[] Magic = { (byte)'G', (byte)'T', (byte)'A', (byte)'C' };
    private const byte FormatVersion = 1;
    private const int IvLength = 16;
    private const int TagLength = 32;

    private static readonly object _lock = new();
    private static Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private static SecureStoreStatus _status = SecureStoreStatus.NotLoaded;
    private static string _statusMessage = "Not loaded.";
    private static long _version;

    public static string FilePath => Path.Combine(CredentialManager.CredentialsFolder, FileName);

    public static SecureStoreStatus Status { get { EnsureLoaded(); return _status; } }

    /// <summary>Human-readable result of the last load (safe to show or log; never contains secrets).</summary>
    public static string StatusMessage { get { EnsureLoaded(); return _statusMessage; } }

    /// <summary>Changes every time the in-memory values change. InitSettings uses it to refresh its cache.</summary>
    public static long Version { get { EnsureLoaded(); return _version; } }

    /// <summary>True when any user credential (Settings window field) is still empty.</summary>
    public static bool UserCredentialsIncomplete()
    {
        EnsureLoaded();
        lock (_lock)
        {
            foreach (string key in UserCredentialKeys)
                if (!_values.TryGetValue(key, out string? v) || string.IsNullOrWhiteSpace(v))
                    return true;
            return false;
        }
    }

    public static string Get(string key)
    {
        EnsureLoaded();
        lock (_lock)
            return _values.TryGetValue(key, out string? v) ? v : "";
    }

    /// <summary>Copy of all values. Used by InitSettings to overlay them on init.ini.</summary>
    public static Dictionary<string, string> Snapshot()
    {
        EnsureLoaded();
        lock (_lock)
            return new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureLoaded()
    {
        if (_status == SecureStoreStatus.NotLoaded) Load();
    }

    /// <summary>Reads, authenticates and decrypts Credentials\my_credentials.dat. Never throws.</summary>
    public static void Load()
    {
        lock (_lock)
        {
            // Set first: nothing below may recurse into EnsureLoaded().
            _status = SecureStoreStatus.Corrupt;
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _version++;

            string path = FilePath;
            if (!File.Exists(path))
            {
                _status = SecureStoreStatus.Missing;
                _statusMessage = $"Credential file not found: {path}";
                return;
            }

            try
            {
                byte[] blob = File.ReadAllBytes(path);
                byte[] plain = Decrypt(blob);
                try
                {
                    var table = ParseJson(plain);
                    _values = table;
                    _status = SecureStoreStatus.Ok;
                    _statusMessage = $"Loaded {table.Count} value(s) from {path}";
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (Exception ex)
            {
                _status = SecureStoreStatus.Corrupt;
                _statusMessage = $"Credential file \"{path}\" could not be read: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Merges <paramref name="updates"/> into the stored values, re-encrypts everything and replaces
    /// Credentials\my_credentials.dat. Throws if the current file could not be loaded (we must not overwrite
    /// a file we cannot read: it holds keys the user cannot re-enter, like azure_key).
    /// </summary>
    public static void Save(IReadOnlyDictionary<string, string> updates)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_status != SecureStoreStatus.Ok)
                throw new InvalidOperationException(
                    "The credential file is not readable, so it was NOT overwritten. " + _statusMessage +
                    " Restore Credentials\\my_credentials.dat from the original release and try again.");

            var merged = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in updates)
                merged[kv.Key] = kv.Value ?? "";

            var obj = new JsonObject();
            foreach (var kv in merged)
                obj[kv.Key] = kv.Value;

            byte[] plain = Encoding.UTF8.GetBytes(obj.ToJsonString());
            byte[] blob;
            try { blob = Encrypt(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }

            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write to a temp file first, then replace: a crash can never leave a half-written .dat behind.
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);

            _values = merged;
            _version++;
            _statusMessage = $"Saved {merged.Count} value(s) to {path}";
        }
    }

    // ── Crypto ────────────────────────────────────────────────────────────────

    private static (byte[] enc, byte[] mac) DeriveKeys()
    {
        byte[] master = CoWorkerKeyMaterial.Get();
        try
        {
            return (HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("GTA|enc|v1")),
                    HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("GTA|mac|v1")));
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    private static byte[] Encrypt(byte[] plain)
    {
        var (encKey, macKey) = DeriveKeys();
        try
        {
            byte[] iv = RandomNumberGenerator.GetBytes(IvLength);
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = encKey;
            byte[] cipher = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);

            int bodyLen = Magic.Length + 1 + IvLength + cipher.Length;
            byte[] blob = new byte[bodyLen + TagLength];
            Buffer.BlockCopy(Magic, 0, blob, 0, Magic.Length);
            blob[Magic.Length] = FormatVersion;
            Buffer.BlockCopy(iv, 0, blob, Magic.Length + 1, IvLength);
            Buffer.BlockCopy(cipher, 0, blob, Magic.Length + 1 + IvLength, cipher.Length);

            byte[] tag = HMACSHA256.HashData(macKey, new ReadOnlySpan<byte>(blob, 0, bodyLen));
            Buffer.BlockCopy(tag, 0, blob, bodyLen, TagLength);
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static byte[] Decrypt(byte[] blob)
    {
        int minLength = Magic.Length + 1 + IvLength + 16 + TagLength;
        if (blob.Length < minLength)
            throw new InvalidDataException("file is too short.");
        for (int i = 0; i < Magic.Length; i++)
            if (blob[i] != Magic[i])
                throw new InvalidDataException("not a GTA credential file.");
        if (blob[Magic.Length] != FormatVersion)
            throw new InvalidDataException($"unsupported file version {blob[Magic.Length]}.");

        var (encKey, macKey) = DeriveKeys();
        try
        {
            int bodyLen = blob.Length - TagLength;
            byte[] expected = HMACSHA256.HashData(macKey, new ReadOnlySpan<byte>(blob, 0, bodyLen));
            if (!CryptographicOperations.FixedTimeEquals(expected, new ReadOnlySpan<byte>(blob, bodyLen, TagLength)))
                throw new InvalidDataException("integrity check failed (file was changed, damaged, or built with a different key).");

            int cipherStart = Magic.Length + 1 + IvLength;
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = encKey;
            return aes.DecryptCbc(
                new ReadOnlySpan<byte>(blob, cipherStart, bodyLen - cipherStart),
                new ReadOnlySpan<byte>(blob, Magic.Length + 1, IvLength),
                PaddingMode.PKCS7);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    // JsonNode only (no reflection serializer).
    private static Dictionary<string, string> ParseJson(byte[] utf8)
    {
        var node = JsonNode.Parse(utf8) as JsonObject
                   ?? throw new InvalidDataException("decrypted content is not a JSON object.");
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in node)
            table[kv.Key] = kv.Value?.ToString() ?? "";
        return table;
    }
}
#endif
