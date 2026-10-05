using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    public class Qobuz : DownloadClientBase<QobuzSettings>
    {
        private readonly IQobuzProxy _proxy;

        public Qobuz(
            IQobuzProxy proxy,
            IConfigService configService,
            IDiskProvider diskProvider,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _proxy = proxy;
        }

        public override string Protocol => nameof(QobuzDownloadProtocol);

        public override string Name => "Qobuz";

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var queue = _proxy.GetQueue();

            foreach (var item in queue)
            {
                item.DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
            }

            return queue;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
            {
                DeleteItemData(item);
            }

            _proxy.RemoveFromQueue(item.DownloadId);
        }

        /// <summary>
        /// Queues an album for download.
        /// </summary>
        /// <remarks>
        /// <paramref name="indexer"/> is passed through because Qobuz credentials live in the
        /// indexer's settings, not this client's. The previous implementation ignored it and
        /// depended on a process-wide singleton that only the indexer's search path ever
        /// initialised.
        /// </remarks>
        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            // Snapshot settings at the entry point: Lidarr reassigns Definition on this shared
            // singleton, so a later read could belong to a different configured client.
            return _proxy.Download(remoteAlbum, Settings, indexer);
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = new List<OsPath> { new OsPath(Settings.DownloadPath) }
            };
        }

        /// <summary>
        /// Verifies the configured download path is usable.
        /// </summary>
        /// <remarks>
        /// Previously a no-op with the comment "we don't really need to do anything here", so
        /// saving an unwritable or non-existent download path reported success and only failed
        /// later, mid-download. Credentials are not checked here because they belong to the
        /// indexer, which tests them itself.
        /// </remarks>
        protected override void Test(List<ValidationFailure> failures)
        {
            var settings = Settings;

            if (string.IsNullOrWhiteSpace(settings.DownloadPath))
            {
                failures.Add(new ValidationFailure(
                    nameof(settings.DownloadPath),
                    "A download path is required."));

                return;
            }

            failures.AddIfNotNull(TestFolder(settings.DownloadPath, nameof(settings.DownloadPath)));
        }
    }
}
