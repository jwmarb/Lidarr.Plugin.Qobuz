using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Plugin.Qobuz.API
{
    /// <summary>
    /// An immutable snapshot of the credentials needed to talk to Qobuz.
    /// </summary>
    /// <remarks>
    /// Credentials are snapshotted rather than read live because Lidarr registers providers as
    /// singletons and reassigns <c>Definition</c> on the shared instance per resolution, so a
    /// field read later in a call may belong to a different configured provider. Snapshot at
    /// the host entry point, then pass the snapshot down.
    /// </remarks>
    public sealed class QobuzCredentials : IEquatable<QobuzCredentials>
    {
        public QobuzCredentials(
            string? email,
            string? md5Password,
            string? userId,
            string? userAuthToken,
            string? appId,
            string? appSecret)
        {
            Email = email ?? string.Empty;
            Md5Password = md5Password ?? string.Empty;
            UserId = userId ?? string.Empty;
            UserAuthToken = userAuthToken ?? string.Empty;
            AppId = appId ?? string.Empty;
            AppSecret = appSecret ?? string.Empty;
        }

        public string Email { get; }

        public string Md5Password { get; }

        public string UserId { get; }

        public string UserAuthToken { get; }

        public string AppId { get; }

        public string AppSecret { get; }

        /// <summary>True when an email and MD5 password are both present.</summary>
        public bool HasEmailLogin =>
            !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Md5Password);

        /// <summary>True when a user id and auth token are both present.</summary>
        public bool HasTokenLogin =>
            !string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(UserAuthToken);

        /// <summary>
        /// True when at least one complete authentication method is present. Email login takes
        /// precedence when both are supplied.
        /// </summary>
        public bool IsComplete => HasEmailLogin || HasTokenLogin;

        public bool Equals(QobuzCredentials? other)
        {
            if (other is null)
            {
                return false;
            }

            return string.Equals(Email, other.Email, StringComparison.Ordinal)
                && string.Equals(Md5Password, other.Md5Password, StringComparison.Ordinal)
                && string.Equals(UserId, other.UserId, StringComparison.Ordinal)
                && string.Equals(UserAuthToken, other.UserAuthToken, StringComparison.Ordinal)
                && string.Equals(AppId, other.AppId, StringComparison.Ordinal)
                && string.Equals(AppSecret, other.AppSecret, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as QobuzCredentials);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Email, StringComparer.Ordinal);
            hash.Add(Md5Password, StringComparer.Ordinal);
            hash.Add(UserId, StringComparer.Ordinal);
            hash.Add(UserAuthToken, StringComparer.Ordinal);
            hash.Add(AppId, StringComparer.Ordinal);
            hash.Add(AppSecret, StringComparer.Ordinal);
            return hash.ToHashCode();
        }

        /// <summary>
        /// Deliberately omits every secret, so that logging a credential snapshot cannot leak
        /// a password hash or auth token.
        /// </summary>
        public override string ToString()
        {
            if (HasEmailLogin)
            {
                return "QobuzCredentials(email login)";
            }

            return HasTokenLogin
                ? "QobuzCredentials(token login)"
                : "QobuzCredentials(incomplete)";
        }
    }

    /// <summary>One artist credited on an album or track.</summary>
    public sealed class QobuzArtist
    {
        public QobuzArtist(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    /// <summary>
    /// A track, normalised into plugin-owned data.
    /// </summary>
    /// <remarks>
    /// The Qobuz library's own models carry no nullable annotations and expose computed
    /// properties that dereference a possibly-null title, so they throw
    /// <see cref="NullReferenceException"/> on perfectly ordinary catalogue gaps. Normalising
    /// at the adapter means callers never touch those types, and the shipped assembly does not
    /// depend on them across a seam (ILRepack internalises the merged library).
    /// </remarks>
    public sealed class QobuzTrackMetadata
    {
        public QobuzTrackMetadata(
            string id,
            string title,
            string performer,
            int trackNumber,
            int mediaNumber,
            long durationSeconds,
            int? year)
        {
            Id = id;
            Title = title;
            Performer = performer;
            TrackNumber = trackNumber;
            MediaNumber = mediaNumber;
            DurationSeconds = durationSeconds;
            Year = year;
        }

        public string Id { get; }

        /// <summary>Never null or empty; the adapter substitutes a placeholder if needed.</summary>
        public string Title { get; }

        /// <summary>Never null; empty when Qobuz credits no performer.</summary>
        public string Performer { get; }

        public int TrackNumber { get; }

        /// <summary>Disc number. At least 1.</summary>
        public int MediaNumber { get; }

        public long DurationSeconds { get; }

        public int? Year { get; }
    }

    /// <summary>An album, normalised into plugin-owned data.</summary>
    public sealed class QobuzAlbumMetadata
    {
        public QobuzAlbumMetadata(
            string id,
            string title,
            string albumArtist,
            IReadOnlyList<QobuzArtist> artists,
            int trackCount,
            int mediaCount,
            long durationSeconds,
            int? year,
            string? genre,
            bool isExplicit,
            string? coverArtUrl)
        {
            Id = id;
            Title = title;
            AlbumArtist = albumArtist;
            Artists = artists;
            TrackCount = trackCount;
            MediaCount = mediaCount;
            DurationSeconds = durationSeconds;
            Year = year;
            Genre = genre;
            IsExplicit = isExplicit;
            CoverArtUrl = coverArtUrl;
        }

        public string Id { get; }

        /// <summary>Never null or empty.</summary>
        public string Title { get; }

        /// <summary>Never null; empty when Qobuz credits no artist.</summary>
        public string AlbumArtist { get; }

        public IReadOnlyList<QobuzArtist> Artists { get; }

        public int TrackCount { get; }

        /// <summary>Disc count. At least 1.</summary>
        public int MediaCount { get; }

        public long DurationSeconds { get; }

        public int? Year { get; }

        public string? Genre { get; }

        public bool IsExplicit { get; }

        public string? CoverArtUrl { get; }
    }

    /// <summary>
    /// Everything needed to download one album: its metadata and the ordered tracks it
    /// contains.
    /// </summary>
    /// <remarks>
    /// Returning a plan in one call is what removes the per-track album refetch. The previous
    /// code fetched the album once to list tracks, then refetched the same album and track
    /// inside the tagging path for every single track.
    /// </remarks>
    public sealed class QobuzAlbumDownloadPlan
    {
        public QobuzAlbumDownloadPlan(
            QobuzAlbumMetadata album,
            IReadOnlyList<QobuzTrackMetadata> tracks)
        {
            Album = album ?? throw new ArgumentNullException(nameof(album));
            Tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));

            // An album with no tracks is not downloadable, and callers index Tracks[0] to
            // resolve the output directory. Enforcing the invariant here means a malformed
            // plan fails at construction with an explanation, rather than throwing
            // IndexOutOfRangeException deep inside the download.
            if (tracks.Count == 0)
            {
                throw new ArgumentException(
                    "An album download plan must contain at least one track.",
                    nameof(tracks));
            }
        }

        public QobuzAlbumMetadata Album { get; }

        /// <summary>The album's tracks, in order. Never empty.</summary>
        public IReadOnlyList<QobuzTrackMetadata> Tracks { get; }
    }

    /// <summary>
    /// An authenticated conversation with Qobuz, scoped to one set of credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The metadata members are deliberately <b>synchronous</b>. The underlying library is
    /// synchronous and blocking, and wrapping each call in <c>Task.Run</c> would advertise
    /// asynchrony this module does not have. Only <see cref="DownloadTrackAsync"/> is
    /// asynchronous, because it performs real streaming HTTP and file I/O and honours
    /// cancellation. Keeping blocking work off host threads is the scheduler's job, decided
    /// once, rather than a fiction repeated on every method.
    /// </para>
    /// <para>
    /// Implementations must be safe to call from multiple threads.
    /// </para>
    /// </remarks>
    public interface IQobuzSession : IDisposable
    {
        /// <summary>The credentials this session authenticated with.</summary>
        QobuzCredentials Credentials { get; }

        /// <summary>The Qobuz app id in use, needed when signing indexer search requests.</summary>
        string AppId { get; }

        /// <summary>The auth token obtained at login, needed when signing search requests.</summary>
        string AuthToken { get; }

        /// <summary>
        /// Builds a signed URL for a Qobuz API method, for handing to Lidarr's own HTTP
        /// pipeline.
        /// </summary>
        /// <remarks>
        /// The library performs its own HTTP and cannot hand back a request, but Lidarr's
        /// indexer pipeline requires a URL plus headers that it executes itself. So search
        /// cannot simply delegate to the library's search method.
        /// </remarks>
        string BuildApiUrl(string method, IReadOnlyDictionary<string, string>? parameters = null);

        /// <summary>
        /// Loads an album and its tracks in a single call.
        /// </summary>
        /// <exception cref="QobuzApiException">Qobuz rejected the request.</exception>
        QobuzAlbumDownloadPlan GetAlbumDownloadPlan(string albumId);

        /// <summary>
        /// Downloads one track's media to <paramref name="destinationPath"/>.
        /// </summary>
        /// <param name="progress">
        /// Invoked with the number of bytes written so far, so callers can report real byte
        /// progress rather than a track count.
        /// </param>
        /// <returns>The number of bytes written.</returns>
        /// <exception cref="QobuzQualityUnavailableException">
        /// Qobuz served a preview sample instead of the requested quality, which means the
        /// account lacks access to it.
        /// </exception>
        Task<long> DownloadTrackAsync(
            string trackId,
            AudioQuality quality,
            string destinationPath,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches album cover art, or <see langword="null"/> when unavailable.
        /// </summary>
        /// <remarks>Never throws for an absent or unauthorised cover; returns null.</remarks>
        Task<byte[]?> TryGetCoverArtAsync(
            QobuzAlbumMetadata album,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Creates authenticated sessions, reusing one per distinct credential set.
    /// </summary>
    /// <remarks>
    /// This replaces a mutable public static singleton that only the indexer ever initialised
    /// while the download path only ever read it, so a grab that reached the download client
    /// before any search had run dereferenced null.
    /// </remarks>
    public interface IQobuzSessionProvider
    {
        /// <summary>
        /// Returns a session authenticated with <paramref name="credentials"/>, creating and
        /// logging in one if necessary.
        /// </summary>
        /// <exception cref="QobuzAuthenticationException">
        /// The credentials are incomplete or Qobuz rejected them.
        /// </exception>
        IQobuzSession GetSession(QobuzCredentials credentials);

        /// <summary>
        /// Returns the existing session for these credentials without attempting a login.
        /// </summary>
        bool TryGetExistingSession(QobuzCredentials credentials, out IQobuzSession? session);
    }

    /// <summary>Base type for Qobuz failures the plugin raises deliberately.</summary>
    public class QobuzApiException : Exception
    {
        public QobuzApiException(string message)
            : base(message)
        {
        }

        public QobuzApiException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>Login failed, or credentials were not usable.</summary>
    public sealed class QobuzAuthenticationException : QobuzApiException
    {
        public QobuzAuthenticationException(string message)
            : base(message)
        {
        }

        public QobuzAuthenticationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// The requested quality is not available to this account; Qobuz served a sample.
    /// </summary>
    public sealed class QobuzQualityUnavailableException : QobuzApiException
    {
        public QobuzQualityUnavailableException(string message)
            : base(message)
        {
        }
    }
}
