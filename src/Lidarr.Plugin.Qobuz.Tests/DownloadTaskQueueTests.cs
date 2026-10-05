using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lidarr.Plugin.Qobuz.Tests.Fakes;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.Download.Clients.Qobuz.Queue;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    [TestFixture]
    public class DownloadTaskQueueTests
    {
        private Logger _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _logger = LogManager.GetLogger("test");
        }

        private static QobuzDownloadJob CreateJob(string id = "job-1", long estimatedBytes = 10_000)
        {
            QobuzAlbumReference.TryParse("https://open.qobuz.com/album/album-1", out var reference);

            return new QobuzDownloadJob(
                downloadId: id,
                albumReference: reference!,
                credentials: TestMetadata.Credentials(),
                request: new AlbumDownloadRequest("/tmp/x", AudioQuality.FLACLossless, false, false),
                remoteAlbum: null!,
                title: "Test Album",
                artist: "Test Artist",
                isExplicit: false,
                estimatedTotalBytes: estimatedBytes,
                totalTracks: 3);
        }

        [Test]
        public async Task Executes_a_queued_job()
        {
            var executor = new RecordingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();

            await queue.EnqueueAsync(CreateJob());

            await executor.WaitForExecutions(1);
            executor.Executed.Should().ContainSingle(j => j.DownloadId == "job-1");
        }

        [Test]
        public async Task A_job_is_listed_as_soon_as_it_is_enqueued()
        {
            var executor = new BlockingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);

            // Not started, so nothing can consume it yet.
            await queue.EnqueueAsync(CreateJob());

            queue.GetQueueListing().Should().ContainSingle(s => s.DownloadId == "job-1");
        }

        /// <summary>
        /// Lidarr must still observe a Completed item to import its files, so terminal jobs
        /// cannot be evicted automatically.
        /// </summary>
        [Test]
        public async Task Retains_completed_jobs_until_they_are_explicitly_removed()
        {
            var executor = new RecordingExecutor { CompleteJobs = true };
            using var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();

            await queue.EnqueueAsync(CreateJob());
            await executor.WaitForExecutions(1);

            var listing = queue.GetQueueListing();
            listing.Should().ContainSingle(s => s.Status == DownloadItemStatus.Completed);

            queue.RemoveItem("job-1").Should().BeTrue();
            queue.GetQueueListing().Should().BeEmpty();
        }

        [Test]
        public async Task Retains_failed_jobs_so_lidarr_can_act_on_them()
        {
            var executor = new ThrowingExecutor("qobuz exploded");
            using var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();

            await queue.EnqueueAsync(CreateJob());
            await WaitFor(() => queue.GetQueueListing()
                .Any(s => s.Status == DownloadItemStatus.Failed));

            var snapshot = queue.GetQueueListing().Single();
            snapshot.Status.Should().Be(DownloadItemStatus.Failed);
            snapshot.Message.Should().Contain("qobuz exploded");
        }

        [Test]
        public async Task Rejects_a_duplicate_download_id()
        {
            var executor = new BlockingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);

            await queue.EnqueueAsync(CreateJob("dup"));

            Func<Task> act = () => queue.EnqueueAsync(CreateJob("dup"));

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public void Rejects_a_null_job()
        {
            var executor = new RecordingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);

            Func<Task> act = () => queue.EnqueueAsync(null!);

            act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Test]
        public void Removing_an_unknown_id_reports_false()
        {
            var executor = new RecordingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);

            queue.RemoveItem("nope").Should().BeFalse();
            queue.RemoveItem(null!).Should().BeFalse();
            queue.RemoveItem("  ").Should().BeFalse();
        }

        [Test]
        public async Task Removing_a_running_job_cancels_it()
        {
            var executor = new BlockingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();

            await queue.EnqueueAsync(CreateJob("cancel-me"));
            await WaitFor(() => executor.Started.Count == 1);

            queue.RemoveItem("cancel-me").Should().BeTrue();

            await WaitFor(() => executor.Cancelled.Count == 1);
            executor.Cancelled.Should().ContainSingle();
        }

        /// <summary>
        /// The old loop was started with a default token, so it could never exit and its drain
        /// after the loop was unreachable. Disposal must now actually stop the worker.
        /// </summary>
        [Test]
        public async Task Dispose_stops_the_worker_and_cancels_running_work()
        {
            var executor = new BlockingExecutor();
            var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();

            await queue.EnqueueAsync(CreateJob("in-flight"));
            await WaitFor(() => executor.Started.Count == 1);

            queue.Dispose();

            await WaitFor(() => executor.Cancelled.Count == 1);
            executor.Cancelled.Should().ContainSingle();
        }

        [Test]
        public async Task Enqueueing_after_dispose_is_rejected()
        {
            var executor = new RecordingExecutor();
            var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();
            queue.Dispose();

            Func<Task> act = () => queue.EnqueueAsync(CreateJob("late"));

            await act.Should().ThrowAsync<ObjectDisposedException>();
        }

        [Test]
        public void Dispose_is_idempotent()
        {
            var executor = new RecordingExecutor();
            var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();

            queue.Dispose();

            queue.Invoking(q => q.Dispose()).Should().NotThrow();
        }

        [Test]
        public void Start_is_idempotent()
        {
            var executor = new RecordingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);

            queue.Start();

            queue.Invoking(q => q.Start()).Should().NotThrow();
        }

        /// <summary>
        /// Bounded concurrency must hold even under a burst, and no permit may leak: if one
        /// did, the queue would eventually stop accepting work entirely.
        /// </summary>
        [Test]
        public async Task Never_runs_more_albums_at_once_than_its_bound()
        {
            var executor = new ConcurrencyProbingExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger, maxConcurrentAlbums: 2);
            queue.Start();

            for (var i = 0; i < 12; i++)
            {
                await queue.EnqueueAsync(CreateJob($"job-{i}"));
            }

            await executor.WaitForExecutions(12);

            executor.MaxObservedConcurrency.Should().BeLessOrEqualTo(2);
            executor.Executions.Should().Be(12,
                "every job must run, which it cannot if a semaphore permit leaked");
        }

        [Test]
        public async Task A_failing_job_does_not_stop_later_jobs()
        {
            var executor = new FailFirstExecutor();
            using var queue = new DownloadTaskQueue(executor, _logger);
            queue.Start();

            await queue.EnqueueAsync(CreateJob("bad"));
            await queue.EnqueueAsync(CreateJob("good"));

            await WaitFor(() => executor.Seen.Count == 2);
            executor.Seen.Should().Contain("good");
        }

        private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("Condition was not met in time.");
        }

        private class RecordingExecutor : IQobuzAlbumDownloadExecutor
        {
            internal ConcurrentBag<QobuzDownloadJob> Executed { get; } = new();

            internal bool CompleteJobs { get; set; }

            public Task ExecuteAsync(QobuzDownloadJob job, CancellationToken cancellationToken)
            {
                Executed.Add(job);

                if (CompleteJobs)
                {
                    job.MarkCompleted();
                }

                return Task.CompletedTask;
            }

            internal async Task WaitForExecutions(int count, int timeoutMs = 5000)
            {
                await WaitFor(() => Executed.Count >= count, timeoutMs);
            }
        }

        private sealed class BlockingExecutor : IQobuzAlbumDownloadExecutor
        {
            internal ConcurrentBag<string> Started { get; } = new();

            internal ConcurrentBag<string> Cancelled { get; } = new();

            public async Task ExecuteAsync(QobuzDownloadJob job, CancellationToken cancellationToken)
            {
                Started.Add(job.DownloadId);

                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.Add(job.DownloadId);
                    throw;
                }
            }
        }

        private sealed class ThrowingExecutor : IQobuzAlbumDownloadExecutor
        {
            private readonly string _message;

            internal ThrowingExecutor(string message)
            {
                _message = message;
            }

            public Task ExecuteAsync(QobuzDownloadJob job, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException(_message);
            }
        }

        private sealed class FailFirstExecutor : IQobuzAlbumDownloadExecutor
        {
            internal ConcurrentBag<string> Seen { get; } = new();

            public Task ExecuteAsync(QobuzDownloadJob job, CancellationToken cancellationToken)
            {
                Seen.Add(job.DownloadId);

                if (job.DownloadId == "bad")
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.CompletedTask;
            }
        }

        private sealed class ConcurrencyProbingExecutor : IQobuzAlbumDownloadExecutor
        {
            private int _current;
            private int _max;
            private int _executions;

            internal int MaxObservedConcurrency => Volatile.Read(ref _max);

            internal int Executions => Volatile.Read(ref _executions);

            public async Task ExecuteAsync(QobuzDownloadJob job, CancellationToken cancellationToken)
            {
                var now = Interlocked.Increment(ref _current);

                // Track the high-water mark without losing updates.
                int observed;
                do
                {
                    observed = Volatile.Read(ref _max);
                }
                while (now > observed
                    && Interlocked.CompareExchange(ref _max, now, observed) != observed);

                try
                {
                    await Task.Delay(20, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _current);
                    Interlocked.Increment(ref _executions);
                }
            }

            internal async Task WaitForExecutions(int count, int timeoutMs = 10000)
            {
                await WaitFor(() => Executions >= count, timeoutMs);
            }
        }
    }
}
