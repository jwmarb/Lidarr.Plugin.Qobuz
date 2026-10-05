using System;
using System.Threading;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Download.Clients.Qobuz.Queue
{
    /// <summary>
    /// An immutable, consistent view of one album download's progress.
    /// </summary>
    /// <remarks>
    /// Returned as a snapshot so that the queue projection cannot observe a half-updated item,
    /// and so Lidarr's reported size and remaining size always agree with each other.
    /// </remarks>
    public sealed class DownloadProgressSnapshot
    {
        public DownloadProgressSnapshot(
            string downloadId,
            string title,
            string artist,
            bool isExplicit,
            AudioQuality quality,
            DownloadItemStatus status,
            long estimatedTotalBytes,
            long downloadedBytes,
            int completedTracks,
            int failedTracks,
            int totalTracks,
            string? outputDirectory,
            string? message)
        {
            DownloadId = downloadId;
            Title = title;
            Artist = artist;
            IsExplicit = isExplicit;
            Quality = quality;
            Status = status;
            EstimatedTotalBytes = estimatedTotalBytes;
            DownloadedBytes = downloadedBytes;
            CompletedTracks = completedTracks;
            FailedTracks = failedTracks;
            TotalTracks = totalTracks;
            OutputDirectory = outputDirectory;
            Message = message;
        }

        public string DownloadId { get; }

        public string Title { get; }

        public string Artist { get; }

        public bool IsExplicit { get; }

        public AudioQuality Quality { get; }

        public DownloadItemStatus Status { get; }

        /// <summary>
        /// Estimated total size in <b>bytes</b>, carried over from the indexer's estimate.
        /// </summary>
        /// <remarks>
        /// Qobuz exposes no cheap way to learn an album's real byte size, so this is an
        /// estimate from duration and bitrate. It is reported in bytes regardless, because
        /// that is the unit every Lidarr consumer assumes.
        /// </remarks>
        public long EstimatedTotalBytes { get; }

        /// <summary>Bytes actually written to disk so far.</summary>
        public long DownloadedBytes { get; }

        public int CompletedTracks { get; }

        public int FailedTracks { get; }

        public int TotalTracks { get; }

        public string? OutputDirectory { get; }

        /// <summary>Failure detail, surfaced to Lidarr for failed items.</summary>
        public string? Message { get; }

        /// <summary>
        /// Total size to report to Lidarr: the real byte count once finished, the estimate
        /// while still running.
        /// </summary>
        public long ReportedTotalBytes =>
            Status == DownloadItemStatus.Completed && DownloadedBytes > 0
                ? DownloadedBytes
                : Math.Max(EstimatedTotalBytes, DownloadedBytes);

        /// <summary>Bytes still outstanding, never negative.</summary>
        public long RemainingBytes =>
            Status == DownloadItemStatus.Completed
                ? 0
                : Math.Max(ReportedTotalBytes - DownloadedBytes, 0);

        /// <summary>
        /// Fraction complete in the range 0..1, or <see langword="null"/> when unknown.
        /// </summary>
        /// <remarks>
        /// Deliberately capped below 1 until the download actually finishes: a FLAC estimate
        /// derived from uncompressed PCM runs high, so real bytes can pass the estimate while
        /// tracks remain.
        /// </remarks>
        public double? Progress
        {
            get
            {
                if (Status == DownloadItemStatus.Completed)
                {
                    return 1d;
                }

                if (EstimatedTotalBytes <= 0 || DownloadedBytes <= 0)
                {
                    return null;
                }

                return Math.Min(DownloadedBytes / (double)EstimatedTotalBytes, 0.99d);
            }
        }
    }

    /// <summary>
    /// One queued album download: immutable inputs plus the mutable progress the worker owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every input needed to execute the job is captured at enqueue time. Nothing is read back
    /// from a Lidarr provider later, because Lidarr reassigns <c>Definition</c> on its shared
    /// provider singletons and a later read can belong to a different configured client.
    /// </para>
    /// <para>
    /// Mutable progress is confined to interlocked counters and a lock-guarded status, and is
    /// only ever published through <see cref="Snapshot"/>.
    /// </para>
    /// </remarks>
    public sealed class QobuzDownloadJob : IAlbumDownloadObserver
    {
        private readonly object _stateLock = new();

        private long _downloadedBytes;
        private int _completedTracks;
        private int _failedTracks;
        private DownloadItemStatus _status;
        private string? _outputDirectory;
        private string? _message;

        public QobuzDownloadJob(
            string downloadId,
            QobuzAlbumReference albumReference,
            QobuzCredentials credentials,
            AlbumDownloadRequest request,
            RemoteAlbum remoteAlbum,
            string title,
            string artist,
            bool isExplicit,
            long estimatedTotalBytes,
            int totalTracks)
        {
            DownloadId = downloadId;
            AlbumReference = albumReference;
            Credentials = credentials;
            Request = request;
            RemoteAlbum = remoteAlbum;
            Title = title;
            Artist = artist;
            IsExplicit = isExplicit;
            EstimatedTotalBytes = estimatedTotalBytes;
            TotalTracks = totalTracks;
            _status = DownloadItemStatus.Queued;
        }

        public string DownloadId { get; }

        public QobuzAlbumReference AlbumReference { get; }

        public QobuzCredentials Credentials { get; }

        public AlbumDownloadRequest Request { get; }

        public RemoteAlbum RemoteAlbum { get; }

        public string Title { get; }

        public string Artist { get; }

        public bool IsExplicit { get; }

        public long EstimatedTotalBytes { get; private set; }

        public int TotalTracks { get; private set; }

        public DownloadItemStatus Status
        {
            get
            {
                lock (_stateLock)
                {
                    return _status;
                }
            }
        }

        /// <summary>
        /// Replaces the enqueue-time estimates once the real album plan has been loaded.
        /// </summary>
        public void RefineTotals(int totalTracks, long estimatedTotalBytes)
        {
            lock (_stateLock)
            {
                TotalTracks = totalTracks;

                if (estimatedTotalBytes > 0)
                {
                    EstimatedTotalBytes = estimatedTotalBytes;
                }
            }
        }

        public void MarkDownloading()
        {
            lock (_stateLock)
            {
                _status = DownloadItemStatus.Downloading;
            }
        }

        public void MarkCompleted()
        {
            lock (_stateLock)
            {
                _status = DownloadItemStatus.Completed;
            }
        }

        public void MarkFailed(string message)
        {
            lock (_stateLock)
            {
                _status = DownloadItemStatus.Failed;
                _message = message;
            }
        }

        public void MarkWarning(string message)
        {
            lock (_stateLock)
            {
                _status = DownloadItemStatus.Warning;
                _message = message;
            }
        }

        public void OnOutputDirectoryResolved(string outputDirectory)
        {
            // Assigned once, before any track starts, instead of being raced by every
            // concurrent track task.
            lock (_stateLock)
            {
                _outputDirectory = outputDirectory;
            }
        }

        public void OnBytesTransferred(long additionalBytes)
        {
            if (additionalBytes > 0)
            {
                Interlocked.Add(ref _downloadedBytes, additionalBytes);
            }
        }

        public void OnTrackCompleted(QobuzTrackMetadata track)
        {
            Interlocked.Increment(ref _completedTracks);
        }

        public void OnTrackFailed(QobuzTrackMetadata track, Exception exception)
        {
            Interlocked.Increment(ref _failedTracks);
        }

        public DownloadProgressSnapshot Snapshot()
        {
            lock (_stateLock)
            {
                return new DownloadProgressSnapshot(
                    DownloadId,
                    Title,
                    Artist,
                    IsExplicit,
                    Request.Quality,
                    _status,
                    EstimatedTotalBytes,
                    Interlocked.Read(ref _downloadedBytes),
                    Volatile.Read(ref _completedTracks),
                    Volatile.Read(ref _failedTracks),
                    TotalTracks,
                    _outputDirectory,
                    _message);
            }
        }
    }
}
