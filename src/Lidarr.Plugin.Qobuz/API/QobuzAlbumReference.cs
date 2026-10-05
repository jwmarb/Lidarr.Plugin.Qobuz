using System;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Qobuz.API
{
    /// <summary>
    /// A Qobuz album identified from a store or share URL.
    /// </summary>
    /// <remarks>
    /// Only albums are modelled. The previous parser recognised seven link words and produced
    /// one of six entity kinds, but every consumer rejected anything except an album — five of
    /// the six outcomes were parsed successfully and then thrown away deep inside the download
    /// queue. Narrowing the interface to what is actually supported moves that rejection to the
    /// point of parsing, where the caller can act on it.
    /// </remarks>
    public sealed class QobuzAlbumReference
    {
        /// <summary>
        /// Qobuz link words that denote an album. <c>interpreter</c> appears in store links and
        /// is an artist, not an album, so it is deliberately absent.
        /// </summary>
        private const string AlbumLinkType = "album";

        /// <summary>
        /// Matched in order; the first match that names an album wins.
        /// </summary>
        /// <remarks>
        /// Ordering matters. The "download-streaming-albums" form must be tried before the
        /// generic three-segment form, otherwise the generic pattern captures the wrong segment
        /// as the id.
        /// </remarks>
        private static readonly Regex[] UrlPatterns =
        {
            // https://open.qobuz.com/album/{id}
            new Regex(
                @"^https?://(?:[^/]*?\.)?qobuz\.com/(?<type>[^/]+?)/(?<id>[^/]+?)/?$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // https://www.qobuz.com/{locale}/album/{slug}/download-streaming-albums/{id}
            new Regex(
                @"^https?://(?:[^/]*?\.)?qobuz\.com/[^/]+?/(?<type>[^/]+?)/[^/]+?/download-streaming-albums/(?<id>[^/]+?)/?$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // https://www.qobuz.com/{locale}/album/{slug}/{id}
            new Regex(
                @"^https?://(?:[^/]*?\.)?qobuz\.com/[^/]+?/(?<type>[^/]+?)/[^/]+?/(?<id>[^/]+?)/?$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        private QobuzAlbumReference(string albumId)
        {
            AlbumId = albumId;
        }

        /// <summary>The Qobuz album id, as used by their API.</summary>
        public string AlbumId { get; }

        /// <summary>
        /// Attempts to read a Qobuz album id out of <paramref name="url"/>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the URL is a Qobuz album link. <see langword="false"/> for
        /// anything else, including well-formed Qobuz links to tracks, artists, labels,
        /// playlists and users.
        /// </returns>
        /// <remarks>
        /// Returns a flag rather than throwing. The previous implementation threw a bare
        /// <see cref="Exception"/> from its parse method and caught everything in a sibling
        /// <c>TryParse</c>, which used exceptions for ordinary control flow on every
        /// non-Qobuz release Lidarr offered it.
        /// </remarks>
        public static bool TryParse(string? url, out QobuzAlbumReference? reference)
        {
            reference = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var candidate = url.Trim();

            // Query strings and fragments carry tracking parameters, never the id.
            var queryStart = candidate.IndexOfAny(new[] { '?', '#' });
            if (queryStart != -1)
            {
                candidate = candidate[..queryStart];
            }

            foreach (var pattern in UrlPatterns)
            {
                var match = pattern.Match(candidate);

                if (!match.Success)
                {
                    continue;
                }

                if (!string.Equals(match.Groups["type"].Value, AlbumLinkType, StringComparison.OrdinalIgnoreCase))
                {
                    // A Qobuz URL for something we cannot download. Keep trying the remaining
                    // patterns, since a more specific one may still interpret it as an album.
                    continue;
                }

                var id = match.Groups["id"].Value.Trim('/');

                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                reference = new QobuzAlbumReference(id);
                return true;
            }

            return false;
        }

        public override string ToString() => $"qobuz:album:{AlbumId}";
    }
}
