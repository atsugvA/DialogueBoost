using Jellyfin.Plugin.DialogueBoost.State;

namespace Jellyfin.Plugin.DialogueBoost.Processing;

/// <summary>
/// What one run actually did about one item and profile.
/// </summary>
/// <remarks>
/// The distinction the record alone cannot carry: an item that was already up to date comes back
/// with its old <c>Success</c> record, which is right — it *is* successful — but a run that encoded
/// nothing then logged "Processed: 28, Skipped: 0". Seen on a real run, 2026-08-28. Whether the
/// work happened now is a property of the run, so it lives here and not in the stored record.
/// </remarks>
public enum ProcessingOutcome
{
    /// <summary>Encoded and published during this run.</summary>
    Written,

    /// <summary>Found already written, at the current settings. Nothing was encoded.</summary>
    AlreadyDone,

    /// <summary>Deliberately not done — dry run, watched, nothing qualified, plugin disabled.</summary>
    Skipped,

    /// <summary>Tried and did not finish. The reason is on the result, and usually on the record.</summary>
    Failed
}

/// <summary>One decision this run made about one item: what happened, why, and what now stands.</summary>
/// <param name="Outcome">What this run did.</param>
/// <param name="Record">
/// The stored record, or the one that would have been stored on a dry run — and <c>null</c> where
/// there is nothing to write down. An item the gate refused has no record: "already watched" is a
/// fact about the account, not about the sidecar, and it stops being true on its own.
/// </param>
/// <param name="Reason">
/// Why, in the words the log uses. Empty where the outcome is its own explanation.
/// </param>
public sealed record ItemProcessingResult(
    ProcessingOutcome Outcome,
    ProcessedItemRecord? Record,
    string? Reason = null);
