using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Interface for the Blob Reaper Actor that deletes offloaded object-store blobs after a delay.
/// </summary>
public interface IBlobReaperActor : IActor
{
    /// <summary>
    /// Schedules deletion of the given blob reference after the specified delay.
    /// </summary>
    Task ScheduleDeletion(ScheduleDeletionRequest request);

    /// <summary>
    /// Pushes the scheduled deletion out to fire NewDelaySeconds from now, but only if that's
    /// later than the currently scheduled deletion time. A no-op if no deletion is scheduled for
    /// this blob reference (e.g. reminder already fired) or if the new delay would fire earlier.
    /// </summary>
    Task PostponeDeletion(PostponeDeletionRequest request);
}
