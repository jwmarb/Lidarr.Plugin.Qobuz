using System;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class Qobuz : HttpIndexerBase<QobuzIndexerSettings>
    {
        private readonly IQobuzSessionProvider _sessionProvider;

        public Qobuz(
            IQobuzSessionProvider sessionProvider,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            // The download-client proxy is no longer injected here. It was assigned to a field
            // and never used, coupling the indexer module to the download-client module for
            // nothing.
            _sessionProvider = sessionProvider;
        }

        public override string Name => "Qobuz";

        public override string Protocol => nameof(QobuzDownloadProtocol);

        public override bool SupportsRss => false;

        public override bool SupportsSearch => true;

        public override int PageSize => 100;

        public override TimeSpan RateLimit => TimeSpan.Zero;

        /// <summary>
        /// Builds a request generator, establishing a Qobuz session first.
        /// </summary>
        /// <remarks>
        /// Never returns <see langword="null"/>. Doing so caused the host to throw a
        /// <see cref="NullReferenceException"/> inside its own catch-all, which surfaced to the
        /// user as "Unable to connect to indexer, check the log for more details" and made
        /// ordinary searches return nothing at all. Throwing a described failure instead means
        /// the real reason reaches the log and the UI.
        /// </remarks>
        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            // Snapshot the settings once, at the entry point. Lidarr reassigns Definition on
            // this shared singleton, so later reads can belong to another configured indexer.
            var settings = Settings;
            var credentials = settings.ToCredentials();

            if (!credentials.IsComplete)
            {
                throw new QobuzAuthenticationException(
                    "Qobuz is not configured: supply either an email and MD5 password, or a "
                    + "user id and auth token, in the indexer settings.");
            }

            var session = _sessionProvider.GetSession(credentials);

            return new QobuzRequestGenerator(session, _logger);
        }

        public override IParseIndexerResponse GetParser()
        {
            return new QobuzParser();
        }
    }
}
