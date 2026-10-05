using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lidarr.Plugin.Qobuz.Tests.Fakes;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.Download.Clients.Qobuz.Queue;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    /// <summary>
    /// Covers the host-facing boundary: what Lidarr is actually told about a download.
    /// </summary>
    /// <remarks>
    /// This boundary was previously untested, and a code review found that every remaining
    /// defect in the refactor lived in exactly this band — the units and the terminal status
    /// are both load-bearing wire formats that the rest of the suite could not see.
    /// </remarks>
    [TestFixture]
    public class QobuzProxyQueueReportingTests
    {
        private QobuzProxy _proxy = null!;
        private FakeQobuzSessionProvider _sessions = null!;

        [SetUp]
        public void SetUp()
        {
            _sessions = new FakeQobuzSessionProvider();

            // A real CacheManager: it is in-memory and has no external dependency, so faking
            // it would only test the fake.
            _proxy = new QobuzProxy(new CacheManager(), _sessions, LogManager.GetLogger("test"));
        }

        [TearDown]
        public void TearDown()
        {
            _proxy.Dispose();
        }

        private static RemoteAlbum RemoteAlbum(
            string? url = "https://open.qobuz.com/album/0060253764221",
            string? container = "Lossless",
            long size = 350_000_000,
            string artist = "Test Artist",
            string album = "Test Album")
        {
            return new RemoteAlbum
            {
                Release = new ReleaseInfo
                {
                    DownloadUrl = url,
                    Container = container,
                    Size = size,
                    Artist = artist,
                    Album = album,
                }
            };
        }

        private static QobuzSettings Settings(string path = "/tmp/qobuz-tests") =>
            new QobuzSettings { DownloadPath = path };

        [Test]
        public async Task A_queued_download_is_reported_to_lidarr_in_bytes()
        {
            await _proxy.Download(RemoteAlbum(size: 350_000_000), Settings(), FakeIndexer());

            var item = _proxy.GetQueue().Should().ContainSingle().Subject;

            // Bytes, not a track count. Lidarr's QueueService displays these directly and
            // TrackedDownloadService feeds TotalSize to size-based custom formats, which
            // compare against gigabyte thresholds.
            item.TotalSize.Should().Be(350_000_000);
            item.RemainingSize.Should().Be(350_000_000, "nothing has transferred yet");
        }

        [Test]
        public async Task A_queued_download_reports_no_eta_before_any_bytes_arrive()
        {
            await _proxy.Download(RemoteAlbum(), Settings(), FakeIndexer());

            // RemainingTime is the queue's sort key, so a fabricated value would reorder the
            // user's queue for no reason.
            _proxy.GetQueue().Single().RemainingTime.Should().BeNull();
        }

        [Test]
        public async Task The_queue_title_carries_artist_album_and_quality()
        {
            await _proxy.Download(
                RemoteAlbum(container: "24bit 96kHz", artist: "Pink Floyd", album: "The Wall"),
                Settings(),
                FakeIndexer());

            var title = _proxy.GetQueue().Single().Title;

            title.Should().Contain("Pink Floyd");
            title.Should().Contain("The Wall");
            title.Should().Contain("FLAC 24bit 96kHz");
            title.Should().Contain("[WEB]");
        }

        [Test]
        public async Task A_queued_download_is_movable_and_removable()
        {
            await _proxy.Download(RemoteAlbum(), Settings(), FakeIndexer());

            var item = _proxy.GetQueue().Single();

            // CanMoveFiles false would make Lidarr copy instead of move on import;
            // CanBeRemoved false would prevent cleanup after import.
            item.CanMoveFiles.Should().BeTrue();
            item.CanBeRemoved.Should().BeTrue();
            item.DownloadId.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public async Task Removing_a_download_takes_it_out_of_the_queue()
        {
            var id = await _proxy.Download(RemoteAlbum(), Settings(), FakeIndexer());

            _proxy.RemoveFromQueue(id);

            _proxy.GetQueue().Should().BeEmpty();
        }

        [Test]
        public void A_release_url_that_is_not_a_qobuz_album_is_rejected_at_the_door()
        {
            // Previously this produced a null queue item that was enqueued anyway, throwing
            // ArgumentNullException when used as a dictionary key.
            FluentActions.Invoking(() => _proxy
                    .Download(RemoteAlbum(url: "https://tidal.com/album/123"), Settings(), FakeIndexer())
                    .GetAwaiter().GetResult())
                .Should().Throw<DownloadClientException>()
                .WithMessage("*not a Qobuz album link*");
        }

        [Test]
        public void An_unrecognised_container_is_rejected_rather_than_downgraded()
        {
            // Previously an unknown container silently became MP3 320, so a user's hi-res
            // grab was quietly delivered as a lossy file.
            FluentActions.Invoking(() => _proxy
                    .Download(RemoteAlbum(container: "24bit 384kHz"), Settings(), FakeIndexer())
                    .GetAwaiter().GetResult())
                .Should().Throw<DownloadClientException>()
                .WithMessage("*audio quality*");
        }

        [Test]
        public void A_release_from_a_non_qobuz_indexer_is_rejected()
        {
            // Credentials live in the Qobuz indexer's settings, so there is nothing to
            // authenticate with otherwise.
            FluentActions.Invoking(() => _proxy
                    .Download(RemoteAlbum(), Settings(), new NonQobuzIndexer())
                    .GetAwaiter().GetResult())
                .Should().Throw<DownloadClientException>()
                .WithMessage("*credentials are unavailable*");
        }

        [Test]
        public void A_missing_release_is_rejected()
        {
            FluentActions.Invoking(() => _proxy
                    .Download(new RemoteAlbum(), Settings(), FakeIndexer())
                    .GetAwaiter().GetResult())
                .Should().Throw<ArgumentException>();
        }

        [Test]
        public async Task An_empty_queue_reports_nothing()
        {
            await Task.CompletedTask;

            _proxy.GetQueue().Should().BeEmpty();
        }

        [Test]
        public void Removing_an_unknown_download_is_harmless()
        {
            _proxy.Invoking(p => p.RemoveFromQueue("no-such-id")).Should().NotThrow();
        }

        [Test]
        public void Dispose_is_idempotent()
        {
            _proxy.Dispose();

            _proxy.Invoking(p => p.Dispose()).Should().NotThrow();
        }

        /// <summary>
        /// Lidarr never disposes its DI container, so the queue's shutdown has to be reachable
        /// through the host's own lifecycle event instead.
        /// </summary>
        [Test]
        public void Handles_the_host_shutdown_event()
        {
            _proxy.Invoking(p => p.Handle(
                    new NzbDrone.Core.Lifecycle.ApplicationShutdownRequested()))
                .Should().NotThrow();
        }

        /// <summary>
        /// End-to-end proof of the Critical fix: a partial album must reach Failed.
        /// </summary>
        /// <remarks>
        /// Runs the real pipeline — proxy, queue, downloader — against the fake session, with
        /// one track rigged to fail. Before the fix this reported Warning, a status Lidarr
        /// ignores for terminal items, so the album was never imported and never retried.
        /// </remarks>
        [Test]
        public async Task A_partial_album_ends_up_failed_so_lidarr_will_retry_it()
        {
            var credentials = new QobuzCredentials(
                "user@example.com", "0123456789abcdef0123456789abcdef", null, null, null, null);

            var session = new FakeQobuzSession(credentials);
            session.AddAlbum(TestMetadata.Plan(trackCount: 3, albumId: "0060253764221"));
            session.MakeTrackFail("track-2");
            _sessions.Register(credentials, session);

            var downloadRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "qobuz-partial-" + Guid.NewGuid().ToString("N"));

            try
            {
                await _proxy.Download(RemoteAlbum(), Settings(downloadRoot), FakeIndexer());

                await WaitForStatus(DownloadItemStatus.Failed);

                var item = _proxy.GetQueue().Single();

                item.Status.Should().Be(DownloadItemStatus.Failed,
                    "Warning is ignored by both CompletedDownloadService and "
                    + "FailedDownloadService, so a partial album would strand forever");
                item.Message.Should().Contain("2 of 3");
            }
            finally
            {
                if (System.IO.Directory.Exists(downloadRoot))
                {
                    System.IO.Directory.Delete(downloadRoot, recursive: true);
                }
            }
        }

        [Test]
        public async Task A_fully_downloaded_album_ends_up_completed_so_lidarr_will_import_it()
        {
            var credentials = new QobuzCredentials(
                "user@example.com", "0123456789abcdef0123456789abcdef", null, null, null, null);

            var session = new FakeQobuzSession(credentials);
            session.AddAlbum(TestMetadata.Plan(trackCount: 3, albumId: "0060253764221"));
            _sessions.Register(credentials, session);

            var downloadRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "qobuz-full-" + Guid.NewGuid().ToString("N"));

            try
            {
                await _proxy.Download(RemoteAlbum(), Settings(downloadRoot), FakeIndexer());

                await WaitForStatus(DownloadItemStatus.Completed);

                var item = _proxy.GetQueue().Single();

                // Completed is the only status Lidarr will import, and the output path is how
                // it finds the files.
                item.Status.Should().Be(DownloadItemStatus.Completed);
                item.OutputPath.ToString().Should().NotBeNullOrWhiteSpace();
                item.RemainingSize.Should().Be(0);
            }
            finally
            {
                if (System.IO.Directory.Exists(downloadRoot))
                {
                    System.IO.Directory.Delete(downloadRoot, recursive: true);
                }
            }
        }

        private async Task WaitForStatus(DownloadItemStatus status, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (_proxy.GetQueue().Any(i => i.Status == status))
                {
                    return;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException(
                $"No queue item reached {status}. Observed: "
                + string.Join(", ", _proxy.GetQueue().Select(i => i.Status.ToString())));
        }

        private static NzbDrone.Core.Indexers.IIndexer FakeIndexer() => new StubQobuzIndexer();

        /// <summary>Supplies a Qobuz indexer definition carrying usable credentials.</summary>
        private sealed class StubQobuzIndexer : NzbDrone.Core.Indexers.IIndexer
        {
            public string Name => "Qobuz";

            public string Protocol => nameof(NzbDrone.Core.Indexers.QobuzDownloadProtocol);

            public bool SupportsRss => false;

            public bool SupportsSearch => true;

            public Type ConfigContract => typeof(NzbDrone.Core.Indexers.Qobuz.QobuzIndexerSettings);

            public NzbDrone.Core.ThingiProvider.ProviderMessage Message => null!;

            public System.Collections.Generic.IEnumerable<NzbDrone.Core.ThingiProvider.ProviderDefinition> DefaultDefinitions =>
                Array.Empty<NzbDrone.Core.ThingiProvider.ProviderDefinition>();

            public NzbDrone.Core.ThingiProvider.ProviderDefinition Definition { get; set; } =
                new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Id = 1,
                    Name = "Qobuz",
                    Settings = new NzbDrone.Core.Indexers.Qobuz.QobuzIndexerSettings
                    {
                        Email = "user@example.com",
                        MD5Password = "0123456789abcdef0123456789abcdef",
                    }
                };

            public object RequestAction(string action, System.Collections.Generic.IDictionary<string, string> query) => null!;

            public NzbDrone.Core.ThingiProvider.Status.ProviderStatusBase? Status() => null;

            public FluentValidation.Results.ValidationResult Test() =>
                new FluentValidation.Results.ValidationResult();

            public Task<System.Collections.Generic.IList<ReleaseInfo>> FetchRecent() =>
                Task.FromResult<System.Collections.Generic.IList<ReleaseInfo>>(new System.Collections.Generic.List<ReleaseInfo>());

            public Task<System.Collections.Generic.IList<ReleaseInfo>> Fetch(NzbDrone.Core.IndexerSearch.Definitions.AlbumSearchCriteria searchCriteria) =>
                Task.FromResult<System.Collections.Generic.IList<ReleaseInfo>>(new System.Collections.Generic.List<ReleaseInfo>());

            public Task<System.Collections.Generic.IList<ReleaseInfo>> Fetch(NzbDrone.Core.IndexerSearch.Definitions.ArtistSearchCriteria searchCriteria) =>
                Task.FromResult<System.Collections.Generic.IList<ReleaseInfo>>(new System.Collections.Generic.List<ReleaseInfo>());

            public NzbDrone.Common.Http.HttpRequest GetDownloadRequest(string link) => null!;
        }

        /// <summary>An indexer whose settings are not Qobuz settings.</summary>
        private sealed class NonQobuzIndexer : StubQobuzIndexerBase
        {
        }

        private class StubQobuzIndexerBase : NzbDrone.Core.Indexers.IIndexer
        {
            public string Name => "Other";

            public string Protocol => "OtherProtocol";

            public bool SupportsRss => true;

            public bool SupportsSearch => true;

            public Type ConfigContract => typeof(object);

            public NzbDrone.Core.ThingiProvider.ProviderMessage Message => null!;

            public System.Collections.Generic.IEnumerable<NzbDrone.Core.ThingiProvider.ProviderDefinition> DefaultDefinitions =>
                Array.Empty<NzbDrone.Core.ThingiProvider.ProviderDefinition>();

            public NzbDrone.Core.ThingiProvider.ProviderDefinition Definition { get; set; } =
                new NzbDrone.Core.Indexers.IndexerDefinition { Id = 2, Name = "Other" };

            public object RequestAction(string action, System.Collections.Generic.IDictionary<string, string> query) => null!;

            public NzbDrone.Core.ThingiProvider.Status.ProviderStatusBase? Status() => null;

            public FluentValidation.Results.ValidationResult Test() =>
                new FluentValidation.Results.ValidationResult();

            public Task<System.Collections.Generic.IList<ReleaseInfo>> FetchRecent() =>
                Task.FromResult<System.Collections.Generic.IList<ReleaseInfo>>(new System.Collections.Generic.List<ReleaseInfo>());

            public Task<System.Collections.Generic.IList<ReleaseInfo>> Fetch(NzbDrone.Core.IndexerSearch.Definitions.AlbumSearchCriteria searchCriteria) =>
                Task.FromResult<System.Collections.Generic.IList<ReleaseInfo>>(new System.Collections.Generic.List<ReleaseInfo>());

            public Task<System.Collections.Generic.IList<ReleaseInfo>> Fetch(NzbDrone.Core.IndexerSearch.Definitions.ArtistSearchCriteria searchCriteria) =>
                Task.FromResult<System.Collections.Generic.IList<ReleaseInfo>>(new System.Collections.Generic.List<ReleaseInfo>());

            public NzbDrone.Common.Http.HttpRequest GetDownloadRequest(string link) => null!;
        }
    }
}
