using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.Clients.Qobuz.Queue;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Indexers.Qobuz;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    public interface IQobuzProxy
    {
        List<DownloadClientItem> GetQueue();

        Task<string> Download(RemoteAlbum remoteAlbum, QobuzSettings settings, IIndexer indexer);

        void RemoveFromQueue(string downloadId);
    }

    /// <summary>
    /// Bridges Lidarr's download-client contract to the Qobuz download queue.
    /// </summary>
    /// <remarks>
    /// Receives settings as an argument rather than reading them from a provider, because
    /// Lidarr registers download clients as singletons and reassigns <c>Definition</c> on the
    /// shared instance per resolution; a settings read that happens later in a call can belong
    /// to a different configured client.
    /// </remarks>
    public class QobuzProxy : IQobuzProxy,
        IQobuzAlbumDownloadExecutor,
        IHandle<ApplicationShutdownRequested>,
        IDisposable
    {
        private readonly ICached<DateTime?> _startTimeCache;
        private readonly IQobuzSessionProvider _sessionProvider;
        private readonly DownloadTaskQueue _taskQueue;
        private readonly HttpClient _httpClient;
        private readonly ITrackTagger _tagger;
        private readonly ILyricProvider _lyricProvider;
        private readonly Logger _logger;
        private bool _disposed;

        public QobuzProxy(
            ICacheManager cacheManager,
            IQobuzSessionProvider sessionProvider,
            Logger logger)
        {
            _startTimeCache = cacheManager.GetCache<DateTime?>(GetType(), "startTimes");
            _sessionProvider = sessionProvider;
            _logger = logger;

            _httpClient = new HttpClient();
            _tagger = new TagLibTrackTagger(logger);
            _lyricProvider = new LrcLibLyricProvider(_httpClient, logger);

            _taskQueue = new DownloadTaskQueue(this, logger);
            _taskQueue.Start();
        }

        public List<DownloadClientItem> GetQueue()
        {
            // Failed items are included. Previously the listing selected only Completed,
            // Queued and Downloading, so a failed album silently vanished from the queue
            // instead of being reported to Lidarr as a failure it could act on.
            return _taskQueue.GetQueueListing()
                .OrderBy(StatusOrder)
                .Select(ToDownloadClientItem)
                .ToList();
        }

        public void RemoveFromQueue(string downloadId)
        {
            if (_taskQueue.RemoveItem(downloadId))
            {
                _startTimeCache.Remove(downloadId);
            }
        }

        public async Task<string> Download(RemoteAlbum remoteAlbum, QobuzSettings settings, IIndexer indexer)
        {
            if (remoteAlbum?.Release == null)
            {
                throw new ArgumentException("A release is required.", nameof(remoteAlbum));
            }

            var downloadUrl = remoteAlbum.Release.DownloadUrl?.Trim();

            // Rejected here rather than enqueued. Previously an unparseable URL produced a null
            // item that was queued regardless, which threw ArgumentNullException when used as a
            // dictionary key and then dereferenced null for its id.
            if (!QobuzAlbumReference.TryParse(downloadUrl, out var albumReference))
            {
                throw new DownloadClientException(
                    "Qobuz cannot download '{0}': the release URL is not a Qobuz album link.",
                    downloadUrl ?? "(none)");
            }

            if (!AudioQualities.TryFromContainer(remoteAlbum.Release.Container, out var qualitySpec))
            {
                // Previously an unrecognised container silently became MP3 320.
                throw new DownloadClientException(
                    "Qobuz cannot determine the requested audio quality from container '{0}'.",
                    remoteAlbum.Release.Container ?? "(none)");
            }

            var credentials = ResolveCredentials(indexer);

            var request = new AlbumDownloadRequest(
                settings.DownloadPath,
                qualitySpec.Quality,
                settings.SaveSyncedLyrics,
                settings.UseLRCLIB);

            var job = new QobuzDownloadJob(
                downloadId: Guid.NewGuid().ToString(),
                albumReference: albumReference!,
                credentials: credentials,
                request: request,
                remoteAlbum: remoteAlbum,
                title: remoteAlbum.Release.Album ?? albumReference!.AlbumId,
                artist: remoteAlbum.Release.Artist ?? string.Empty,
                isExplicit: false,
                estimatedTotalBytes: remoteAlbum.Release.Size,
                totalTracks: 0);

            await _taskQueue.EnqueueAsync(job).ConfigureAwait(false);

            return job.DownloadId;
        }

        /// <summary>
        /// Runs one queued album download.
        /// </summary>
        public async Task<AlbumDownloadOutcome> ExecuteAsync(
            QobuzDownloadJob job,
            CancellationToken cancellationToken)
        {
            var session = _sessionProvider.GetSession(job.Credentials);

            // Blocking library call, moved off the queue worker thread deliberately and in one
            // place, rather than being hidden behind a fake-async wrapper per API method.
            var plan = await Task.Run(
                    () => session.GetAlbumDownloadPlan(job.AlbumReference.AlbumId),
                    cancellationToken)
                .ConfigureAwait(false);

            var spec = AudioQualities.For(job.Request.Quality);
            job.RefineTotals(plan.Tracks.Count, spec.EstimateBytes(plan.Album.DurationSeconds));

            var downloader = new QobuzAlbumDownloader(session, _tagger, _lyricProvider, _logger);

            var result = await downloader
                .DownloadAsync(plan, job.Request, new JobProgressObserver(job), cancellationToken)
                .ConfigureAwait(false);

            if (result.IsCompleteSuccess)
            {
                return AlbumDownloadOutcome.Success();
            }

            if (result.DownloadedTracks == 0)
            {
                return AlbumDownloadOutcome.Failure(
                    $"No tracks could be downloaded for {job.Title}.");
            }

            {
                // Reported as Failed, not Warning. Lidarr ignores Warning entirely for
                // terminal items: CompletedDownloadService.Check returns unless the status is
                // Completed, and FailedDownloadService acts only on Failed, so a Warning item
                // is never imported, never failed, never blocklisted and never retried — it
                // just sits in the queue forever. Upstream pins that behaviour in its own test
                // (CompletedDownloadServiceTests/ProcessFixture.cs:105 lists Warning under
                // should_not_process_if_download_status_isnt_completed). Failing it means
                // Lidarr can blocklist this release and search for another source, which is
                // what a user actually wants from an incomplete album.
                return AlbumDownloadOutcome.Failure(
                    $"Only {result.DownloadedTracks} of {plan.Tracks.Count} tracks downloaded "
                    + $"for {job.Title}; {result.FailedTracks} failed.");
            }
        }

        /// <summary>
        /// Stops the download queue when Lidarr shuts down.
        /// </summary>
        /// <remarks>
        /// Lidarr registers this type as a singleton and never disposes the container, so
        /// <see cref="IDisposable"/> alone is decorative — without this handler the queue's
        /// bounded shutdown logic would be unreachable in production. <c>IHandle</c> is the
        /// host's own convention for this, used by <c>Scheduler</c>, <c>CommandExecutor</c>
        /// and <c>DatabaseTarget</c>.
        /// </remarks>
        public void Handle(ApplicationShutdownRequested message)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _taskQueue.Dispose();
            _httpClient.Dispose();
        }

        /// <summary>
        /// Builds a credential snapshot from the indexer that produced this release.
        /// </summary>
        /// <remarks>
        /// Lidarr hands the originating indexer to <c>Download</c>, which is where Qobuz
        /// credentials actually live: they are indexer settings, not download-client settings.
        /// The previous code ignored that argument entirely and relied on a global that only the
        /// indexer's search path ever initialised, so a grab that reached the download client
        /// first had nothing to authenticate with.
        /// </remarks>
        private QobuzCredentials ResolveCredentials(IIndexer indexer)
        {
            if (indexer?.Definition is IndexerDefinition { Settings: QobuzIndexerSettings indexerSettings })
            {
                return indexerSettings.ToCredentials();
            }

            throw new DownloadClientException(
                "Qobuz credentials are unavailable. The release must come from a configured "
                + "Qobuz indexer, which is where the account details are stored.");
        }

        /// <summary>Orders the listing so active work appears before finished work.</summary>
        private static int StatusOrder(DownloadProgressSnapshot snapshot) => snapshot.Status switch
        {
            DownloadItemStatus.Downloading => 0,
            DownloadItemStatus.Queued => 1,
            DownloadItemStatus.Completed => 2,
            DownloadItemStatus.Warning => 3,
            DownloadItemStatus.Failed => 4,
            _ => 5
        };

        private DownloadClientItem ToDownloadClientItem(DownloadProgressSnapshot snapshot)
        {
            var format = AudioQualities.For(snapshot.Quality).DisplayLabel;

            var title = $"{snapshot.Artist} - {snapshot.Title} [WEB] [{format}]";

            if (snapshot.IsExplicit)
            {
                title += " [Explicit]";
            }

            var item = new DownloadClientItem
            {
                DownloadId = snapshot.DownloadId,
                Title = title,

                // Bytes, which is what every Lidarr consumer of these fields assumes.
                TotalSize = snapshot.ReportedTotalBytes,
                RemainingSize = snapshot.RemainingBytes,
                RemainingTime = GetRemainingTime(snapshot),
                Status = snapshot.Status,
                Message = snapshot.Message,
                CanMoveFiles = true,
                CanBeRemoved = true,
            };

            if (snapshot.OutputDirectory.IsNotNullOrWhiteSpace())
            {
                item.OutputPath = new OsPath(snapshot.OutputDirectory);
            }

            return item;
        }

        private TimeSpan? GetRemainingTime(DownloadProgressSnapshot snapshot)
        {
            // Every terminal status must clear the cache entry, including Warning. Warning is
            // this plugin's partial-album outcome, so omitting it both leaked an entry per
            // partially-failed album for the process lifetime and kept reporting an ETA for
            // work that had already stopped.
            if (snapshot.Status is DownloadItemStatus.Completed
                or DownloadItemStatus.Failed
                or DownloadItemStatus.Warning)
            {
                _startTimeCache.Remove(snapshot.DownloadId);
                return null;
            }

            var progress = snapshot.Progress;

            if (!progress.HasValue || progress.Value <= 0)
            {
                return null;
            }

            var started = _startTimeCache.Find(snapshot.DownloadId);

            if (started == null)
            {
                _startTimeCache.Set(snapshot.DownloadId, DateTime.UtcNow);
                return null;
            }

            var elapsed = DateTime.UtcNow - started.Value;
            var fraction = Math.Min(progress.Value, 0.999d);

            return TimeSpan.FromTicks((long)(elapsed.Ticks * (1 - fraction) / fraction));
        }

        /// <summary>
        /// Forwards album-download progress onto the job, accumulating the downloader's
        /// per-transfer deltas into the job's running byte total.
        /// </summary>
        private sealed class JobProgressObserver : IAlbumDownloadObserver
        {
            private readonly QobuzDownloadJob _job;

            internal JobProgressObserver(QobuzDownloadJob job)
            {
                _job = job;
            }

            public void OnOutputDirectoryResolved(string outputDirectory) =>
                _job.OnOutputDirectoryResolved(outputDirectory);

            public void OnBytesTransferred(long additionalBytes) =>
                _job.OnBytesTransferred(additionalBytes);

            public void OnTrackCompleted(QobuzTrackMetadata track) => _job.OnTrackCompleted(track);

            public void OnTrackFailed(QobuzTrackMetadata track, Exception exception) =>
                _job.OnTrackFailed(track, exception);
        }
    }
}
