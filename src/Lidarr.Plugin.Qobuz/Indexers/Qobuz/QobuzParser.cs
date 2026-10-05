using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Core.Indexers.Qobuz
{
    /// <summary>
    /// Turns a Qobuz album-search response into Lidarr releases, one per offered quality.
    /// </summary>
    public class QobuzParser : IParseIndexerResponse
    {
        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var content = new HttpResponse<SearchResult>(response.HttpResponse).Content;

            var searchResult = JObject.Parse(content).ToObject<SearchResult>();

            // Every one of these can be absent: the library's models carry no nullable
            // annotations, and an empty search simply omits the albums object.
            var albums = searchResult?.Albums?.Items;

            if (albums == null)
            {
                return Array.Empty<ReleaseInfo>();
            }

            return albums
                .Where(album => album != null)
                .SelectMany(ToReleases)
                .OrderByDescending(release => release.Size)
                .ToList();
        }

        /// <summary>
        /// Offers one release per quality this album can be streamed at.
        /// </summary>
        private static IEnumerable<ReleaseInfo> ToReleases(Album album)
        {
            var qualities = new List<AudioQualitySpec>(AudioQualities.StandardTiers);

            if ((album.Hires ?? false) && (album.HiresStreamable ?? false))
            {
                // Qobuz advertises 192 kHz whenever hi-res exists and quietly serves 96 kHz
                // when it does not, which cannot be detected without downloading.
                qualities.AddRange(AudioQualities.HiResTiers);
            }

            var metadata = QobuzReleaseMetadata.From(album);

            return qualities.Select(quality => ToReleaseInfo(metadata, quality));
        }

        private static ReleaseInfo ToReleaseInfo(QobuzReleaseMetadata album, AudioQualitySpec quality)
        {
            var title = $"{album.Artist} - {album.Title}";

            if (album.Year > 0)
            {
                title += $" ({album.Year})";
            }

            if (album.IsExplicit)
            {
                title += " [Explicit]";
            }

            title += $" [{quality.DisplayLabel}] [WEB]";

            return new ReleaseInfo
            {
                Guid = $"Qobuz-{album.Id}-{quality.Quality}",
                Artist = album.Artist,
                Album = album.Title,
                Title = title,
                DownloadUrl = album.Url,
                InfoUrl = album.Url,
                PublishDate = album.PublishDate,
                DownloadProtocol = nameof(QobuzDownloadProtocol),
                Codec = quality.Codec,

                // Round-trips back to a quality in the download client, so it must stay in
                // step with AudioQualities.TryFromContainer.
                Container = quality.Container,

                // Bytes. Qobuz exposes no cheap way to obtain a real size, so this is an
                // estimate from duration and bitrate; the division by 8 inside EstimateBytes
                // is what keeps it in bytes rather than bits.
                Size = quality.EstimateBytes(album.DurationSeconds),
            };
        }

        /// <summary>
        /// The fields a release needs, read defensively out of the library's model once.
        /// </summary>
        private sealed class QobuzReleaseMetadata
        {
            private QobuzReleaseMetadata(
                string id,
                string title,
                string artist,
                string url,
                DateTime publishDate,
                int year,
                long durationSeconds,
                bool isExplicit)
            {
                Id = id;
                Title = title;
                Artist = artist;
                Url = url;
                PublishDate = publishDate;
                Year = year;
                DurationSeconds = durationSeconds;
                IsExplicit = isExplicit;
            }

            internal string Id { get; }

            internal string Title { get; }

            internal string Artist { get; }

            internal string Url { get; }

            internal DateTime PublishDate { get; }

            internal int Year { get; }

            internal long DurationSeconds { get; }

            internal bool IsExplicit { get; }

            internal static QobuzReleaseMetadata From(Album album)
            {
                var publishDate = DateTime.UtcNow;
                var year = 0;

                if (album.ReleaseDateOriginal.HasValue)
                {
                    publishDate = album.ReleaseDateOriginal.Value.DateTime;
                    year = publishDate.Year;
                }

                return new QobuzReleaseMetadata(
                    id: album.Id ?? string.Empty,
                    title: SafeTitle(album),
                    artist: album.Artist?.Name?.Trim() ?? string.Empty,
                    url: album.Url ?? string.Empty,
                    publishDate: publishDate,
                    year: year,
                    durationSeconds: Math.Max(album.Duration.GetValueOrDefault(), 0),
                    isExplicit: album.ParentalWarning.GetValueOrDefault());
            }

            /// <summary>
            /// Reads the album title without tripping over <c>CompleteTitle</c>, which calls
            /// <c>Title.Trim()</c> internally and throws when Qobuz omits a title.
            /// </summary>
            private static string SafeTitle(Album album)
            {
                if (string.IsNullOrWhiteSpace(album.Title))
                {
                    return "Unknown Album";
                }

                try
                {
                    var complete = album.CompleteTitle;

                    if (!string.IsNullOrWhiteSpace(complete))
                    {
                        return complete.Trim();
                    }
                }
                catch (NullReferenceException)
                {
                    // Falls through to the plain title.
                }

                return album.Title!.Trim();
            }
        }
    }
}
