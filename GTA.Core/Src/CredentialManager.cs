using System.Text.Json.Nodes;

namespace GTA.Core;

/// <summary>Known account kinds. Values are resolved from init.ini (cred_&lt;kind&gt;_id / _password) first.</summary>
public enum AccountKind
{
    /// <summary>Primary account (proxy / SSO login).</summary>
    PrimaryAccount,

    /// <summary>Mail account (cloud login).</summary>
    MailAccount,
}

/// <summary>One loaded (Id, Password) pair. ToString() never prints the password.</summary>
public sealed class AccountCredential
{
    public string Id { get; set; } = "";
    public string Password { get; set; } = "";

    public override string ToString() => $"{Id} (password redacted)";
}

/// <summary>Loads account credentials: init.ini first, then a JSON file in the Credentials folder next to the exe.</summary>
public static class CredentialManager
{
    private const string FolderName = "Credentials";


    /// <summary>AppContext.BaseDirectory\Credentials (runtime session/token cache folder).</summary>
    public static string CredentialsFolder => Path.Combine(AppContext.BaseDirectory, FolderName);

    public static AccountCredential Load(AccountKind kind) => Load(kind.ToString());

    /// <summary>Load an account by name.</summary>
    public static AccountCredential Load(string accountFileName)
    {
        string prefix = "cred_" + accountFileName.ToLowerInvariant() + "_";
        string iniId = InitSettings.Get(prefix + "id");
        string iniPw = InitSettings.Get(prefix + "password");
        if (!string.IsNullOrWhiteSpace(iniId) && !string.IsNullOrWhiteSpace(iniPw))
            return new AccountCredential { Id = iniId, Password = iniPw };

        string path = Path.Combine(CredentialsFolder, accountFileName + ".json");

        if (!File.Exists(path))
        {
#if CO_WORKER_USE
            throw new FileNotFoundException(
                $"Credential for \"{accountFileName}\" is not set. Open Settings (left side, under \"Task\") and enter it.",
                path);
#else
            throw new FileNotFoundException(
                $"Credential not found: set \"{prefix}id\" and \"{prefix}password\" in Config\\init.ini, " +
                $"or create \"{path}\" shaped like: {{ \"Id\": \"user@example.com\", \"Password\": \"...\" }}",
                path);
#endif
        }

        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { throw new IOException($"Could not read credential file \"{path}\": {ex.Message}", ex); }

        // JsonNode instead of JsonSerializer.Deserialize<T>: no reflection needed.
        AccountCredential? cred;
        try
        {
            var obj = JsonNode.Parse(json) as JsonObject;
            cred = obj is null ? null : new AccountCredential
            {
                Id       = ReadCaseInsensitive(obj, "Id"),
                Password = ReadCaseInsensitive(obj, "Password"),
            };
        }
        catch (Exception ex) { throw new InvalidDataException($"Credential file \"{path}\" is not valid JSON: {ex.Message}", ex); }

        if (cred is null || string.IsNullOrWhiteSpace(cred.Id) || string.IsNullOrWhiteSpace(cred.Password))
            throw new InvalidDataException($"Credential file \"{path}\" is missing a non-empty \"Id\" and/or \"Password\" field.");

        return cred;
    }

    private static string ReadCaseInsensitive(JsonObject obj, string name)
    {
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value?.ToString() ?? "";
        return "";
    }
}
