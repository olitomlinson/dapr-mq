using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DaprMQ.ApiServer.Services;

// TEMPORARY backstop for dapr/dapr#10835: the Dapr sidecar can dispatch an actor call while another turn on
// the same actor is still executing - when a caller cancels calls queued behind that turn, the sidecar sends
// them to the app about 50ms later. Concurrent turns then overwrite each other's state (lost LockCount,
// orphaned lock rows, "Count desync").
//
// This guard restores one turn at a time per actor inside the actor host, modelled on the SDK-side fix in
// dapr/dotnet-sdk#1937 (ActorTurnLock). Remove it - the UseActorTurnGuard() line in Program.cs, this file and
// ActorTurnGuardTests - once the runtime (dapr/dapr#10835) or the SDK (dapr/dotnet-sdk#1937) ships a fix.

/// <summary>
/// One turn at a time per actor key. A call holding the same non-empty reentrancy id as the chain that holds
/// the turn may enter alongside it (A -> B -> A), as Dapr reentrancy allows; any other call waits. A waiter
/// whose token is cancelled leaves the queue and never enters. Idle actors leave no entry behind.
/// </summary>
public sealed class ActorTurnGuard
{
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _sync = new();

    /// <summary>Actors with a turn in progress or a call waiting.</summary>
    internal int TrackedActors
    {
        get { lock (_sync) { return _entries.Count; } }
    }

    public async Task<Lease> AcquireAsync(string actorKey, string? reentrancyId, CancellationToken cancellationToken)
    {
        reentrancyId = string.IsNullOrEmpty(reentrancyId) ? null : reentrancyId;
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(actorKey, out entry!))
            {
                entry = new Entry();
                _entries.Add(actorKey, entry);
            }
            entry.References++; // waiters hold one too, so an entry is never removed and recreated under them
        }

        var waited = false;
        try
        {
            while (true)
            {
                Task available;
                lock (_sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.ActiveCalls == 0 || (reentrancyId != null && entry.ReentrancyId == reentrancyId))
                    {
                        if (entry.ActiveCalls == 0)
                        {
                            entry.ReentrancyId = reentrancyId;
                        }
                        entry.ActiveCalls++;
                        return new Lease(this, actorKey, entry, waited);
                    }
                    available = entry.Available.Task;
                }

                waited = true;
                await available.WaitAsync(cancellationToken);
            }
        }
        catch
        {
            lock (_sync)
            {
                ReleaseReference(actorKey, entry);
            }
            throw;
        }
    }

    private void Release(string actorKey, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.ActiveCalls == 0)
            {
                entry.ReentrancyId = null;
                entry.WakeWaiters();
            }
            ReleaseReference(actorKey, entry);
        }
    }

    private void ReleaseReference(string actorKey, Entry entry)
    {
        if (--entry.References == 0)
        {
            _entries.Remove(actorKey);
        }
    }

    internal sealed class Entry
    {
        public int References;
        public int ActiveCalls;
        public string? ReentrancyId;
        public TaskCompletionSource Available = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void WakeWaiters()
        {
            var available = Available;
            Available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            available.SetResult();
        }
    }

    /// <summary>A turn on one actor; disposing it lets the next call in.</summary>
    public sealed class Lease : IDisposable
    {
        private ActorTurnGuard? _owner;
        private readonly string _actorKey;
        private readonly Entry _entry;

        internal Lease(ActorTurnGuard owner, string actorKey, Entry entry, bool waited)
        {
            _owner = owner;
            _actorKey = actorKey;
            _entry = entry;
            Waited = waited;
        }

        /// <summary>True if the call had to wait for another turn - which the sidecar should never cause.</summary>
        public bool Waited { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_actorKey, _entry);
    }
}

public static class ActorTurnGuardMiddleware
{
    private const string ReentrancyHeader = "Dapr-Reentrancy-Id";

    /// <summary>
    /// TEMPORARY (dapr/dapr#10835): wraps every request the sidecar sends to an actor -
    /// /actors/{type}/{id}/method/..., reminders, timers and deactivation - in an <see cref="ActorTurnGuard"/>
    /// turn held until the SDK has finished the whole request, state save and response included. A request
    /// aborted while it waits is dropped without running.
    /// </summary>
    public static IApplicationBuilder UseActorTurnGuard(this IApplicationBuilder app, ILogger logger)
    {
        var guard = new ActorTurnGuard();
        return app.Use(async (context, next) =>
        {
            var actorKey = ActorKeyOf(context.Request.Path);
            if (actorKey == null)
            {
                await next(context);
                return;
            }

            var reentrancyId = context.Request.Headers[ReentrancyHeader].FirstOrDefault();
            ActorTurnGuard.Lease lease;
            try
            {
                lease = await guard.AcquireAsync(actorKey, reentrancyId, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Actor turn guard dropped {Method} {Path}: the sidecar dispatched it while another turn on {Actor} was in progress, and its request was aborted while it waited (dapr/dapr#10835)",
                    context.Request.Method, context.Request.Path, actorKey);
                return;
            }

            using (lease)
            {
                if (lease.Waited)
                {
                    logger.LogWarning(
                        "Actor turn guard held back {Method} {Path} until the turn in progress on {Actor} finished (dapr/dapr#10835)",
                        context.Request.Method, context.Request.Path, actorKey);
                }
                await next(context);
            }
        });
    }

    /// <summary>"{type}/{id}" for a request to /actors/{type}/{id}[/...], otherwise null.</summary>
    internal static string? ActorKeyOf(PathString path)
    {
        var parts = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "actors" ? $"{parts[1]}/{parts[2]}" : null;
    }
}
