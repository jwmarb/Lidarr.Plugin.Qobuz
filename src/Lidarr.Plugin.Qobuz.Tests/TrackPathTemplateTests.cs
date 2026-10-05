using System;
using System.IO;
using FluentAssertions;
using Lidarr.Plugin.Qobuz.Tests.Fakes;
using NUnit.Framework;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    [TestFixture]
    public class TrackPathTemplateTests
    {
        [Test]
        public void Renders_the_default_album_directory()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(albumArtist: "Pink Floyd", title: "The Wall"),
                TestMetadata.Track(),
                "flac");

            TrackPathTemplate.Render(TrackPathTemplate.AlbumDirectory, fields)
                .Should().Be("Pink Floyd/The Wall/");
        }

        [Test]
        public void Renders_the_default_track_filename()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(),
                TestMetadata.Track(title: "Comfortably Numb", trackNumber: 6, mediaNumber: 2),
                "flac");

            TrackPathTemplate.Render(TrackPathTemplate.TrackFile, fields)
                .Should().Be("02 - 06 - Comfortably Numb.flac");
        }

        [Test]
        public void Pads_track_and_volume_numbers_to_two_digits()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(),
                TestMetadata.Track(trackNumber: 3, mediaNumber: 1),
                "mp3");

            TrackPathTemplate.Render("%volume%-%track%", fields).Should().Be("01-03");
        }

        [Test]
        public void Does_not_truncate_numbers_beyond_two_digits()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(),
                TestMetadata.Track(trackNumber: 142),
                "mp3");

            TrackPathTemplate.Render("%track%", fields).Should().Be("142");
        }

        [Test]
        public void Substitutes_every_supported_placeholder()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(
                    title: "Album Title",
                    albumArtist: "Album Artist",
                    trackCount: 11,
                    mediaCount: 2,
                    year: 1979),
                TestMetadata.Track(
                    title: "Track Title",
                    performer: "Track Artist",
                    trackNumber: 4,
                    mediaNumber: 2,
                    year: 1979),
                "flac");

            var template =
                "%title%|%album%|%albumartist%|%artist%|%albumartists%|%artists%"
                + "|%track%|%trackcount%|%volume%|%volumecount%|%year%|%ext%";

            TrackPathTemplate.Render(template, fields).Should().Be(
                "Track Title|Album Title|Album Artist|Track Artist|Album Artist|Track Artist"
                + "|04|11|02|2|1979|flac");
        }

        /// <summary>
        /// Path separators inside a substituted value would silently create directories, so
        /// they must be neutralised; separators written in the template itself must survive.
        /// </summary>
        [Test]
        public void Sanitises_separators_inside_values_but_not_in_the_template()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(albumArtist: "AC/DC", title: "Back: In Black"),
                TestMetadata.Track(),
                "flac");

            var rendered = TrackPathTemplate.Render(TrackPathTemplate.AlbumDirectory, fields);

            rendered.Should().StartWith("AC_DC/");
            rendered.Should().NotContain("AC/DC");
        }

        [TestCase("a\0b")]
        [TestCase("a\nb")]
        public void Replaces_characters_that_cannot_appear_in_a_filename(string hostile)
        {
            TrackPathTemplate.Sanitise(hostile).Should().NotContain("\0").And.NotContain("\n");
        }

        [Test]
        public void Trims_trailing_dots_and_spaces()
        {
            // Windows silently strips these and then cannot find the file it just wrote.
            TrackPathTemplate.Sanitise("Album Name. ").Should().Be("Album Name");
            TrackPathTemplate.Sanitise("Album...").Should().Be("Album");
        }

        [Test]
        public void Produces_a_usable_segment_for_a_value_made_only_of_bad_characters()
        {
            TrackPathTemplate.Sanitise("...").Should().Be("_");
            TrackPathTemplate.Sanitise("   ").Should().Be("_");
        }

        [Test]
        public void Renders_an_empty_string_for_a_null_or_empty_value()
        {
            TrackPathTemplate.Sanitise(null).Should().BeEmpty();
            TrackPathTemplate.Sanitise("").Should().BeEmpty();
        }

        [Test]
        public void Falls_back_to_the_album_artist_when_a_track_has_no_performer()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(albumArtist: "Various Artists"),
                TestMetadata.Track(performer: ""),
                "flac");

            fields.Artist.Should().Be("Various Artists");
        }

        [Test]
        public void Omits_the_year_placeholder_when_no_year_is_known()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(year: null),
                TestMetadata.Track(year: null),
                "flac");

            TrackPathTemplate.Render("x%year%y", fields).Should().Be("xy");
        }

        [Test]
        public void Prefers_the_track_year_over_the_album_year()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(year: 1979),
                TestMetadata.Track(year: 1995),
                "flac");

            fields.Year.Should().Be(1995);
        }

        [Test]
        public void Rejects_null_arguments()
        {
            var fields = new TrackPathFields(TestMetadata.Album(), TestMetadata.Track(), "flac");

            FluentActions.Invoking(() => TrackPathTemplate.Render(null!, fields))
                .Should().Throw<ArgumentNullException>();

            FluentActions.Invoking(() => TrackPathTemplate.Render("%title%", null!))
                .Should().Throw<ArgumentNullException>();

            FluentActions.Invoking(() => new TrackPathFields(null!, TestMetadata.Track(), "flac"))
                .Should().Throw<ArgumentNullException>();

            FluentActions.Invoking(() => new TrackPathFields(TestMetadata.Album(), null!, "flac"))
                .Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void A_rendered_filename_contains_no_invalid_characters()
        {
            var fields = new TrackPathFields(
                TestMetadata.Album(albumArtist: "AC/DC"),
                TestMetadata.Track(title: "Who Made Who? <live>"),
                "flac");

            var fileName = TrackPathTemplate.Render(TrackPathTemplate.TrackFile, fields);

            fileName.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
        }
    }
}
