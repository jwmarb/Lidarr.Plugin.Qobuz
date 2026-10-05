using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using QobuzApiSharp.Models.Content;
using QobuzApiSharp.Service;

namespace NzbDrone.Plugin.Qobuz.API
{
    /// <summary>
    /// The production <see cref="IQobuzSession"/>: the only place in the plugin that touches
    /// <see cref="QobuzApiService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library client is <c>sealed</c> with no interface and no virtual members, so it
    /// cannot be mocked or subclassed. Containing it behind this adapter is what makes the rest
    /// of the plugin testable at all.
    /// </para>
    /// <para>
    /// The client also exposes a mutable <c>UserAuthToken</c> and contains no synchronization
    /// whatsoever. A session therefore owns exactly one client, logs in once, and serializes
    /// the library's blocking metadata calls behind <see cref="_apiLock"/>. Media transfer uses
    /// this plugin's own <see cref="HttpClient"/> and so runs concurrently, bounded by the
    /// download scheduler instead.
    /// </para>
    /// </remarks>
    public sealed class QobuzSession : IQobuzSession
    {
        private const string ApiBaseUrl = "https://www.qobuz.com/api.json/0.2";

        private readonly QobuzApiService _client;
        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly object _apiLock = new();

        private readonly string _authToken;
        private bool _disposed;

        internal QobuzSession(
            QobuzApiService client,
            HttpClient httpClient,
            QobuzCredentials credentials,
            string authToken,
            Logger logger)
        {
            _client = client;
            _httpClient = httpClient;
            _logger = logger;
            _authToken = authToken ?? string.Empty;
            Credentials = credentials;
        }

        public QobuzCredentials Credentials { get; }

        public string AppId => _client.AppId ?? string.Empty;

        /// <summary>
        /// The auth token obtained at login.
        /// </summary>
        /// <remarks>
        /// Immutable for the life of the session. A session owns exactly one API client and
        /// logs in once; re-authenticating creates a new session rather than mutating this one,
        /// which is what keeps the library's unsynchronized <c>UserAuthToken</c> from being
        /// written while in-flight requests are reading it.
        /// </remarks>
        public string AuthToken => _authToken;

        public string BuildApiUrl(string method, IReadOnlyDictionary<string, string>? parameters = null)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("An API method is required.", nameof(method));
            }

            var builder = new StringBuilder(ApiBaseUrl);
            builder.Append(method);

            if (parameters is { Count: > 0 })
            {
                var first = true;

                foreach (var pair in parameters)
                {
                    builder.Append(first ? '?' : '&');
                    builder.Append(WebUtility.UrlEncode(pair.Key));
                    builder.Append('=');
                    builder.Append(WebUtility.UrlEncode(pair.Value));
                    first = false;
                }
            }

