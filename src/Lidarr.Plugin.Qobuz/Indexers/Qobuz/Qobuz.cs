using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentValidation.Results;
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
        /// ordinary searches return nothing at all.
        /// </remarks>
        /// <remarks>
        /// Note that <see cref="HttpIndexerBase{TSettings}.TestConnection"/> flattens any
        /// exception that is not one of Lidarr's own indexer exception types into that same
        /// generic message, discarding what we threw. So throwing here fixes the search path
        /// and the log, but the <em>settings test</em> message is made useful by the
        /// <see cref="Test"/> override below rather than by this exception.
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

        /// <summary>
        /// Tests the indexer, reporting credential problems in terms the user can act on.
        /// </summary>
        /// <remarks>
        /// The base implementation funnels everything through <c>TestConnection</c>, whose
        /// final catch-all replaces the real reason with "Unable to connect to indexer, check
        /// the log for more details" for any exception that is not one of Lidarr's own indexer
        /// types. Authentication is checked first here so a misconfigured account is reported
        /// as such, against the field that is actually wrong, instead of looking like a network
        /// fault.
        /// </remarks>
        protected override async Task Test(List<ValidationFailure> failures)
        {
            var credentials = Settings.ToCredentials();

            if (!credentials.IsComplete)
            {
                failures.Add(new ValidationFailure(
                    nameof(QobuzIndexerSettings.Email),
                    "Supply either an email and MD5 password, or a user id and auth token. "
                    + "The password must be an MD5 hash of your password, not the password."));

                return;
            }

            try
            {
                // Forces a login, so bad credentials are reported here rather than silently
                // producing empty searches later.
                _sessionProvider.GetSession(credentials);
            }
            catch (QobuzAuthenticationException ex)
            {
                _logger.Warn(ex, "Qobuz authentication failed while testing the indexer.");

                failures.Add(new ValidationFailure(
                    nameof(QobuzIndexerSettings.Email),
                    "Qobuz rejected these credentials: " + ex.Message));

                return;
            }

            await base.Test(failures).ConfigureAwait(false);
        }
    }
}
