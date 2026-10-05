using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Download.Clients.Qobuz.Queue
{
    /// <summary>
    /// Runs queued Qobuz album downloads, bounded in both admission and concurrency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The previous implementation exposed six methods over three independently-mutated
    /// collections, only one of which was guarded by its lock. It also tracked running work in a
    /// list it could never remove from, because it added a continuation task and removed the
    /// inner task, and its background loop was started with a default
    /// <see cref="CancellationToken"/> so it could never exit and its drain was unreachable.
    /// </para>
    /// <para>
    /// Here, all shared state is guarded by a single lock, the worker loop is driven by a real
    /// cancellation token owned by this module, and terminal jobs are retained until Lidarr
    /// explicitly removes them.
    /// </para>
    /// </remarks>
    public sealed class DownloadTaskQueue : IDisposable
    {
        private readonly Channel<QobuzDownloadJob> _channel;
        private readonly Dictionary<string, QueueEntry> _entries = new();
        private readonly object _lock = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly SemaphoreSlim _concurrency;
        private readonly IQobuzAlbumDownloadExecutor _executor;
        private readonly Logger _logger;

        private Task? _worker;
        private bool _disposed;

        public DownloadTaskQueue(
            IQobuzAlbumDownloadExecutor executor,
            Logger logger,
            int capacity = 500,
            int maxConcurrentAlbums = 3)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _logger = logger;

            _channel = Channel.CreateBounded<QobuzDownloadJob>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true
                });

            _concurrency = new SemaphoreSlim(maxConcurrentAlbums, maxConcurrentAlbums);
        }

        /// <summary>Starts the worker loop. Idempotent.</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_worker != null || _disposed)
                {
                    return;
                }

                _worker = Task.Run(() => ProcessQueueAsync(_shutdown.Token));
            }
        }

        /// <summary>
        /// Admits a job to the queue.
        /// </summary>
        /// <remarks>
        /// The job is registered before it is published to the channel, so it can never be
        /// dequeued and executed before it is visible in the listing.
        /// </remarks>
        public async Task EnqueueAsync(QobuzDownloadJob job, CancellationToken cancellationToken = default)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            var entry = new QueueEntry(job);

            lock (_lock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DownloadTaskQueue));
                }

                if (_entries.ContainsKey(job.DownloadId))
                {
                    throw new InvalidOperationException(
                        $"A Qobuz download with id {job.DownloadId} is already queued.");
                }

                _entries[job.DownloadId] = entry;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _shutdown.Token);

                await _channel.Writer.WriteAsync(job, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                // Never leave a registered job that will not be executed.
                lock (_lock)
                {
                    _entries.Remove(job.DownloadId);
                }

                entry.Dispose();
                throw;
            }
        }

        /// <summary>
        /// All known jobs, including completed and failed ones.
        /// </summary>
        /// <remarks>
        /// Terminal jobs are deliberately retained. Lidarr must still observe a Completed item
        /// in order to import its files, so evicting on completion would make finished
        /// downloads disappear before they were imported.
        /// </remarks>
        public IReadOnlyList<DownloadProgressSnapshot> GetQueueListing()
        {
            lock (_lock)
            {
                return _entries.Values
                    .Select(e => e.Job.Snapshot())
                    .ToList();
            }
        }

        /// <summary>
        /// Cancels a job if it is still running and forgets it.
        /// </summary>
        public bool RemoveItem(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return false;
            }

            QueueEntry? entry;

            lock (_lock)
            {
                if (!_entries.Remove(downloadId, out entry))
                {
                    return false;
                }
            }

            entry!.Cancel();
            entry.Dispose();
            return true;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _channel.Writer.TryComplete();

            try
            {
                _shutdown.Cancel();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error signalling Qobuz queue shutdown.");
            }

            var workerStopped = false;

            try
            {
                // Bounded so a wedged download cannot hang Lidarr's shutdown indefinitely.
                workerStopped = _worker == null || _worker.Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                // A faulted worker still counts as stopped: nothing is using the primitives.
                workerStopped = _worker?.IsCompleted ?? true;
                _logger.Debug(ex, "Qobuz queue worker did not stop cleanly.");
            }

            List<QueueEntry> remaining;

            lock (_lock)
            {
                remaining = _entries.Values.ToList();
                _entries.Clear();
            }

            foreach (var entry in remaining)
            {
                entry.Cancel();
                entry.Dispose();
            }

            // Only dispose the primitives once nothing can still be using them. If the worker
            // overran its shutdown budget it is still touching _shutdown and _concurrency, and
            // disposing them underneath it would raise ObjectDisposedException on a background
            // thread during host shutdown. Leaking two handles for the remaining process
            // lifetime is the lesser evil.
            if (workerStopped)
            {
                _shutdown.Dispose();
                _concurrency.Dispose();
            }
            else
            {
                _logger.Warn(
                    "The Qobuz download queue did not stop within 10s; leaving its "
                    + "cancellation handles alive to avoid faulting the in-flight worker.");
            }
        }

        private async Task ProcessQueueAsync(CancellationToken shutdownToken)
        {
            var running = new List<Task>();

            try
            {
                // Completes when the channel is completed and drained, so shutdown is orderly.
                await foreach (var job in _channel.Reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
                {
                    // Acquired after the job is in hand, so a failure between acquiring and
                    // dequeuing cannot leak a permit.
                    await _concurrency.WaitAsync(shutdownToken).ConfigureAwait(false);

                    var task = RunJobAsync(job, shutdownToken);

                    lock (_lock)
                    {
                        running.Add(task);
                        running.RemoveAll(t => t.IsCompleted);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "The Qobuz download queue worker stopped unexpectedly.");
            }

            List<Task> toAwait;

            lock (_lock)
            {
                toAwait = running.ToList();
            }

            try
            {
                await Task.WhenAll(toAwait).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Errors while draining in-flight Qobuz downloads.");
            }
        }

        /// <summary>
        /// Executes one job, always releasing its concurrency permit and always leaving it in a
        /// terminal state.
        /// </summary>
        private async Task RunJobAsync(QobuzDownloadJob job, CancellationToken shutdownToken)
        {
            try
            {
                CancellationToken jobToken;

                lock (_lock)
                {
                    if (!_entries.TryGetValue(job.DownloadId, out var entry))
                    {
                        // Removed between enqueue and execution.
                        return;
                    }

                    jobToken = entry.Token;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    jobToken,
                    shutdownToken);

                job.MarkDownloading();

                await _executor.ExecuteAsync(job, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Qobuz download {0} was cancelled.", job.Title);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Qobuz download failed for {0}.", job.Title);
                job.MarkFailed(ex.Message);
            }
            finally
            {
                _concurrency.Release();
            }
        }

        /// <summary>
        /// Pairs a job with the cancellation source that controls it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// All three members are guarded by one lock. An earlier version used a plain
        /// <c>bool _disposed</c> field and read <c>_cts.Token</c> when it was false, which is
        /// a genuine race rather than a theoretical one: <see cref="RemoveItem"/> runs on a
        /// Lidarr thread while the queue worker is reading <see cref="Token"/>, and a disposal
        /// landing between the flag check and the property read throws
        /// <see cref="ObjectDisposedException"/>. A standalone probe of that exact shape
        /// produced 55 throws in 200,000 attempts, and in the queue the consequence was worse
        /// than the exception: <c>RunJobAsync</c> would catch it and mark a perfectly healthy
        /// download as Failed.
        /// </para>
        /// <para>
        /// Taking a snapshot of the token while holding the lock is what makes the read safe.
        /// A <see cref="CancellationToken"/> is a struct and stays valid to observe even after
        /// its source is disposed, so callers may keep the copy.
        /// </para>
        /// </remarks>
        private sealed class QueueEntry : IDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly object _gate = new();
            private bool _disposed;

            internal QueueEntry(QobuzDownloadJob job)
            {
                Job = job;
            }

            internal QobuzDownloadJob Job { get; }

            /// <summary>
            /// A snapshot of this entry's cancellation token, or an already-cancelled token if
            /// the entry has been retired.
            /// </summary>
            internal CancellationToken Token
            {
                get
                {
                    lock (_gate)
                    {
                        return _disposed ? new CancellationToken(true) : _cts.Token;
                    }
                }
            }

            internal void Cancel()
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    try
                    {
                        _cts.Cancel();
                    }
                    catch (AggregateException ex)
                    {
                        // A registered callback threw. Cancellation still happened, and this
                        // entry is being retired regardless, so there is nothing to recover.
                        _ = ex;
                    }
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _cts.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Executes one queued album download.
    /// </summary>
    /// <remarks>
    /// Kept as a seam so the queue's own behaviour — admission, concurrency, cancellation,
    /// retention, failure capture — can be tested without network access or real credentials.
    /// </remarks>
    public interface IQobuzAlbumDownloadExecutor
    {
        Task ExecuteAsync(QobuzDownloadJob job, CancellationToken cancellationToken);
    }
}