            return builder.ToString();
        }

        public QobuzAlbumDownloadPlan GetAlbumDownloadPlan(string albumId)
        {
            if (string.IsNullOrWhiteSpace(albumId))
            {
                throw new ArgumentException("An album id is required.", nameof(albumId));
            }

            Album album;

            try
            {
                // Serialized: the library is blocking and holds unsynchronized mutable state.
                lock (_apiLock)
                {
                    album = _client.GetAlbum(albumId, withAuth: true);
                }
            }
            catch (Exception ex) when (ex is not QobuzApiException)
            {
                throw new QobuzApiException($"Failed to load Qobuz album {albumId}.", ex);
            }

            if (album == null)
            {
                throw new QobuzApiException($"Qobuz returned no album for id {albumId}.");
            }

            var metadata = QobuzMetadataMapper.ToAlbumMetadata(album);
            var tracks = QobuzMetadataMapper.ToTrackMetadata(album, metadata);

            if (tracks.Count == 0)
            {
                throw new QobuzApiException(
                    $"Qobuz album {albumId} ({metadata.Title}) reported no downloadable tracks.");
            }

            return new QobuzAlbumDownloadPlan(metadata, tracks);
        }

        public async Task<long> DownloadTrackAsync(
            string trackId,
            AudioQuality quality,
            string destinationPath,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                throw new ArgumentException("A track id is required.", nameof(trackId));
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("A destination path is required.", nameof(destinationPath));
            }

            var spec = AudioQualities.For(quality);
            var mediaUrl = ResolveTrackMediaUrl(trackId, spec);

            cancellationToken.ThrowIfCancellationRequested();

            // Download to a temporary file and move it into place only on success, so an
            // interrupted transfer can never leave a truncated file that looks complete.
            var directory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Unique per attempt, not a fixed "<file>.part". Lidarr assigns a fresh download
            // id to every grab, so the same album can legitimately be in flight twice at once
            // (a retry overlapping a cancelled transfer that is still draining), and both
            // attempts resolve to the same destination path. With a shared temporary name the
            // second attempt hits FileShare.None and fails, turning a harmless duplicate into
            // a reported download failure.
            var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.part";

            try
            {
                var written = await CopyToFileAsync(mediaUrl, temporaryPath, progress, cancellationToken)
                    .ConfigureAwait(false);

                File.Move(temporaryPath, destinationPath, overwrite: true);
                return written;
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryPath);
                throw;
            }
        }

        public async Task<byte[]?> TryGetCoverArtAsync(
            QobuzAlbumMetadata album,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(album?.CoverArtUrl))
            {
                return null;
            }

            try
            {
                using var response = await _httpClient
                    .GetAsync(album!.CoverArtUrl, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Debug(
                        "Qobuz cover art for album {0} unavailable (HTTP {1}).",
                        album.Id,
                        (int)response.StatusCode);

                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Artwork is optional; never fail an otherwise good album for it.
                _logger.Debug(ex, "Could not fetch Qobuz cover art for album {0}.", album.Id);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Ignoring error while disposing the Qobuz API client.");
            }

            _httpClient.Dispose();
        }

        private string ResolveTrackMediaUrl(string trackId, AudioQualitySpec spec)
        {
            FileUrl? fileUrl;

            try
            {
                lock (_apiLock)
                {
                    fileUrl = _client.GetTrackFileUrl(trackId, spec.FormatId);
                }
            }
            catch (Exception ex) when (ex is not QobuzApiException)
            {
                throw new QobuzApiException(
                    $"Failed to resolve a Qobuz media URL for track {trackId} at {spec.DisplayLabel}.",
                    ex);
            }

            if (fileUrl == null)
            {
                throw new QobuzApiException(
                    $"Track {trackId} has no available media source at {spec.DisplayLabel}.");
            }

            // Qobuz answers with a 30-second preview when the account cannot stream the
            // requested quality, rather than returning an error.
            if (fileUrl.Sample ?? false)
            {
                throw new QobuzQualityUnavailableException(
                    $"Qobuz returned a preview sample for track {trackId}, so this account "
                    + $"cannot stream {spec.DisplayLabel}.");
            }

            if (string.IsNullOrWhiteSpace(fileUrl.Url))
            {
                // A populated response with no URL carries the real reason in Code/Message.
                throw new QobuzApiException(
                    $"Qobuz returned no media URL for track {trackId} at {spec.DisplayLabel}"
                    + $" (code: {fileUrl.Code ?? "none"}, message: {fileUrl.Message ?? "none"}).");
            }

            return fileUrl.Url;
        }

        private async Task<long> CopyToFileAsync(
            string mediaUrl,
            string temporaryPath,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            using var response = await _httpClient
                .GetAsync(mediaUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new QobuzApiException(
                    $"Qobuz media download failed with HTTP {(int)response.StatusCode}.");
            }

            // Captured before reading so a short transfer can be detected afterwards.
            var expectedBytes = response.Content.Headers.ContentLength;

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            long total = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                total += read;
                progress?.Report(total);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (total == 0)
            {
                throw new QobuzApiException("Qobuz media download produced an empty file.");
            }

            // Defence in depth, not a known bug. I probed this against a server that declares
            // a large Content-Length and then sends a short body with a clean FIN: .NET 8's
            // HttpClient already raises HttpIOException for that, and a connection reset
            // raises IOException, so the transport normally surfaces truncation on its own.
            // The comparison stays because the consequence of a miss is the worst outcome
            // available — a truncated FLAC renamed into place and imported as if complete,
            // corrupting the library silently until playback — and it costs one branch.
            if (expectedBytes.HasValue && total != expectedBytes.Value)
            {
                throw new QobuzApiException(
                    $"Qobuz media download was truncated: expected {expectedBytes.Value} bytes "
                    + $"but received {total}.");
            }

            return total;
        }

        private void TryDeleteTemporaryFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not remove partial Qobuz download at {0}.", path);
            }
        }
    }

    /// <summary>
    /// Creates and caches one <see cref="QobuzSession"/> per distinct credential set.
    /// </summary>
    /// <remarks>
    /// Registered through its interface, so Lidarr's container treats it as a singleton. That
    /// is intentional: sessions are expensive (each login is a blocking HTTP round trip) and
    /// are safely shared.
    /// </remarks>
    public sealed class QobuzSessionProvider : IQobuzSessionProvider, IDisposable
    {
        private readonly ConcurrentDictionary<QobuzCredentials, QobuzSession> _sessions = new();
        private readonly Logger _logger;
        private readonly object _loginLock = new();

        public QobuzSessionProvider(Logger logger)
        {
            _logger = logger;
        }

        public IQobuzSession GetSession(QobuzCredentials credentials)
        {
            if (credentials == null)
            {
                throw new ArgumentNullException(nameof(credentials));
            }

            if (!credentials.IsComplete)
            {
                throw new QobuzAuthenticationException(
                    "Qobuz credentials are incomplete. Supply either an email and MD5 password, "
                    + "or a user id and auth token.");
            }

            if (_sessions.TryGetValue(credentials, out var existing))
            {
                return existing;
            }

            // Logins are serialized so that a burst of concurrent searches cannot start
            // several logins for the same credentials at once.
            lock (_loginLock)
            {
                if (_sessions.TryGetValue(credentials, out existing))
                {
                    return existing;
                }

                var session = CreateAndAuthenticate(credentials);

                // Stop handing out sessions for credentials nobody is configured with any
                // more, so the cache cannot grow without bound as a user edits settings.
                EvictSupersededSessions();

                _sessions[credentials] = session;
                return session;
            }
        }

        /// <summary>
        /// Drops cached sessions beyond the most recent few so the cache stays bounded.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called under <see cref="_loginLock"/>. Several are kept rather than one, because a
        /// Lidarr instance may legitimately have more than one Qobuz indexer configured with
        /// different accounts.
        /// </para>
        /// <para>
        /// Evicted sessions are deliberately <b>not</b> disposed. A caller can hold a session
        /// across a multi-minute album download — <c>QobuzProxy.ExecuteAsync</c> does exactly
        /// that, awaiting a whole album between acquiring the session and finishing with it —
        /// and this provider has no way to know. Disposing here would close the
        /// <see cref="HttpClient"/> underneath a live transfer and fail a download that was
        /// progressing fine. Dropping the reference is enough: the garbage collector reclaims
        /// the session once the download that still holds it completes, and its pooled
        /// connections idle out on their own. Only <see cref="Dispose"/>, at process shutdown,
        /// disposes sessions eagerly.
        /// </para>
        /// </remarks>
        private void EvictSupersededSessions()
        {
            const int MaxCachedSessions = 4;

            if (_sessions.Count < MaxCachedSessions)
            {
                return;
            }

            foreach (var key in _sessions.Keys.ToList())
            {
                if (_sessions.Count < MaxCachedSessions)
                {
                    break;
                }

                if (_sessions.TryRemove(key, out _))
                {
                    _logger.Debug(
                        "Evicted a superseded Qobuz session from the cache; it will be "
                        + "reclaimed once any download still using it finishes.");
                }
            }
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }

        private QobuzSession CreateAndAuthenticate(QobuzCredentials credentials)
        {
            QobuzApiService client;

            try
            {
                var hasCustomApp = !string.IsNullOrWhiteSpace(credentials.AppId)
                    && !string.IsNullOrWhiteSpace(credentials.AppSecret);

                // The parameterless constructor scrapes app credentials from the Qobuz web
                // player, which is network I/O performed inside a constructor.
                client = hasCustomApp
                    ? new QobuzApiService(credentials.AppId, credentials.AppSecret)
                    : new QobuzApiService();
            }
            catch (Exception ex)
            {
                throw new QobuzAuthenticationException(
                    "Could not initialise the Qobuz API client. If this persists, set an App ID "
                    + "and App Secret explicitly in the indexer settings.",
                    ex);
            }

            try
            {
                var login = credentials.HasEmailLogin
                    ? client.LoginWithEmail(credentials.Email, credentials.Md5Password)
                    : client.LoginWithToken(credentials.UserId, credentials.UserAuthToken);

                var authToken = login?.AuthToken;

                if (string.IsNullOrWhiteSpace(authToken))
                {
                    throw new QobuzAuthenticationException(
                        "Qobuz accepted the login request but returned no auth token.");
                }

                _logger.Debug("Authenticated a Qobuz session ({0}).", credentials);

                return new QobuzSession(
                    client,
                    new HttpClient(),
                    credentials,
                    authToken!,
                    _logger);
            }
            catch (QobuzAuthenticationException)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                client.Dispose();

                throw new QobuzAuthenticationException(
                    "Qobuz rejected the supplied credentials. If you are using a user id and "
                    + "auth token, try setting a different App ID and App Secret.",
                    ex);
            }
        }
    }
}
