using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevFoundry.AlertIntake;

/// <summary>One alert of an Alertmanager notification (webhook payload version 4). Everything in it is data written by rules and by whatever they measure.</summary>
public sealed record Alert(
    string Status,
    Dictionary<string, string>? Labels,
    Dictionary<string, string>? Annotations,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    [property: JsonPropertyName("generatorURL")] string? GeneratorUrl,
    string? Fingerprint)
{
    public const string Firing = "firing";
    public const string Resolved = "resolved";

    public IReadOnlyDictionary<string, string> LabelMap => Labels ?? [];
    public string Name => LabelMap.TryGetValue("alertname", out var name) && name.Length > 0 ? name : "unnamed alert";
    public string Annotation(string key) => Annotations is not null && Annotations.TryGetValue(key, out var value) ? value : "";

    /// <summary>
    /// What identifies this alert across notifications: Alertmanager's own fingerprint when it is a plain token, else a hash of the labels.
    /// It ends up in the ticket, so it is never taken as it comes.
    /// </summary>
    public string Key
    {
        get
        {
            if (!string.IsNullOrEmpty(Fingerprint) && Fingerprint.Length <= 64 && Fingerprint.All(char.IsAsciiLetterOrDigit))
            {
                return Fingerprint.ToLowerInvariant();
            }
            var text = string.Join('\n', LabelMap.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
        }
    }
}

public sealed record AlertMessage(string? Status, List<Alert>? Alerts)
{
    public const int MaxAlerts = 50;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static bool TryParse(string json, out AlertMessage? message, out string error)
    {
        message = null;
        try
        {
            message = JsonSerializer.Deserialize<AlertMessage>(json, Options);
        }
        catch (JsonException)
        {
            error = "the body is not a valid Alertmanager notification";
            return false;
        }
        if (message?.Alerts is null || message.Alerts.Count == 0 || message.Alerts.Count > MaxAlerts)
        {
            error = $"the notification must have 1 to {MaxAlerts} alerts";
            return false;
        }
        if (message.Alerts.Any(alert => alert.Status is not (Alert.Firing or Alert.Resolved)))
        {
            error = "every alert must be firing or resolved";
            return false;
        }
        error = "";
        return true;
    }
}
