using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    [TestFixture]
    public class AudioQualitiesTests
    {
        [Test]
        public void Format_ids_are_the_values_Qobuz_expects()
        {
            // These are Qobuz's own format_id values and are sent on the wire. If one of these
            // changes, downloads request the wrong quality.
            AudioQualities.For(AudioQuality.MP3320).FormatId.Should().Be("5");
            AudioQualities.For(AudioQuality.FLACLossless).FormatId.Should().Be("6");
            AudioQualities.For(AudioQuality.FLACHiRes24Bit96kHz).FormatId.Should().Be("7");
            AudioQualities.For(AudioQuality.FLACHiRes24Bit192Khz).FormatId.Should().Be("27");
        }

        [TestCase(AudioQuality.MP3320, "320", "MP3", "mp3", "MP3 320kbps")]
        [TestCase(AudioQuality.FLACLossless, "Lossless", "FLAC", "flac", "FLAC Lossless")]
        [TestCase(AudioQuality.FLACHiRes24Bit96kHz, "24bit 96kHz", "FLAC", "flac", "FLAC 24bit 96kHz")]
        [TestCase(AudioQuality.FLACHiRes24Bit192Khz, "24bit 192kHz", "FLAC", "flac", "FLAC 24bit 192kHz")]
        public void Describes_each_quality_the_way_Lidarr_is_told(
            AudioQuality quality,
            string expectedContainer,
            string expectedCodec,
            string expectedExtension,
            string expectedLabel)
        {
            var spec = AudioQualities.For(quality);

            spec.Container.Should().Be(expectedContainer);
            spec.Codec.Should().Be(expectedCodec);
            spec.FileExtension.Should().Be(expectedExtension);
            spec.DisplayLabel.Should().Be(expectedLabel);
        }

        /// <summary>
        /// The whole point of unifying this mapping: the indexer writes Container and the
        /// download client reads it back. If the round-trip breaks, a user's hi-res grab
        /// silently becomes MP3.
        /// </summary>
        [TestCase(AudioQuality.MP3320)]
        [TestCase(AudioQuality.FLACLossless)]
        [TestCase(AudioQuality.FLACHiRes24Bit96kHz)]
        [TestCase(AudioQuality.FLACHiRes24Bit192Khz)]
        public void Container_round_trips_through_the_indexer_to_the_download_client(AudioQuality quality)
        {
            var container = AudioQualities.For(quality).Container;

            var parsed = AudioQualities.TryFromContainer(container, out var spec);

            parsed.Should().BeTrue();
            spec!.Quality.Should().Be(quality);
        }

        [Test]
        public void Container_parsing_ignores_case_and_surrounding_whitespace()
        {
            AudioQualities.TryFromContainer("  lossless  ", out var spec).Should().BeTrue();

            spec!.Quality.Should().Be(AudioQuality.FLACLossless);
        }

        /// <summary>
        /// Regression guard. The previous code had <c>_ =&gt; AudioQuality.MP3320</c>, so an
        /// unrecognised container downgraded the download to a lossy file with no signal.
        /// </summary>
        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        [TestCase("24bit 384kHz")]
        [TestCase("Dolby Atmos")]
        [TestCase("garbage")]
        public void Unknown_container_is_reported_as_unparsed_rather_than_downgraded(string? container)
        {
            var parsed = AudioQualities.TryFromContainer(container, out _);

            parsed.Should().BeFalse(
                "an unrecognised container must be reported, not silently turned into MP3 320");
        }

        /// <summary>
        /// On failure the out-parameter must be null, not a default quality. Returning
        /// MP3 320 there is what the original code did implicitly, so a caller who ignored
        /// the boolean silently downgraded a hi-res grab to a lossy file.
        /// </summary>
        [TestCase("")]
        [TestCase(null)]
        [TestCase("Dolby Atmos")]
        public void A_failed_container_parse_yields_no_spec_at_all(string? container)
        {
            AudioQualities.TryFromContainer(container, out var spec).Should().BeFalse();

            spec.Should().BeNull(
                "handing back a default quality on failure is a silent downgrade");
        }

        [Test]
        public void Unknown_quality_value_is_rejected_loudly()
        {
            var bogus = (AudioQuality)9999;

            Action act = () => AudioQualities.For(bogus);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        /// <summary>
        /// The bug this test exists to prevent: sizes were computed as
        /// <c>durationSeconds * bitsPerSecond</c> and assigned to <c>ReleaseInfo.Size</c>,
        /// which Lidarr reads as bytes. Every release was reported eight times too large,
        /// which silently breaks size-based custom formats and quality size limits.
        /// </summary>
        [Test]
        public void Size_estimates_are_bytes_not_bits()
        {
            // A 10 minute CD-quality album: 16 bit x 44100 Hz x 2ch = 1411200 bits/s.
            // 600 s x 1411200 bits/s = 846,720,000 bits = 105,840,000 bytes (~101 MiB).
            var spec = AudioQualities.For(AudioQuality.FLACLossless);

            var bytes = spec.EstimateBytes(600);

            bytes.Should().Be(105_840_000);
            bytes.Should().BeLessThan(600 * spec.BitsPerSecond,
                "a byte count must be smaller than the bit count it derives from");
        }

        [Test]
        public void Size_estimate_for_a_three_minute_mp3_is_plausible()
        {
            // 180 s at 320 kbps = 7,200,000 bytes, about 6.9 MiB. A real 3 minute 320 kbps
            // MP3 is around 7 MB, so this sanity-checks the unit rather than the precision.
            var bytes = AudioQualities.For(AudioQuality.MP3320).EstimateBytes(180);

            bytes.Should().Be(7_200_000);
            (bytes / 1024.0 / 1024.0).Should().BeApproximately(6.87, 0.05);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(-9999)]
        public void Non_positive_duration_estimates_nothing(long duration)
        {
            // Qobuz returns a nullable duration; callers coalesce a missing one to zero.
            AudioQualities.For(AudioQuality.FLACLossless).EstimateBytes(duration).Should().Be(0);
        }

        [Test]
        public void Hi_res_tiers_offer_192_before_96()
        {
            // Qobuz advertises 192 kHz whenever hi-res exists and serves 96 kHz when it does
            // not. Preserving this order keeps the indexer's released ordering stable.
            AudioQualities.HiResTiers.Should().HaveCount(2);
            AudioQualities.HiResTiers[0].Quality.Should().Be(AudioQuality.FLACHiRes24Bit192Khz);
            AudioQualities.HiResTiers[1].Quality.Should().Be(AudioQuality.FLACHiRes24Bit96kHz);
        }

        [Test]
        public void Standard_tiers_are_always_available_qualities()
        {
            AudioQualities.StandardTiers.Should().HaveCount(2);
            AudioQualities.StandardTiers[0].Quality.Should().Be(AudioQuality.MP3320);
            AudioQualities.StandardTiers[1].Quality.Should().Be(AudioQuality.FLACLossless);
        }

        [Test]
        public void Every_declared_quality_has_exactly_one_spec()
        {
            foreach (AudioQuality quality in Enum.GetValues(typeof(AudioQuality)))
            {
                AudioQualities.For(quality).Quality.Should().Be(quality);
            }

            AudioQualities.All.Should().HaveCount(Enum.GetValues(typeof(AudioQuality)).Length);
        }

        [Test]
        public void Container_strings_are_distinct_so_the_round_trip_is_unambiguous()
        {
            var containers = new System.Collections.Generic.List<string>();

            foreach (var spec in AudioQualities.All)
            {
                containers.Add(spec.Container.ToLowerInvariant());
            }

            containers.Should().OnlyHaveUniqueItems();
        }
    }
}
