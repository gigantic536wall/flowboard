using System.Globalization;
using System.Security.Claims;
using Flowboard.Core.Domain;

namespace Flowboard.Web.Components.Board;

/// <summary>Small presentation helpers shared by the board components.</summary>
public static class Ui
{
    public const string DisplayNameClaim = "display_name";

    private static readonly string[] Palette =
        ["#6366f1", "#0ea5e9", "#10b981", "#f59e0b", "#ef4444", "#a855f7", "#14b8a6", "#f97316", "#64748b", "#ec4899"];

    public static readonly Labels[] AllLabels =
        [Labels.Red, Labels.Orange, Labels.Yellow, Labels.Green, Labels.Blue, Labels.Purple];

    public static string UserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Not signed in.");

    public static string DisplayName(ClaimsPrincipal user) =>
        user.FindFirstValue(DisplayNameClaim) ?? user.Identity?.Name ?? "Someone";

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpper(CultureInfo.CurrentCulture),
            _ => string.Concat(parts[0][0], parts[^1][0]).ToUpper(CultureInfo.CurrentCulture),
        };
    }

    /// <summary>A stable colour per person, so everyone sees the same colour for the same colleague.</summary>
    public static string ColorFor(string userId)
    {
        var hash = 0;
        foreach (var c in userId)
        {
            hash = unchecked((hash * 31) + c);
        }

        return Palette[(hash & int.MaxValue) % Palette.Length];
    }

    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        return span.TotalSeconds switch
        {
            < 45 => "just now",
            < 90 => "a minute ago",
            < 3600 => $"{(int)span.TotalMinutes} minutes ago",
            < 7200 => "an hour ago",
            < 86400 => $"{(int)span.TotalHours} hours ago",
            _ => when.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture),
        };
    }

    public static IEnumerable<Labels> Split(Labels labels) => AllLabels.Where(l => labels.HasFlag(l));

    public static string RoleName(Role role) => role switch
    {
        Role.Owner => "Owner",
        Role.Member => "Member",
        _ => "Viewer",
    };
}
