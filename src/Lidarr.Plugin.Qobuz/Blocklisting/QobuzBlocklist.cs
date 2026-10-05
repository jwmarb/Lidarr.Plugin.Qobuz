using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Blocklisting
{
    public class QobuzBlocklist : IBlocklistForProtocol
    {
        private readonly IBlocklistRepository _blocklistRepository;

        public QobuzBlocklist(IBlocklistRepository blocklistRepository)
        {
            _blocklistRepository = blocklistRepository;
        }

        public string Protocol => nameof(QobuzDownloadProtocol);

        public bool IsBlocklisted(int artistId, ReleaseInfo release)
        {
            var blocklistedByGuid = _blocklistRepository.BlocklistedByTorrentInfoHash(artistId, release.Guid);

            return blocklistedByGuid.Any(item => SameRelease(item, release));
        }

        /// <summary>
        /// Builds a blocklist entry from a failed download.
        /// </summary>
        /// <remarks>
        /// Every field is read defensively. The previous implementation called
        /// <c>DateTime.Parse</c> directly on <c>Data["publishedDate"]</c>, which throws
        /// <see cref="ArgumentNullException"/> when the key is absent and
        /// <see cref="FormatException"/> when it is not a date — while every neighbouring field
        /// already supplied a default. A failed download would then fail again while being
        /// recorded as failed.
        /// </remarks>
        public Blocklist GetBlocklist(DownloadFailedEvent message)
        {
            return new Blocklist
            {
                ArtistId = message.ArtistId,
                AlbumIds = message.AlbumIds,
                SourceTitle = message.SourceTitle,
                Quality = message.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = ParseDate(message.Data),
                Size = ParseSize(message.Data),
                Indexer = GetValue(message.Data, "indexer"),
                Protocol = GetValue(message.Data, "protocol"),
                Message = message.Message,
                TorrentInfoHash = GetValue(message.Data, "guid")
            };
        }

        private static string GetValue(IDictionary<string, string>? data, string key)
        {
            if (data == null)
            {
                return string.Empty;
            }

            return data.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        }

        private static DateTime ParseDate(IDictionary<string, string>? data)
        {
            var raw = GetValue(data, "publishedDate");

            return DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : DateTime.UtcNow;
        }

        private static long ParseSize(IDictionary<string, string>? data)
        {
            var raw = GetValue(data, "size");

            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0L;
        }

        /// <summary>
        /// Decides whether a blocklist entry refers to the same release.
        /// </summary>
        /// <remarks>
        /// Both sides are null-guarded. The previous version dereferenced
        /// <c>item.Indexer</c> whenever the release had no guid, which throws for any entry
        /// recorded without an indexer name.
        /// </remarks>
        private static bool SameRelease(Blocklist item, ReleaseInfo release)
        {
            if (release.Guid.IsNotNullOrWhiteSpace())
            {
                return release.Guid.Equals(item.TorrentInfoHash, StringComparison.Ordinal);
            }

            if (item.Indexer.IsNullOrWhiteSpace() || release.Indexer.IsNullOrWhiteSpace())
            {
                return false;
            }

            return item.Indexer.Equals(release.Indexer, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
