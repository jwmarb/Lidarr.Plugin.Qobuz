using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests.Fakes
{
    /// <summary>
    /// An in-memory <see cref="IQobuzSession"/>: the second adapter that justifies the seam.
    /// </summary>
    /// <remarks>
    /// Models observable contracts and failure modes, not implementation details, so tests can
    /// exercise authentication rejection, unavailable qualities, missing albums, cancellation
    /// and deterministic byte progress without network access or real credentials.
    /// </remarks>
    public sealed class FakeQobuzSession : IQobuzSession
    {
        private readonly Dictionary<string, QobuzAlbumDownloadPlan> _albums = new();
        private readonly HashSet<string> _unavailableTracks = new();
        private readonly HashSet<string> _failingTracks = new();

        public FakeQobuzSession(QobuzCredentials? credentials = null)
        {
            Credentials = credentials ?? new QobuzCredentials("a@b.c", "md5", null, null, null, null);
        }

        public QobuzCredentials Credentials { get; }

        public string AppId { get; set; } = "fake-app-id";

        public string AuthToken { get; set; } = "fake-auth-token";

        /// <summary>Bytes written per track, so progress is predictable.</summary>
        public int BytesPerTrack { get; set; } = 1024;

        /// <summary>Every track path this session wrote, in completion order.</summary>
        public ConcurrentBag<string> WrittenFiles { get; } = new();

        /// <summary>How many times an album plan was requested, to prove it is fetched once.</summary>
        public int AlbumPlanRequests { get; private set; }

        /// <summary>How many times cover art was requested, to prove it is fetched once.</summary>
        public int CoverArtRequests { get; private set; }

        public byte[]? CoverArt { get; set; }

        public bool IsDisposed { get; private set; }

        public void AddAlbum(QobuzAlbumDownloadPlan plan)
        {
            _albums[plan.Album.Id] = plan;
        }

        /// <summary>Marks a track as one Qobuz will answer with a preview sample.</summary>
        public void MakeTrackQualityUnavailable(string trackId) => _unavailableTracks.Add(trackId);

        /// <summary>Marks a track whose transfer fails outright.</summary>
        public void MakeTrackFail(string trackId) => _failingTracks.Add(trackId);

        public string BuildApiUrl(string method, IReadOnlyDictionary<string, string>? parameters = null)
        {
            var url = "https://www.qobuz.com/api.json/0.2" + method;

            if (parameters is { Count: > 0 })
            {
                var first = true;

                foreach (var pair in parameters)
                {
                    url += (first ? "?" : "&") + pair.Key + "=" + Uri.EscapeDataString(pair.Value);
                    first = false;
                }
            }

            return url;
        }

        public QobuzAlbumDownloadPlan GetAlbumDownloadPlan(string albumId)
        {
            AlbumPlanRequests++;

            if (!_albums.TryGetValue(albumId, out var plan))
            {
                throw new QobuzApiException($"Qobuz returned no album for id {albumId}.");
            }

            return plan;
        }

        public async Task<long> DownloadTrackAsync(
            string trackId,
            AudioQuality quality,
            string destinationPath,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_unavailableTracks.Contains(trackId))
            {
                throw new QobuzQualityUnavailableException(
                    $"Qobuz returned a preview sample for track {trackId}.");
            }

            if (_failingTracks.Contains(trackId))
            {
                throw new QobuzApiException($"Transfer failed for track {trackId}.");
            }

            var directory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Report in two halves so tests can observe incremental progress.
            var half = BytesPerTrack / 2;
            progress?.Report(half);
            progress?.Report(BytesPerTrack);

            await File.WriteAllBytesAsync(destinationPath, new byte[BytesPerTrack], cancellationToken)
                .ConfigureAwait(false);

            WrittenFiles.Add(destinationPath);

            return BytesPerTrack;
        }

        public Task<byte[]?> TryGetCoverArtAsync(
            QobuzAlbumMetadata album,
            CancellationToken cancellationToken = default)
        {
            CoverArtRequests++;
            return Task.FromResult(CoverArt);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    /// <summary>
    /// A session provider over <see cref="FakeQobuzSession"/> that can also reject credentials.
    /// </summary>
    public sealed class FakeQobuzSessionProvider : IQobuzSessionProvider
    {
        private readonly Dictionary<QobuzCredentials, IQobuzSession> _sessions = new();

        public bool RejectEverything { get; set; }

        public int SessionsCreated { get; private set; }

        public void Register(QobuzCredentials credentials, IQobuzSession session)
        {
            _sessions[credentials] = session;
        }

        public IQobuzSession GetSession(QobuzCredentials credentials)
        {
            if (RejectEverything)
            {
                throw new QobuzAuthenticationException("Qobuz rejected the supplied credentials.");
            }

            if (!credentials.IsComplete)
            {
                throw new QobuzAuthenticationException("Qobuz credentials are incomplete.");
            }

            if (!_sessions.TryGetValue(credentials, out var session))
            {
                session = new FakeQobuzSession(credentials);
                _sessions[credentials] = session;
                SessionsCreated++;
            }

            return session;
        }

        public bool TryGetExistingSession(QobuzCredentials credentials, out IQobuzSession? session)
        {
            if (_sessions.TryGetValue(credentials, out var found))
            {
                session = found;
                return true;
            }

            session = null;
            return false;
        }
    }

    /// <summary>Builds plugin-owned metadata for tests.</summary>
    public static class TestMetadata
    {
        public static QobuzAlbumMetadata Album(
            string id = "album-1",
            string title = "Test Album",
            string albumArtist = "Test Artist",
            int trackCount = 3,
            int mediaCount = 1,
            long durationSeconds = 600,
            int? year = 2001,
            string? genre = "Rock",
            bool isExplicit = false,
            string? coverArtUrl = "https://example.invalid/cover.jpg")
        {
            return new QobuzAlbumMetadata(
                id,
                title,
                albumArtist,
                new[] { new QobuzArtist(albumArtist) },
                trackCount,
                mediaCount,
                durationSeconds,
                year,
                genre,
                isExplicit,
                coverArtUrl);
        }

        public static QobuzTrackMetadata Track(
            string id = "track-1",
            string title = "Test Track",
            string performer = "Test Artist",
            int trackNumber = 1,
            int mediaNumber = 1,
            long durationSeconds = 200,
            int? year = 2001)
        {
            return new QobuzTrackMetadata(
                id,
                title,
                performer,
                trackNumber,
                mediaNumber,
                durationSeconds,
                year);
        }

        public static QobuzAlbumDownloadPlan Plan(int trackCount = 3, string albumId = "album-1")
        {
            var album = Album(id: albumId, trackCount: trackCount);

            var tracks = new List<QobuzTrackMetadata>();

            for (var i = 1; i <= trackCount; i++)
            {
                tracks.Add(Track(id: $"track-{i}", title: $"Track {i}", trackNumber: i));
            }

            return new QobuzAlbumDownloadPlan(album, tracks);
        }

        public static QobuzCredentials Credentials(string email = "a@b.c") =>
            new QobuzCredentials(email, "md5hash", null, null, null, null);
    }
}
