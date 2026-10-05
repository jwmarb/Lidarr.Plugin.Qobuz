using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    /// <summary>
    /// Lyrics for one track.
    /// </summary>
    public sealed class TrackLyrics
    {
        public TrackLyrics(string? plain, string? synced)
        {
            Plain = plain;
            Synced = synced;
        }

        public string? Plain { get; }

        public string? Synced { get; }

        public bool HasAny => !string.IsNullOrWhiteSpace(Plain) || !string.IsNullOrWhiteSpace(Synced);

        public static TrackLyrics None { get; } = new TrackLyrics(null, null);
    }

    /// <summary>
    /// Looks up lyrics, which Qobuz itself does not supply.
    /// </summary>
    public interface ILyricProvider
    {
        Task<TrackLyrics> GetLyricsAsync(
            QobuzAlbumMetadata album,
            QobuzTrackMetadata track,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Fetches lyrics from an LRCLIB instance.
    /// </summary>
    public sealed class LrcLibLyricProvider : ILyricProvider
    {
        private const string DefaultInstance = "lrclib.net";

        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly string _instance;

        public LrcLibLyricProvider(HttpClient httpClient, Logger logger, string? instance = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _instance = string.IsNullOrWhiteSpace(instance) ? DefaultInstance : instance;
        }

        public async Task<TrackLyrics> GetLyricsAsync(
            QobuzAlbumMetadata album,
            QobuzTrackMetadata track,
            CancellationToken cancellationToken = default)
        {
            var requestUrl =
                $"https://{_instance}/api/get"
                + $"?artist_name={Uri.EscapeDataString(track.Performer)}"
                + $"&track_name={Uri.EscapeDataString(track.Title)}"
                + $"&album_name={Uri.EscapeDataString(album.Title)}"
                + $"&duration={track.DurationSeconds}";

            try
            {
                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // A 404 simply means LRCLIB has nothing for this track.
                    return TrackLyrics.None;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                var json = JObject.Parse(content);

                return new TrackLyrics(
                    json["plainLyrics"]?.ToString(),
                    json["syncedLyrics"]?.ToString());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Lyrics are optional; never fail a download because a lyric server misbehaved.
                _logger.Debug(ex, "Could not fetch lyrics for {0}.", track.Title);
                return TrackLyrics.None;
            }
        }
    }

    /// <summary>
    /// Writes tags to an audio file that has already been downloaded.
    /// </summary>
    public interface ITrackTagger
    {
        void ApplyTags(
            string filePath,
            QobuzAlbumMetadata album,
            QobuzTrackMetadata track,
            byte[]? coverArt,
            string? lyrics);
    }

    /// <summary>
    /// Tags files with TagLib, from metadata the caller already holds.
    /// </summary>
    /// <remarks>
    /// Accepting metadata rather than fetching it is the point. The previous implementation was
    /// an extension method on the sealed Qobuz client that re-fetched the track, re-fetched its
    /// album, and re-downloaded the cover art on every single track, even though the caller had
    /// all three in hand.
    /// </remarks>
    public sealed class TagLibTrackTagger : ITrackTagger
    {
        private readonly Logger _logger;

        public TagLibTrackTagger(Logger logger)
        {
            _logger = logger;
        }

        public void ApplyTags(
            string filePath,
            QobuzAlbumMetadata album,
            QobuzTrackMetadata track,
            byte[]? coverArt,
            string? lyrics)
        {
            using var file = TagLib.File.Create(filePath);

            file.Tag.Title = track.Title;
            file.Tag.Album = album.Title;
            file.Tag.Performers = new[] { track.Performer };

            file.Tag.AlbumArtists = album.Artists.Count > 0
                ? album.Artists.Select(a => a.Name).ToArray()
                : new[] { album.AlbumArtist };

            if (track.Year.HasValue)
            {
                file.Tag.Year = (uint)track.Year.Value;
            }

            file.Tag.Track = (uint)Math.Max(track.TrackNumber, 0);
            file.Tag.TrackCount = (uint)Math.Max(album.TrackCount, 0);
            file.Tag.Disc = (uint)Math.Max(track.MediaNumber, 0);
            file.Tag.DiscCount = (uint)Math.Max(album.MediaCount, 0);

            if (!string.IsNullOrWhiteSpace(album.Genre))
            {
                file.Tag.Genres = new[] { album.Genre! };
            }

            if (coverArt is { Length: > 0 })
            {
                try
                {
                    file.Tag.Pictures = new TagLib.IPicture[]
                    {
                        new TagLib.Picture(new TagLib.ByteVector(coverArt))
                    };
                }
                catch (Exception ex)
                {
                    // Corrupt artwork must not cost us the rest of the tags.
                    _logger.Debug(ex, "Could not embed cover art for {0}.", track.Title);
                }
            }

            if (!string.IsNullOrWhiteSpace(lyrics))
            {
                file.Tag.Lyrics = lyrics;
            }

            file.Save();
        }
    }

    /// <summary>
    /// What a completed album download produced.
    /// </summary>
    public sealed class AlbumDownloadResult
    {
        public AlbumDownloadResult(
            string outputDirectory,
            int downloadedTracks,
            int failedTracks,
            long bytesWritten)
        {
            OutputDirectory = outputDirectory;
            DownloadedTracks = downloadedTracks;
            FailedTracks = failedTracks;
            BytesWritten = bytesWritten;
        }

        public string OutputDirectory { get; }

        public int DownloadedTracks { get; }

        public int FailedTracks { get; }

        public long BytesWritten { get; }

        public bool IsCompleteSuccess => FailedTracks == 0 && DownloadedTracks > 0;
    }

    /// <summary>
    /// Reports progress of an album download as it happens.
    /// </summary>
    public interface IAlbumDownloadObserver
    {
        /// <summary>Called once the output directory is known, before any track is written.</summary>
        void OnOutputDirectoryResolved(string outputDirectory);

        /// <summary>
        /// Called with the number of <em>additional</em> bytes written since the previous
        /// call, so that concurrent track transfers can be summed without coordination.
        /// </summary>
        void OnBytesTransferred(long additionalBytes);

        /// <summary>Called when a track finishes successfully.</summary>
        void OnTrackCompleted(QobuzTrackMetadata track);

        /// <summary>Called when a track fails permanently.</summary>
        void OnTrackFailed(QobuzTrackMetadata track, Exception exception);
    }

    /// <summary>
    /// Downloads one Qobuz album: resolves paths, transfers media, tags files and writes
    /// lyrics.
    /// </summary>
    /// <remarks>
    /// This is the module the download queue executes. It replaces logic that was spread across
    /// a static extension-method grab-bag, a queue item that reached into a global singleton,
    /// and a metadata helper, and it fetches the album and its cover art exactly once instead of
    /// once per track.
    /// </remarks>
    public sealed class QobuzAlbumDownloader
    {
        private readonly IQobuzSession _session;
        private readonly ITrackTagger _tagger;
        private readonly ILyricProvider _lyricProvider;
        private readonly Logger _logger;
        private readonly int _maxConcurrentTracks;

        public QobuzAlbumDownloader(
            IQobuzSession session,
            ITrackTagger tagger,
            ILyricProvider lyricProvider,
            Logger logger,
            int maxConcurrentTracks = 3)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _tagger = tagger ?? throw new ArgumentNullException(nameof(tagger));
            _lyricProvider = lyricProvider ?? throw new ArgumentNullException(nameof(lyricProvider));
            _logger = logger;
            _maxConcurrentTracks = Math.Max(1, maxConcurrentTracks);
        }

        public async Task<AlbumDownloadResult> DownloadAsync(
            QobuzAlbumDownloadPlan plan,
            AlbumDownloadRequest request,
            IAlbumDownloadObserver? observer = null,
            CancellationToken cancellationToken = default)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var album = plan.Album;
            var spec = AudioQualities.For(request.Quality);

            // Resolve the output directory once, from the album, rather than letting each
            // concurrent track race to assign it.
            var firstTrack = plan.Tracks[0];
            var directoryFields = new TrackPathFields(album, firstTrack, spec.FileExtension);
            var relativeDirectory = TrackPathTemplate.Render(
                TrackPathTemplate.AlbumDirectory,
                directoryFields);

            var outputDirectory = Path.GetFullPath(
                Path.Combine(request.DownloadPath, relativeDirectory));

            Directory.CreateDirectory(outputDirectory);
            observer?.OnOutputDirectoryResolved(outputDirectory);

            // Fetched once for the whole album instead of once per track.
            var coverArt = await _session.TryGetCoverArtAsync(album, cancellationToken)
                .ConfigureAwait(false);

            long totalBytes = 0;
            var completed = 0;
            var failed = 0;

            using var concurrency = new SemaphoreSlim(_maxConcurrentTracks, _maxConcurrentTracks);

            var tasks = plan.Tracks.Select(async track =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    var written = await DownloadTrackAsync(
                            album,
                            track,
                            spec,
                            request,
                            outputDirectory,
                            coverArt,
                            observer,
                            delta =>
                            {
                                // Deltas, not totals: several tracks transfer at once, so a
                                // per-track running total would overwrite its siblings.
                                Interlocked.Add(ref totalBytes, delta);
                                observer?.OnBytesTransferred(delta);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    Interlocked.Increment(ref completed);
                    observer?.OnTrackCompleted(track);
                    return written;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Counters are interlocked because up to _maxConcurrentTracks tasks update
                    // them concurrently; plain ++ loses updates.
                    Interlocked.Increment(ref failed);
                    _logger.Error(ex, "Failed to download Qobuz track {0} ({1}).", track.Id, track.Title);
                    observer?.OnTrackFailed(track, ex);
                    return 0L;
                }
                finally
                {
                    concurrency.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            return new AlbumDownloadResult(
                outputDirectory,
                Volatile.Read(ref completed),
                Volatile.Read(ref failed),
                Interlocked.Read(ref totalBytes));
        }

        private async Task<long> DownloadTrackAsync(
            QobuzAlbumMetadata album,
            QobuzTrackMetadata track,
            AudioQualitySpec spec,
            AlbumDownloadRequest request,
            string outputDirectory,
            byte[]? coverArt,
            IAlbumDownloadObserver? observer,
            Action<long> reportDelta,
            CancellationToken cancellationToken)
        {
            var fields = new TrackPathFields(album, track, spec.FileExtension);
            var fileName = TrackPathTemplate.Render(TrackPathTemplate.TrackFile, fields);
            var trackPath = Path.Combine(outputDirectory, fileName);

            long lastReported = 0;

            var progress = new Progress<long>(bytesSoFar =>
            {
                var delta = bytesSoFar - lastReported;

                if (delta > 0)
                {
                    lastReported = bytesSoFar;
                    reportDelta(delta);
                }
            });

            var written = await _session
                .DownloadTrackAsync(track.Id, spec.Quality, trackPath, progress, cancellationToken)
                .ConfigureAwait(false);

            var lyrics = request.UseLrcLib
                ? await _lyricProvider.GetLyricsAsync(album, track, cancellationToken).ConfigureAwait(false)
                : TrackLyrics.None;

            try
            {
                _tagger.ApplyTags(trackPath, album, track, coverArt, lyrics.Plain);
            }
            catch (Exception ex)
            {
                // The audio is already on disk and valid; tagging failure must not discard it.
                _logger.Warn(ex, "Downloaded {0} but could not write its tags.", track.Title);
            }

            if (request.SaveSyncedLyrics && !string.IsNullOrWhiteSpace(lyrics.Synced))
            {
                await WriteLrcFileAsync(outputDirectory, album, track, lyrics.Synced!, cancellationToken)
                    .ConfigureAwait(false);
            }

            return written;
        }

        private static async Task WriteLrcFileAsync(
            string outputDirectory,
            QobuzAlbumMetadata album,
            QobuzTrackMetadata track,
            string syncedLyrics,
            CancellationToken cancellationToken)
        {
            var lrcFields = new TrackPathFields(album, track, "lrc");
            var lrcName = TrackPathTemplate.Render(TrackPathTemplate.TrackFile, lrcFields);

            await File.WriteAllTextAsync(
                    Path.Combine(outputDirectory, lrcName),
                    syncedLyrics,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The immutable settings one album download runs with.
    /// </summary>
    /// <remarks>
    /// Snapshotted at the host entry point and passed down, rather than read from a provider
    /// whose <c>Definition</c> Lidarr reassigns on the shared singleton between calls.
    /// </remarks>
    public sealed class AlbumDownloadRequest
    {
        public AlbumDownloadRequest(
            string downloadPath,
            AudioQuality quality,
            bool saveSyncedLyrics,
            bool useLrcLib)
        {
            if (string.IsNullOrWhiteSpace(downloadPath))
            {
                throw new ArgumentException("A download path is required.", nameof(downloadPath));
            }

            DownloadPath = downloadPath;
            Quality = quality;
            SaveSyncedLyrics = saveSyncedLyrics;
            UseLrcLib = useLrcLib;
        }

        public string DownloadPath { get; }

        public AudioQuality Quality { get; }

        public bool SaveSyncedLyrics { get; }

        public bool UseLrcLib { get; }
    }
}
