using System;
using Mathom.Web.Domain;

namespace Mathom.Web.Pages;

/// <summary>Relative "time ago" formatting for timeline entries.</summary>
public static class RelativeTime
{
    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d < TimeSpan.FromSeconds(45)) return "just now";
        if (d < TimeSpan.FromMinutes(60)) return $"{(int)d.TotalMinutes}m";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours}h";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays}d";
        return t.ToLocalTime().ToString("MMM d");
    }
}

/// <summary>Maps an item's processing status to a small status pill for the timeline.</summary>
public static class StatusView
{
    public static bool InFlight(ItemStatus s) => s is ItemStatus.Pending or ItemStatus.Processing;

    /// <summary>Returns the pill CSS modifier and label, or empty strings when the item is settled (Ready).</summary>
    public static (string Css, string Label) Pill(ItemStatus status, SourceType source) => status switch
    {
        ItemStatus.Pending => ("pending", "captured"),
        ItemStatus.Processing => ("processing", source == SourceType.Voice ? "transcribing…" : "thinking…"),
        ItemStatus.Failed => ("failed", "needs attention"),
        _ => ("", ""),
    };
}
