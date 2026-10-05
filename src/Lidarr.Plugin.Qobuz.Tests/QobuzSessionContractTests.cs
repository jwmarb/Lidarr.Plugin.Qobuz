using System;
using System.Collections.Generic;
using FluentAssertions;
using Lidarr.Plugin.Qobuz.Tests.Fakes;
using NUnit.Framework;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    /// <summary>
    /// Guards the invariants the album-download plan promises its callers.
    /// </summary>
    /// <remarks>
    /// <see cref="QobuzAlbumDownloader"/> indexes <c>Tracks[0]</c> to resolve the output
    /// directory before any track is transferred. That is only safe because the plan refuses
    /// to exist in an empty state, so the guarantee is tested here rather than left to a
    /// comment.
    /// </remarks>
    [TestFixture]
    public class QobuzAlbumDownloadPlanTests
    {
        [Test]
        public void Rejects_a_plan_with_no_tracks()
        {
            Action act = () => new QobuzAlbumDownloadPlan(
                TestMetadata.Album(),
                Array.Empty<QobuzTrackMetadata>());

            act.Should().Throw<ArgumentException>()
                .WithMessage("*at least one track*");
        }

        [Test]
        public void Rejects_a_null_album_or_track_list()
        {
            FluentActions.Invoking(() => new QobuzAlbumDownloadPlan(
                    null!,
                    new[] { TestMetadata.Track() }))
                .Should().Throw<ArgumentNullException>();

            FluentActions.Invoking(() => new QobuzAlbumDownloadPlan(TestMetadata.Album(), null!))
                .Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void Keeps_tracks_in_the_order_supplied()
        {
            var tracks = new List<QobuzTrackMetadata>
            {
                TestMetadata.Track(id: "t1", trackNumber: 1),
                TestMetadata.Track(id: "t2", trackNumber: 2),
                TestMetadata.Track(id: "t3", trackNumber: 3),
            };

            var plan = new QobuzAlbumDownloadPlan(TestMetadata.Album(), tracks);

            plan.Tracks.Should().HaveCount(3);
            plan.Tracks[0].Id.Should().Be("t1");
            plan.Tracks[2].Id.Should().Be("t3");
        }

        [Test]
        public void A_single_track_album_is_valid()
        {
            var plan = new QobuzAlbumDownloadPlan(
                TestMetadata.Album(trackCount: 1),
                new[] { TestMetadata.Track() });

            plan.Tracks.Should().ContainSingle();
        }
    }

    [TestFixture]
    public class QobuzCredentialsTests
    {
        private const string PasswordHash = "DISTINCTIVE-MD5-HASH";
        private const string AuthToken = "DISTINCTIVE-AUTH-TOKEN";
        private const string AppSecret = "DISTINCTIVE-APP-SECRET";

        private static QobuzCredentials Full() => new QobuzCredentials(
            "user@example.com", PasswordHash, "12345", AuthToken, "app-id", AppSecret);

        /// <summary>
        /// A credentials object is logged at Debug in the session provider, so its string
        /// form must never carry a secret.
        /// </summary>
        [Test]
        public void ToString_never_reveals_a_secret()
        {
            var rendered = Full().ToString();

            rendered.Should().NotContain(PasswordHash);
            rendered.Should().NotContain(AuthToken);
            rendered.Should().NotContain(AppSecret);
            rendered.Should().Be("QobuzCredentials(email login)");
        }

        [Test]
        public void Interpolation_also_reveals_no_secret()
        {
            // This is the shape NLog's "{0}" produces.
            var interpolated = $"Authenticated a Qobuz session ({Full()}).";

            interpolated.Should().NotContain(PasswordHash);
            interpolated.Should().NotContain(AuthToken);
            interpolated.Should().NotContain(AppSecret);
        }

        [Test]
        public void Describes_a_token_login_without_revealing_the_token()
        {
            var tokenOnly = new QobuzCredentials(null, null, "12345", AuthToken, null, null);

            tokenOnly.ToString().Should().Be("QobuzCredentials(token login)");
            tokenOnly.ToString().Should().NotContain(AuthToken);
        }

        [Test]
        public void Describes_incomplete_credentials()
        {
            new QobuzCredentials(null, null, null, null, null, null)
                .ToString().Should().Be("QobuzCredentials(incomplete)");
        }

        /// <summary>
        /// The session cache keys on these credentials, so equality and hashing must agree.
        /// </summary>
        [Test]
        public void Equal_credentials_are_interchangeable_as_a_cache_key()
        {
            var a = Full();
            var b = Full();

            a.Equals(b).Should().BeTrue();
            a.GetHashCode().Should().Be(b.GetHashCode());
        }

        [Test]
        public void A_different_secret_produces_a_different_cache_key()
        {
            var a = Full();
            var b = new QobuzCredentials(
                "user@example.com", "OTHER-HASH", "12345", AuthToken, "app-id", AppSecret);

            a.Equals(b).Should().BeFalse("a changed password must not reuse the old session");
        }

        [Test]
        public void Comparisons_are_case_sensitive_and_null_safe()
        {
            Full().Equals(null).Should().BeFalse();
            Full().Equals((object?)null).Should().BeFalse();

            new QobuzCredentials("A@B.C", "h", null, null, null, null)
                .Equals(new QobuzCredentials("a@b.c", "h", null, null, null, null))
                .Should().BeFalse("credentials are opaque strings, not case-insensitive names");
        }

        [Test]
        public void Null_inputs_normalise_to_empty_strings()
        {
            var credentials = new QobuzCredentials(null, null, null, null, null, null);

            credentials.Email.Should().BeEmpty();
            credentials.Md5Password.Should().BeEmpty();
            credentials.UserId.Should().BeEmpty();
            credentials.UserAuthToken.Should().BeEmpty();
            credentials.AppId.Should().BeEmpty();
            credentials.AppSecret.Should().BeEmpty();
        }

        [TestCase("a@b.c", "hash", null, null, true, TestName = "email and password is complete")]
        [TestCase(null, null, "123", "tok", true, TestName = "id and token is complete")]
        [TestCase("a@b.c", null, null, null, false, TestName = "email without password is not")]
        [TestCase(null, "hash", null, null, false, TestName = "password without email is not")]
        [TestCase(null, null, "123", null, false, TestName = "id without token is not")]
        [TestCase(null, null, null, "tok", false, TestName = "token without id is not")]
        [TestCase("   ", "   ", "  ", "  ", false, TestName = "whitespace is not credentials")]
        public void Reports_whether_it_can_authenticate(
            string? email,
            string? password,
            string? userId,
            string? token,
            bool expected)
        {
            new QobuzCredentials(email, password, userId, token, null, null)
                .IsComplete.Should().Be(expected);
        }

        [Test]
        public void Email_login_takes_precedence_when_both_are_supplied()
        {
            var both = Full();

            both.HasEmailLogin.Should().BeTrue();
            both.HasTokenLogin.Should().BeTrue();
            both.ToString().Should().Be("QobuzCredentials(email login)");
        }
    }
}
