using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Plugin.Qobuz.API
{
    /// <summary>
    /// A Qobuz streaming quality. The numeric values are Qobuz's own <c>format_id</c> values
    /// and are sent to the API verbatim, so they must not be renumbered.
    /// </summary>
    public enum AudioQuality
    {
        MP3320 = 5,
        FLACLossless = 6,
        FLACHiRes24Bit96kHz = 7,
        FLACHiRes24Bit192Khz = 27,
    }

    /// <summary>
    /// Everything the plugin needs to know about one <see cref="AudioQuality"/>: how to ask
    /// Qobuz for it, how to describe it to Lidarr, and how big it is likely to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BitsPerSecond"/> is the uncompressed PCM rate (bit depth x sample rate x
    /// channels) for the FLAC tiers and the nominal encoder rate for MP3. It is only ever used
    /// to estimate a size, because Qobuz exposes no cheap way to obtain a real byte count.
    /// FLAC compresses, so these estimates run high for lossless tiers.
    /// </para>
    /// </remarks>
    public sealed class AudioQualitySpec
    {
        internal AudioQualitySpec(
            AudioQuality quality,
            string container,
            string codec,
            string fileExtension,
            string displayLabel,
            long bitsPerSecond)
        {
            Quality = quality;
            Container = container;
            Codec = codec;
            FileExtension = fileExtension;
            DisplayLabel = displayLabel;
            BitsPerSecond = bitsPerSecond;
        }

        public AudioQuality Quality { get; }

        /// <summary>
        /// Qobuz's <c>format_id</c>, as the string their API expects.
        /// </summary>
        public string FormatId => ((int)Quality).ToString();

        /// <summary>
        /// The value placed in <c>ReleaseInfo.Container</c>. This string round-trips: the
        /// download client parses it back with <see cref="AudioQualities.TryFromContainer"/>,
        /// so it is a wire format between the indexer and the download client and may not be
        /// changed on one side alone.
        /// </summary>
        public string Container { get; }

        /// <summary>The value placed in <c>ReleaseInfo.Codec</c>.</summary>
        public string Codec { get; }

        /// <summary>File extension, without a leading dot.</summary>
        public string FileExtension { get; }

        /// <summary>Human-readable label used in release and queue titles.</summary>
        public string DisplayLabel { get; }

        /// <summary>Nominal bits per second, used only for size estimation.</summary>
        public long BitsPerSecond { get; }

        /// <summary>
        /// Estimated size <b>in bytes</b> for a track or album of the given duration.
        /// </summary>
        /// <remarks>
        /// Lidarr reads <c>ReleaseInfo.Size</c> and <c>DownloadClientItem.TotalSize</c> as
        /// bytes, so the division by 8 here is load-bearing: omitting it inflates every
        /// reported size eightfold and silently breaks size-based custom formats, which
        /// compare against gigabyte thresholds.
        /// </remarks>
        public long EstimateBytes(long durationSeconds)
        {
            if (durationSeconds <= 0)
            {
                return 0;
            }

            return durationSeconds * BitsPerSecond / 8;
        }
    }

    /// <summary>
    /// The one place that knows how Qobuz qualities map to Lidarr's vocabulary.
    /// </summary>
    /// <remarks>
    /// This mapping previously existed in three places and two directions — the indexer parser,
    /// the download item, and the queue projection — and had to agree across all of them or a
    /// user's requested quality would silently degrade. Callers now ask this module instead of
    /// switching on the enum themselves.
    /// </remarks>
    public static class AudioQualities
    {
        private static readonly AudioQualitySpec[] Specs =
        {
            new AudioQualitySpec(
                AudioQuality.MP3320,
                container: "320",
                codec: "MP3",
                fileExtension: "mp3",
                displayLabel: "MP3 320kbps",
                bitsPerSecond: 320_000),

            new AudioQualitySpec(
                AudioQuality.FLACLossless,
                container: "Lossless",
                codec: "FLAC",
                fileExtension: "flac",
                displayLabel: "FLAC Lossless",

                // 16 bit x 44100 Hz x 2 channels
                bitsPerSecond: 1_411_200),

            new AudioQualitySpec(
                AudioQuality.FLACHiRes24Bit96kHz,
                container: "24bit 96kHz",
                codec: "FLAC",
                fileExtension: "flac",
                displayLabel: "FLAC 24bit 96kHz",

                // 24 bit x 96000 Hz x 2 channels
                bitsPerSecond: 4_608_000),

            new AudioQualitySpec(
                AudioQuality.FLACHiRes24Bit192Khz,
                container: "24bit 192kHz",
                codec: "FLAC",
                fileExtension: "flac",
                displayLabel: "FLAC 24bit 192kHz",

                // 24 bit x 192000 Hz x 2 channels
                bitsPerSecond: 9_216_000),
        };

        /// <summary>All known qualities, lowest fidelity first.</summary>
        public static IReadOnlyList<AudioQualitySpec> All { get; } = Specs;

        /// <summary>
        /// The qualities Qobuz always offers for a streamable album.
        /// </summary>
        public static IReadOnlyList<AudioQualitySpec> StandardTiers { get; } = new[]
        {
            Specs[0], Specs[1]
        };

        /// <summary>
        /// The additional qualities offered only when an album is flagged hi-res streamable.
        /// </summary>
        /// <remarks>
        /// Qobuz advertises 192 kHz whenever hi-res exists and silently serves 96 kHz when it
        /// does not, which cannot be detected without downloading. Both are offered.
        /// </remarks>
        public static IReadOnlyList<AudioQualitySpec> HiResTiers { get; } = new[]
        {
            Specs[3], Specs[2]
        };

        /// <summary>
        /// The spec for a known quality.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is not a declared <see cref="AudioQuality"/>. Previously the three
        /// duplicated mappings disagreed here: one threw <c>NotImplementedException</c>,
        /// another silently substituted MP3.
        /// </exception>
        public static AudioQualitySpec For(AudioQuality quality)
        {
            var spec = Specs.FirstOrDefault(s => s.Quality == quality);

            if (spec == null)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(quality),
                    quality,
                    "Unknown Qobuz audio quality.");
            }

            return spec;
        }

        /// <summary>
        /// Parses a <c>ReleaseInfo.Container</c> string produced by the indexer back into a
        /// quality.
        /// </summary>
        /// <remarks>
        /// Returns <see langword="false"/> rather than silently downgrading. The previous
        /// behaviour mapped anything unrecognised to MP3 320, so a mismatch between the
        /// indexer and the download client turned a user's hi-res grab into a lossy file with
        /// no warning anywhere.
        /// </remarks>
        public static bool TryFromContainer(string? container, out AudioQualitySpec spec)
        {
            if (!string.IsNullOrWhiteSpace(container))
            {
                var trimmed = container.Trim();

                var match = Specs.FirstOrDefault(
                    s => string.Equals(s.Container, trimmed, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    spec = match;
                    return true;
                }
            }

            spec = Specs[0];
            return false;
        }
    }
}
