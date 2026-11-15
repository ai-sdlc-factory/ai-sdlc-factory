using System.Globalization;
using System.Text;

namespace DevFoundry.AlertIntake;

/// <summary>
/// What the intake has done since it started, in Prometheus' text format, so a dashboard can show it and a rule can alert on it. The counters only
/// count; they carry no text from alerts.
/// </summary>
public sealed class IntakeMetrics(IntakeSettings settings, RouteTable routes)
{
    public static readonly string[] Actions =
        [AlertOutcome.Created, AlertOutcome.Duplicate, AlertOutcome.Commented, AlertOutcome.Grouped, AlertOutcome.RateLimited, AlertOutcome.Ignored, AlertOutcome.DryRun, AlertOutcome.Failed];

    public static readonly string[] RejectReasons = ["unauthorized", "too_large", "invalid"];

    private readonly long[] outcomes = new long[Actions.Length];
    private readonly long[] rejected = new long[RejectReasons.Length];
    private long notifications;
    private long alerts;

    public void Notification(int alertCount)
    {
        Interlocked.Increment(ref notifications);
        Interlocked.Add(ref alerts, alertCount);
    }

    public void Outcome(string action)
    {
        var index = Array.IndexOf(Actions, action);
        if (index >= 0)
        {
            Interlocked.Increment(ref outcomes[index]);
        }
    }

    public void Rejected(string reason)
    {
        var index = Array.IndexOf(RejectReasons, reason);
        if (index >= 0)
        {
            Interlocked.Increment(ref rejected[index]);
        }
    }

    public string Render()
    {
        var text = new StringBuilder();
        void Header(string name, string help, string type)
        {
            text.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
            text.Append("# TYPE ").Append(name).Append(' ').AppendLine(type);
        }
        string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        Header("alert_intake_notifications_total", "Alertmanager notifications accepted.", "counter");
        text.AppendLine($"alert_intake_notifications_total {Number(Interlocked.Read(ref notifications))}");
        Header("alert_intake_alerts_total", "Alerts in the accepted notifications.", "counter");
        text.AppendLine($"alert_intake_alerts_total {Number(Interlocked.Read(ref alerts))}");
        Header("alert_intake_outcomes_total", "What happened to each alert: created, duplicate, commented, rate_limited, ignored, dry_run or failed.", "counter");
        for (var i = 0; i < Actions.Length; i++)
        {
            text.AppendLine($"alert_intake_outcomes_total{{action=\"{Actions[i]}\"}} {Number(Interlocked.Read(ref outcomes[i]))}");
        }
        Header("alert_intake_rejected_total", "Notifications refused before any alert was looked at.", "counter");
        for (var i = 0; i < RejectReasons.Length; i++)
        {
            text.AppendLine($"alert_intake_rejected_total{{reason=\"{RejectReasons[i]}\"}} {Number(Interlocked.Read(ref rejected[i]))}");
        }
        Header("alert_intake_dry_run", "1 when the intake only logs the tickets it would open.", "gauge");
        text.AppendLine($"alert_intake_dry_run {(settings.DryRun ? 1 : 0)}");
        Header("alert_intake_routes", "How many routes the route table has.", "gauge");
        text.AppendLine($"alert_intake_routes {Number(routes.Count)}");
        Header("alert_intake_max_tickets_per_hour", "The limit of new tickets per hour.", "gauge");
        text.AppendLine($"alert_intake_max_tickets_per_hour {Number(settings.MaxTicketsPerHour)}");
        return text.ToString();
    }
}
