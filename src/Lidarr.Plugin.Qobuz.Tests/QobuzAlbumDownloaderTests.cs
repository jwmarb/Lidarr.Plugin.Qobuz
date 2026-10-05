using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lidarr.Plugin.Qobuz.Tests.Fakes;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    [TestFixture]
    public class QobuzAlbumDownloaderTests
    {
        private string _downloadRoot = null!;
        private FakeQobuzSession _session = null!;
        private RecordingTagger _tagger = null!;
        private StubLyricProvider _lyrics = null!;
        private Logger _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _downloadRoot = Path.Combine(
                Path.GetTempPath(),
                "qobuz-tests-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_downloadRoot);

            _session = new FakeQobuzSession();
            _tagger = new RecordingTagger();
            _lyrics = new StubLyricProvider();
            _logger = LogManager.GetLogger("test");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_downloadRoot))
            {
                Directory.Delete(_downloadRoot, recursive: true);
            }
        }

        private QobuzAlbumDownloader CreateDownloader(int maxConcurrency = 3) =>
            new QobuzAlbumDownloader(_session, _tagger, _lyrics, _logger, maxConcurrency);

        private AlbumDownloadRequest CreateRequest(
            AudioQuality quality = AudioQuality.FLACLossless,
            bool saveSyncedLyrics = false,
            bool useLrcLib = false) =>
            new AlbumDownloadRequest(_downloadRoot, quality, saveSyncedLyrics, useLrcLib);

        [Test]
        public async Task Downloads_every_track_in_the_plan()
        {
            var plan = TestMetadata.Plan(trackCount: 4);

            var result = await CreateDownloader().DownloadAsync(plan, CreateRequest());

            result.DownloadedTracks.Should().Be(4);
            result.FailedTracks.Should().Be(0);
            result.IsCompleteSuccess.Should().BeTrue();
            _session.WrittenFiles.Should().HaveCount(4);
        }

        /// <summary>
        /// The core of the A6 deepening: the old code re-fetched the album and re-downloaded
        /// the cover art once per track.
        /// </summary>
        [Test]
        public async Task Fetches_cover_art_once_per_album_not_once_per_track()
        {
            var plan = TestMetadata.Plan(trackCount: 8);

            await CreateDownloader().DownloadAsync(plan, CreateRequest());

            _session.CoverArtRequests.Should().Be(1,
                "cover art is identical for every track on an album");
        }

        [Test]
        public async Task Reports_real_bytes_written()
        {
            _session.BytesPerTrack = 2048;
            var plan = TestMetadata.Plan(trackCount: 3);
            var observer = new RecordingObserver();

            var result = await CreateDownloader().DownloadAsync(plan, CreateRequest(), observer);

            result.BytesWritten.Should().Be(3 * 2048);
            observer.TotalBytes.Should().Be(3 * 2048,
                "the observer must see the same byte count the result reports");
        }

        [Test]
        public async Task Resolves_the_output_directory_once_before_any_track_starts()
        {
            var plan = TestMetadata.Plan(trackCount: 5);
            var observer = new RecordingObserver();

            var result = await CreateDownloader().DownloadAsync(plan, CreateRequest(), observer);

            // Previously every concurrent track assigned DownloadFolder, last write winning.
            observer.OutputDirectories.Should().HaveCount(1);
            observer.OutputDirectories[0].Should().Be(result.OutputDirectory);
        }

        [Test]
        public async Task Writes_files_into_an_artist_and_album_folder()
        {
            var plan = TestMetadata.Plan(trackCount: 1);

            var result = await CreateDownloader().DownloadAsync(plan, CreateRequest());

            result.OutputDirectory.Should().Contain("Test Artist");
            result.OutputDirectory.Should().Contain("Test Album");

            var written = _session.WrittenFiles.Single();
            Path.GetFileName(written).Should().Be("01 - 01 - Track 1.flac");
        }

        [TestCase(AudioQuality.MP3320, "mp3")]
        [TestCase(AudioQuality.FLACLossless, "flac")]
        [TestCase(AudioQuality.FLACHiRes24Bit96kHz, "flac")]
        public async Task Uses_the_file_extension_for_the_requested_quality(
            AudioQuality quality,
            string expectedExtension)
        {
            var plan = TestMetadata.Plan(trackCount: 1);

            await CreateDownloader().DownloadAsync(plan, CreateRequest(quality));

            Path.GetExtension(_session.WrittenFiles.Single())
                .Should().Be("." + expectedExtension);
        }

        /// <summary>
        /// A6 again: tagging must use the metadata the caller already holds.
        /// </summary>
        [Test]
        public async Task Tags_each_track_from_the_plan_metadata()
        {
            var plan = TestMetadata.Plan(trackCount: 3);

            await CreateDownloader().DownloadAsync(plan, CreateRequest());

            _tagger.Calls.Should().HaveCount(3);
            _tagger.Calls.Select(c => c.Track.Title)
                .Should().BeEquivalentTo("Track 1", "Track 2", "Track 3");
            _tagger.Calls.Should().OnlyContain(c => c.Album.Title == "Test Album");
        }

        [Test]
        public async Task A_failing_track_does_not_abort_the_rest_of_the_album()
        {
            var plan = TestMetadata.Plan(trackCount: 4);
            _session.MakeTrackFail("track-2");
            var observer = new RecordingObserver();

            var result = await CreateDownloader().DownloadAsync(plan, CreateRequest(), observer);

            result.DownloadedTracks.Should().Be(3);
            result.FailedTracks.Should().Be(1);
            result.IsCompleteSuccess.Should().BeFalse();
            observer.FailedTracks.Should().ContainSingle(t => t.Id == "track-2");
        }

        [Test]
        public async Task An_unavailable_quality_is_recorded_as_a_track_failure()
        {
            var plan = TestMetadata.Plan(trackCount: 2);
            _session.MakeTrackQualityUnavailable("track-1");

            var result = await CreateDownloader().DownloadAsync(
                plan,
                CreateRequest(AudioQuality.FLACHiRes24Bit192Khz));

            result.FailedTracks.Should().Be(1);
            result.DownloadedTracks.Should().Be(1);
        }

        /// <summary>
        /// Counters are interlocked; with plain ++ across concurrent tasks they lose updates.
        /// </summary>
        [Test]
        public async Task Counts_every_track_when_many_download_concurrently()
        {
            var plan = TestMetadata.Plan(trackCount: 60);

            var result = await CreateDownloader(maxConcurrency: 8)
                .DownloadAsync(plan, CreateRequest());

            result.DownloadedTracks.Should().Be(60);
            result.BytesWritten.Should().Be(60 * _session.BytesPerTrack);
        }

        [Test]
        public async Task Tagging_failure_keeps_the_downloaded_audio()
        {
            var plan = TestMetadata.Plan(trackCount: 2);
            _tagger.ThrowOnApply = true;

            var result = await CreateDownloader().DownloadAsync(plan, CreateRequest());

            // The audio is already on disk and valid; a tag write failure must not discard it.
            result.DownloadedTracks.Should().Be(2);
            result.FailedTracks.Should().Be(0);
        }

        [Test]
        public async Task Does_not_look_up_lyrics_unless_asked()
        {
            var plan = TestMetadata.Plan(trackCount: 3);

            await CreateDownloader().DownloadAsync(plan, CreateRequest(useLrcLib: false));

            _lyrics.Requests.Should().Be(0);
        }

        [Test]
        public async Task Writes_an_lrc_file_when_synced_lyrics_are_requested_and_available()
        {
            var plan = TestMetadata.Plan(trackCount: 1);
            _lyrics.Result = new TrackLyrics("plain words", "[00:01.00] synced words");

            var result = await CreateDownloader().DownloadAsync(
                plan,
                CreateRequest(saveSyncedLyrics: true, useLrcLib: true));

            var lrc = Path.Combine(result.OutputDirectory, "01 - 01 - Track 1.lrc");
            File.Exists(lrc).Should().BeTrue();
            (await File.ReadAllTextAsync(lrc)).Should().Be("[00:01.00] synced words");
        }

        [Test]
        public async Task Does_not_write_an_lrc_file_when_no_synced_lyrics_exist()
        {
            var plan = TestMetadata.Plan(trackCount: 1);
            _lyrics.Result = new TrackLyrics("plain only", null);

            var result = await CreateDownloader().DownloadAsync(
                plan,
                CreateRequest(saveSyncedLyrics: true, useLrcLib: true));

            Directory.GetFiles(result.OutputDirectory, "*.lrc").Should().BeEmpty();
        }

        [Test]
        public void Cancellation_propagates_out_of_the_download()
        {
            var plan = TestMetadata.Plan(trackCount: 3);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = () => CreateDownloader()
                .DownloadAsync(plan, CreateRequest(), null, cts.Token);

            act.Should().Throw<OperationCanceledException>();
        }

        [Test]
        public void Rejects_a_null_plan_or_request()
        {
            var downloader = CreateDownloader();

            downloader.Invoking(d => d.DownloadAsync(null!, CreateRequest()).GetAwaiter().GetResult())
                .Should().Throw<ArgumentNullException>();

            downloader.Invoking(d => d.DownloadAsync(TestMetadata.Plan(), null!).GetAwaiter().GetResult())
                .Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void A_download_request_requires_a_path()
        {
            Action act = () => new AlbumDownloadRequest("  ", AudioQuality.MP3320, false, false);

            act.Should().Throw<ArgumentException>();
        }

        private sealed class RecordingTagger : ITrackTagger
        {
            internal System.Collections.Concurrent.ConcurrentBag<TagCall> Calls { get; } = new();

            internal bool ThrowOnApply { get; set; }

            public void ApplyTags(
                string filePath,
                QobuzAlbumMetadata album,
                QobuzTrackMetadata track,
                byte[]? coverArt,
                string? lyrics)
            {
                if (ThrowOnApply)
                {
                    throw new InvalidOperationException("simulated tagging failure");
                }

                Calls.Add(new TagCall(filePath, album, track, coverArt, lyrics));
            }

            internal sealed record TagCall(
                string FilePath,
                QobuzAlbumMetadata Album,
                QobuzTrackMetadata Track,
                byte[]? CoverArt,
                string? Lyrics);
        }

        private sealed class StubLyricProvider : ILyricProvider
        {
            private int _requests;

            internal TrackLyrics Result { get; set; } = TrackLyrics.None;

            internal int Requests => Volatile.Read(ref _requests);

            public Task<TrackLyrics> GetLyricsAsync(
                QobuzAlbumMetadata album,
                QobuzTrackMetadata track,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _requests);
                return Task.FromResult(Result);
            }
        }

        private sealed class RecordingObserver : IAlbumDownloadObserver
        {
            private long _totalBytes;

            internal System.Collections.Generic.List<string> OutputDirectories { get; } = new();

            internal System.Collections.Concurrent.ConcurrentBag<QobuzTrackMetadata> CompletedTracks { get; } = new();

            internal System.Collections.Concurrent.ConcurrentBag<QobuzTrackMetadata> FailedTracks { get; } = new();

            internal long TotalBytes => Interlocked.Read(ref _totalBytes);

            public void OnOutputDirectoryResolved(string outputDirectory)
            {
                lock (OutputDirectories)
                {
                    OutputDirectories.Add(outputDirectory);
                }
            }

            public void OnBytesTransferred(long additionalBytes) =>
                Interlocked.Add(ref _totalBytes, additionalBytes);

            public void OnTrackCompleted(QobuzTrackMetadata track) => CompletedTracks.Add(track);

            public void OnTrackFailed(QobuzTrackMetadata track, Exception exception) =>
                FailedTracks.Add(track);
        }
    }
}
