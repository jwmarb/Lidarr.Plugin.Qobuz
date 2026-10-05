using System;
using FluentAssertions;
using Lidarr.Plugin.Qobuz.Tests.Fakes;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.Download.Clients.Qobuz.Queue;
using NzbDrone.Plugin.Qobuz.API;

namespace Lidarr.Plugin.Qobuz.Tests
{
    [TestFixture]
    public class DownloadProgressSnapshotTests
    {
        private static DownloadProgressSnapshot Snapshot(
            DownloadItemStatus status = DownloadItemStatus.Downloading,
            long estimatedTotalBytes = 1000,
            long downloadedBytes = 0,
            int completedTracks = 0,
            int failedTracks = 0,
            int totalTracks = 10)
        {
            return new DownloadProgressSnapshot(
                "id",
                "Album",
                "Artist",
                false,
                AudioQuality.FLACLossless,
                status,
                estimatedTotalBytes,
                downloadedBytes,
                completedTracks,
                failedTracks,
                totalTracks,
                null,
                null);
        }

        [Test]
        public void Reports_the_estimate_while_downloading()
        {
            var snapshot = Snapshot(estimatedTotalBytes: 5000, downloadedBytes: 1000);

            snapshot.ReportedTotalBytes.Should().Be(5000);
            snapshot.RemainingBytes.Should().Be(4000);
        }

        [Test]
        public void Reports_the_real_byte_count_once_complete()
        {
            // The estimate derives from uncompressed PCM, so the real FLAC size is smaller.
            var snapshot = Snapshot(
                status: DownloadItemStatus.Completed,
                estimatedTotalBytes: 5000,
                downloadedBytes: 3200);

            snapshot.ReportedTotalBytes.Should().Be(3200);
            snapshot.RemainingBytes.Should().Be(0);
            snapshot.Progress.Should().Be(1d);
        }

        [Test]
        public void Never_reports_negative_remaining_bytes_when_the_estimate_is_beaten()
        {
            // A FLAC that compresses worse than predicted can exceed the estimate mid-download.
            var snapshot = Snapshot(estimatedTotalBytes: 1000, downloadedBytes: 4000);

            snapshot.RemainingBytes.Should().BeGreaterOrEqualTo(0);
            snapshot.ReportedTotalBytes.Should().Be(4000,
                "the reported total must never be smaller than what is already written");
        }

        [Test]
        public void Progress_is_unknown_before_any_bytes_arrive()
        {
            Snapshot(downloadedBytes: 0).Progress.Should().BeNull();
        }

        [Test]
        public void Progress_is_unknown_without_an_estimate()
        {
            Snapshot(estimatedTotalBytes: 0, downloadedBytes: 500).Progress.Should().BeNull();
        }

        [Test]
        public void Progress_is_capped_below_one_until_the_download_finishes()
        {
            // Otherwise a pessimistic estimate shows 100% while tracks are still downloading.
            var snapshot = Snapshot(estimatedTotalBytes: 1000, downloadedBytes: 9999);

            snapshot.Progress.Should().BeApproximately(0.99, 0.0001);
            snapshot.Progress.Should().BeLessThan(1d);
        }

        [Test]
        public void Progress_tracks_the_fraction_of_estimated_bytes()
        {
            Snapshot(estimatedTotalBytes: 1000, downloadedBytes: 250)
                .Progress.Should().BeApproximately(0.25, 0.0001);
        }

        /// <summary>
        /// Warning is this plugin's partial-album outcome and is terminal, so it must behave
        /// like one: no outstanding work, and nothing left in the ETA cache.
        /// </summary>
        [Test]
        public void A_partial_album_is_terminal_for_reporting_purposes()
        {
            var snapshot = Snapshot(
                status: DownloadItemStatus.Warning,
                estimatedTotalBytes: 5000,
                downloadedBytes: 3000,
                completedTracks: 8,
                failedTracks: 2);

            snapshot.Status.Should().Be(DownloadItemStatus.Warning);
            snapshot.CompletedTracks.Should().Be(8);
            snapshot.FailedTracks.Should().Be(2);
        }

        [Test]
        public void A_failed_download_still_reports_what_it_managed()
        {
            var snapshot = Snapshot(
                status: DownloadItemStatus.Failed,
                estimatedTotalBytes: 5000,
                downloadedBytes: 1500,
                completedTracks: 3,
                failedTracks: 7);

            snapshot.Status.Should().Be(DownloadItemStatus.Failed);
            snapshot.CompletedTracks.Should().Be(3);
            snapshot.FailedTracks.Should().Be(7);
            snapshot.DownloadedBytes.Should().Be(1500);
        }
    }

