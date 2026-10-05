using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    /// <summary>
    /// The values a path template can refer to, gathered once per track.
    /// </summary>
    /// <remarks>
    /// Replaces a private helper that took thirteen positional <see cref="string"/> parameters,
    /// where every call site had to supply them in exactly the right order and a transposition
    /// of two adjacent arguments produced wrong filenames rather than a compile error.
    /// </remarks>
    public sealed class TrackPathFields
    {
        public TrackPathFields(
            QobuzAlbumMetadata album,
            QobuzTrackMetadata track,
            string fileExtension)
        {
            if (album == null)
            {
                throw new ArgumentNullException(nameof(album));
            }

            if (track == null)
            {
                throw new ArgumentNullException(nameof(track));
            }

            Title = track.Title;
            Album = album.Title;
            AlbumArtist = album.AlbumArtist;
            Artist = string.IsNullOrEmpty(track.Performer) ? album.AlbumArtist : track.Performer;
            AlbumArtists = album.Artists.Count > 0
                ? album.Artists.Select(a => a.Name).ToList()
                : new List<string> { album.AlbumArtist };

            // Qobuz exposes no per-track multi-artist credit, only a single performer.
            Artists = new List<string> { Artist };

            TrackNumber = track.TrackNumber;
            TrackCount = album.TrackCount;
            Volume = track.MediaNumber;
            VolumeCount = album.MediaCount;
            Year = track.Year ?? album.Year;
            FileExtension = fileExtension;
        }

        public string Title { get; }

        public string Album { get; }

        public string AlbumArtist { get; }

        public string Artist { get; }

        public IReadOnlyList<string> AlbumArtists { get; }

        public IReadOnlyList<string> Artists { get; }

        public int TrackNumber { get; }

        public int TrackCount { get; }

        public int Volume { get; }

        public int VolumeCount { get; }

        public int? Year { get; }

        public string FileExtension { get; }
    }

    /// <summary>
    /// Renders download paths from a template.
    /// </summary>
    /// <remarks>
    /// The templates themselves remain fixed (see <see cref="AlbumDirectory"/> and
    /// <see cref="TrackFile"/>) rather than becoming user settings. Making them configurable
    /// would change where files land and therefore what Lidarr imports, which is a product
    /// decision with migration consequences, not part of this refactor.
    /// </remarks>
    public static class TrackPathTemplate
    {
        /// <summary>The album folder layout, relative to the configured download path.</summary>
        public const string AlbumDirectory = "%albumartist%/%album%/";

        /// <summary>The track filename layout, within the album folder.</summary>
        public const string TrackFile = "%volume% - %track% - %title%.%ext%";

        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// Substitutes every placeholder in <paramref name="template"/>, sanitising each
        /// substituted value so it is safe to use as a single path segment.
        /// </summary>
        /// <remarks>
        /// Only substituted <em>values</em> are sanitised; separators written literally in the
        /// template survive, which is what lets a template describe nested folders.
        /// </remarks>
        public static string Render(string template, TrackPathFields fields)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }

            if (fields == null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            var builder = new StringBuilder(template);

            Replace("%title%", fields.Title);
            Replace("%album%", fields.Album);
            Replace("%albumartist%", fields.AlbumArtist);
            Replace("%artist%", fields.Artist);
            Replace("%albumartists%", string.Join("; ", fields.AlbumArtists));
            Replace("%artists%", string.Join("; ", fields.Artists));
            Replace("%track%", fields.TrackNumber.ToString("00", CultureInfo.InvariantCulture));
            Replace("%trackcount%", fields.TrackCount.ToString(CultureInfo.InvariantCulture));
            Replace("%volume%", fields.Volume.ToString("00", CultureInfo.InvariantCulture));
            Replace("%volumecount%", fields.VolumeCount.ToString(CultureInfo.InvariantCulture));
            Replace("%year%", fields.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Replace("%ext%", fields.FileExtension);

            return builder.ToString();

            void Replace(string placeholder, string value)
            {
                builder.Replace(placeholder, Sanitise(value));
            }
        }

        /// <summary>
        /// Replaces characters that cannot appear in a filename, and trims trailing dots and
        /// spaces, which Windows silently strips and then fails to find.
        /// </summary>
        public static string Sanitise(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);

            foreach (var c in value)
            {
                builder.Append(Array.IndexOf(InvalidFileNameChars, c) >= 0 ? '_' : c);
            }

            var sanitised = builder.ToString().TrimEnd('.', ' ');

            // A value consisting only of invalid characters must still yield a usable segment.
            return sanitised.Length == 0 ? "_" : sanitised;
        }
    }
}
