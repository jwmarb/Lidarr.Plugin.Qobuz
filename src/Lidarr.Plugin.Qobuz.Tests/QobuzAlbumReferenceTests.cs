using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    [TestFixture]
    public class QobuzAlbumReferenceTests
    {
        [TestCase("https://open.qobuz.com/album/0060253764221", "0060253764221")]
        [TestCase("https://play.qobuz.com/album/za0rwdc1ead7b", "za0rwdc1ead7b")]
        [TestCase("https://www.qobuz.com/us-en/album/some-slug/0060253764221", "0060253764221")]
        [TestCase("https://www.qobuz.com/gb-en/album/rumours-fleetwood-mac/0603497894772", "0603497894772")]
        public void Parses_an_album_id_from_a_qobuz_album_url(string url, string expectedId)
        {
            QobuzAlbumReference.TryParse(url, out var reference).Should().BeTrue();

            reference!.AlbumId.Should().Be(expectedId);
        }

        [Test]
        public void Parses_the_store_download_streaming_albums_form()
        {
            // This longer form must be matched before the generic three-segment pattern,
            // otherwise the wrong path segment is captured as the id.
            var url = "https://www.qobuz.com/us-en/album/a-slug/download-streaming-albums/0060253764221";

            QobuzAlbumReference.TryParse(url, out var reference).Should().BeTrue();

            reference!.AlbumId.Should().Be("0060253764221");
        }

        [Test]
        public void Ignores_query_parameters()
        {
            var url = "https://open.qobuz.com/album/0060253764221?utm_source=newsletter&foo=bar";

            QobuzAlbumReference.TryParse(url, out var reference).Should().BeTrue();

            reference!.AlbumId.Should().Be("0060253764221");
        }

        [Test]
        public void Ignores_url_fragments()
        {
            QobuzAlbumReference.TryParse("https://open.qobuz.com/album/abc123#track-2", out var reference)
                .Should().BeTrue();

            reference!.AlbumId.Should().Be("abc123");
        }

        [Test]
        public void Tolerates_a_trailing_slash_and_surrounding_whitespace()
        {
            QobuzAlbumReference.TryParse("  https://open.qobuz.com/album/abc123/  ", out var reference)
                .Should().BeTrue();

            reference!.AlbumId.Should().Be("abc123");
        }

        [TestCase("http://open.qobuz.com/album/abc123")]
        [TestCase("https://open.qobuz.com/ALBUM/abc123")]
        public void Accepts_http_and_is_case_insensitive_about_the_link_word(string url)
        {
            QobuzAlbumReference.TryParse(url, out var reference).Should().BeTrue();

            reference!.AlbumId.Should().Be("abc123");
        }

        /// <summary>
        /// These are the cases the old parser accepted and then failed on later, inside the
        /// download queue, with an <c>InvalidOperationException</c> carrying no message.
        /// Rejecting them at parse time is the point of narrowing the interface.
        /// </summary>
        [TestCase("https://open.qobuz.com/track/12345678")]
        [TestCase("https://open.qobuz.com/artist/2070579")]
        [TestCase("https://open.qobuz.com/playlist/5747952")]
        [TestCase("https://www.qobuz.com/us-en/interpreter/some-artist/2070579")]
        [TestCase("https://www.qobuz.com/us-en/label/some-label/12345")]
        public void Rejects_qobuz_urls_that_are_not_albums(string url)
        {
            QobuzAlbumReference.TryParse(url, out var reference).Should().BeFalse();

            reference.Should().BeNull();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a url at all")]
        [TestCase("https://tidal.com/album/12345")]
        [TestCase("https://deezer.com/album/12345")]
        [TestCase("magnet:?xt=urn:btih:abcdef")]
        [TestCase("https://qobuz.com.evil.example/album/abc123")]
        public void Rejects_anything_that_is_not_a_qobuz_album_link(string? url)
        {
            QobuzAlbumReference.TryParse(url, out var reference).Should().BeFalse();

            reference.Should().BeNull();
        }

        [Test]
        public void Rejects_an_album_url_with_no_id()
        {
            QobuzAlbumReference.TryParse("https://open.qobuz.com/album/", out var reference)
                .Should().BeFalse();

            reference.Should().BeNull();
        }

        [Test]
        public void Describes_itself_for_logs()
        {
            QobuzAlbumReference.TryParse("https://open.qobuz.com/album/abc123", out var reference);

            reference!.ToString().Should().Be("qobuz:album:abc123");
        }
    }
}