    [TestFixture]
    public class QobuzDownloadJobTests
    {
        private static QobuzDownloadJob CreateJob(long estimatedBytes = 1000, int totalTracks = 3)
        {
            QobuzAlbumReference.TryParse("https://open.qobuz.com/album/a1", out var reference);

            return new QobuzDownloadJob(
                "job-1",
                reference!,
                TestMetadata.Credentials(),
                new AlbumDownloadRequest("/tmp", AudioQuality.MP3320, false, false),
                null!,
                "Album",
                "Artist",
                false,
                estimatedBytes,
                totalTracks);
        }

        [Test]
        public void Starts_queued()
        {
            CreateJob().Snapshot().Status.Should().Be(DownloadItemStatus.Queued);
        }

        [Test]
        public void Transitions_through_downloading_to_completed()
        {
            var job = CreateJob();

            job.MarkDownloading();
            job.Snapshot().Status.Should().Be(DownloadItemStatus.Downloading);

            job.MarkCompleted();
            job.Snapshot().Status.Should().Be(DownloadItemStatus.Completed);
        }

        [Test]
        public void A_failure_carries_its_message_into_the_snapshot()
        {
            var job = CreateJob();

            job.MarkFailed("Qobuz said no");

            var snapshot = job.Snapshot();
            snapshot.Status.Should().Be(DownloadItemStatus.Failed);
            snapshot.Message.Should().Be("Qobuz said no");
        }

        [Test]
        public void A_partial_album_is_a_warning_not_a_failure()
        {
            var job = CreateJob();

            job.MarkWarning("2 of 10 tracks failed");

            job.Snapshot().Status.Should().Be(DownloadItemStatus.Warning);
        }

        [Test]
        public void Accumulates_transferred_bytes()
        {
            var job = CreateJob();

            job.OnBytesTransferred(100);
            job.OnBytesTransferred(250);

            job.Snapshot().DownloadedBytes.Should().Be(350);
        }

        [Test]
        public void Ignores_non_positive_byte_reports()
        {
            var job = CreateJob();

            job.OnBytesTransferred(100);
            job.OnBytesTransferred(0);
            job.OnBytesTransferred(-50);

            job.Snapshot().DownloadedBytes.Should().Be(100);
        }

        /// <summary>
        /// The counters are interlocked precisely so this cannot lose updates; the previous
        /// implementation used plain ++ from up to three concurrent track tasks.
        /// </summary>
        [Test]
        public void Counts_correctly_under_concurrent_updates()
        {
            var job = CreateJob();

            System.Threading.Tasks.Parallel.For(0, 1000, _ =>
            {
                job.OnBytesTransferred(10);
                job.OnTrackCompleted(TestMetadata.Track());
                job.OnTrackFailed(TestMetadata.Track(), new Exception("x"));
            });

            var snapshot = job.Snapshot();
            snapshot.DownloadedBytes.Should().Be(10_000);
            snapshot.CompletedTracks.Should().Be(1000);
            snapshot.FailedTracks.Should().Be(1000);
        }

        [Test]
        public void Refines_its_totals_once_the_album_plan_is_known()
        {
            var job = CreateJob(estimatedBytes: 1000, totalTracks: 0);

            job.RefineTotals(totalTracks: 12, estimatedTotalBytes: 50_000);

            var snapshot = job.Snapshot();
            snapshot.TotalTracks.Should().Be(12);
            snapshot.EstimatedTotalBytes.Should().Be(50_000);
        }

        [Test]
        public void Keeps_the_original_estimate_when_a_refined_one_is_unusable()
        {
            var job = CreateJob(estimatedBytes: 1000);

            job.RefineTotals(totalTracks: 5, estimatedTotalBytes: 0);

            job.Snapshot().EstimatedTotalBytes.Should().Be(1000);
        }

        [Test]
        public void Publishes_the_output_directory()
        {
            var job = CreateJob();

            job.OnOutputDirectoryResolved("/music/Artist/Album");

            job.Snapshot().OutputDirectory.Should().Be("/music/Artist/Album");
        }

        [Test]
        public void Carries_its_immutable_inputs()
        {
            var job = CreateJob();

            job.DownloadId.Should().Be("job-1");
            job.AlbumReference.AlbumId.Should().Be("a1");
            job.Credentials.IsComplete.Should().BeTrue();
            job.Request.Quality.Should().Be(AudioQuality.MP3320);
        }
    }
}
