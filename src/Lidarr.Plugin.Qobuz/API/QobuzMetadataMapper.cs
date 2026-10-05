using System;
using System.Collections.Generic;
using System.Linq;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Plugin.Qobuz.API
{
    /// <summary>
    /// Converts QobuzApiSharp's models into the plugin's own, and is the only code allowed to
    /// touch them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library is compiled without nullable reference types, so every reference-typed
    /// property can be null regardless of how it is declared. Worse, <c>Album.CompleteTitle</c>
    /// and <c>Track.CompleteTitle</c> are computed getters that call <c>Title.Trim()</c>
    /// internally and therefore throw <see cref="NullReferenceException"/> whenever Qobuz omits
    /// a title. The plugin previously read those properties directly in five places.
    /// </para>
    /// <para>
    /// Containing all of that here means a catalogue gap degrades one field instead of aborting
    /// a download with an unexplained NRE.
    /// </para>
    /// </remarks>
    internal static class QobuzMetadataMapper
    {
        private const string UnknownTitle = "Unknown Album";
        private const string UnknownTrackTitle = "Unknown Track";

        internal static QobuzAlbumMetadata ToAlbumMetadata(Album album)
        {
            if (album == null)
            {
                throw new ArgumentNullException(nameof(album));
            }

            var artists = (album.Artists ?? new List<Artist>())
                .Where(a => !string.IsNullOrWhiteSpace(a?.Name))
                .Select(a => new QobuzArtist(a.Name!.Trim()))
                .ToList();

            var albumArtist = SafeName(album.Artist?.Name);

            if (string.IsNullOrEmpty(albumArtist) && artists.Count > 0)
            {
                albumArtist = artists[0].Name;
            }

            var trackCount = album.TracksCount.GetValueOrDefault();

            if (trackCount <= 0)
            {
                trackCount = album.Tracks?.Items?.Count ?? 0;
            }

            return new QobuzAlbumMetadata(
                id: album.Id ?? string.Empty,
                title: SafeCompleteTitle(album),
                albumArtist: albumArtist,
                artists: artists,
                trackCount: trackCount,
                mediaCount: Math.Max(album.MediaCount.GetValueOrDefault(), 1),
                durationSeconds: Math.Max(album.Duration.GetValueOrDefault(), 0),
                year: YearOf(album.ReleaseDateOriginal),
                genre: string.IsNullOrWhiteSpace(album.Genre?.Name) ? null : album.Genre!.Name!.Trim(),
                isExplicit: album.ParentalWarning.GetValueOrDefault(),
                coverArtUrl: string.IsNullOrWhiteSpace(album.Image?.Large) ? null : album.Image!.Large);
        }

        internal static IReadOnlyList<QobuzTrackMetadata> ToTrackMetadata(
            Album album,
            QobuzAlbumMetadata albumMetadata)
        {
            var items = album?.Tracks?.Items;

            if (items == null)
            {
                return Array.Empty<QobuzTrackMetadata>();
            }

            var tracks = new List<QobuzTrackMetadata>(items.Count);
            var ordinal = 0;

            foreach (var track in items)
            {
                ordinal++;

                if (track?.Id == null)
                {
                    // Without an id the track cannot be fetched or downloaded at all.
                    continue;
                }

                tracks.Add(ToTrackMetadata(track, albumMetadata, ordinal));
            }

            return tracks;
        }

        internal static QobuzTrackMetadata ToTrackMetadata(
            Track track,
            QobuzAlbumMetadata albumMetadata,
            int fallbackTrackNumber)
        {
            if (track == null)
            {
                throw new ArgumentNullException(nameof(track));
            }

            var performer = SafeName(track.Performer?.Name);

            if (string.IsNullOrEmpty(performer))
            {
                performer = albumMetadata?.AlbumArtist ?? string.Empty;
            }

            var trackNumber = track.TrackNumber.GetValueOrDefault();

            if (trackNumber <= 0)
            {
                trackNumber = fallbackTrackNumber;
            }

            return new QobuzTrackMetadata(
                id: track.Id!.Value.ToString(),
                title: SafeCompleteTitle(track, UnknownTrackTitle),
                performer: performer,
                trackNumber: trackNumber,
                mediaNumber: Math.Max(track.MediaNumber.GetValueOrDefault(), 1),
                durationSeconds: Math.Max(track.Duration.GetValueOrDefault(), 0),
                year: YearOf(track.ReleaseDateOriginal) ?? albumMetadata?.Year);
        }

        /// <summary>
        /// Reads <c>Album.CompleteTitle</c> without risking the NRE it throws on a null title.
        /// </summary>
        /// <remarks>
        /// Internal rather than private so the indexer parser shares this one implementation.
        /// Two copies of null-safe title handling would have to agree forever, which is the
        /// duplication this mapper exists to remove.
        /// </remarks>
        internal static string SafeCompleteTitle(Album album)
        {
            if (!string.IsNullOrWhiteSpace(album.Title))
            {
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
                    // The computed getter also dereferences Version/Work; fall through.
                }

                return album.Title!.Trim();
            }

            return UnknownTitle;
        }

        /// <summary>
        /// Reads <c>Track.CompleteTitle</c> without risking the NRE it throws on a null title.
        /// </summary>
        private static string SafeCompleteTitle(Track track, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(track.Title))
            {
                try
                {
                    var complete = track.CompleteTitle;

                    if (!string.IsNullOrWhiteSpace(complete))
                    {
                        return complete.Trim();
                    }
                }
                catch (NullReferenceException)
                {
                    // As above.
                }

                return track.Title!.Trim();
            }

            return fallback;
        }

        private static string SafeName(string? name) =>
            string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();

        private static int? YearOf(DateTimeOffset? releaseDate)
        {
            if (!releaseDate.HasValue)
            {
                return null;
            }

            var year = releaseDate.Value.Year;

            // Guard against placeholder dates such as 0001-01-01 that Qobuz occasionally returns.
            return year > 1 ? year : null;
        }
    }
}
