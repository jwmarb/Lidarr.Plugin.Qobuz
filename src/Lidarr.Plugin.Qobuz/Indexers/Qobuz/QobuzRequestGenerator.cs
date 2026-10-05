using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Indexers.Qobuz
{
    /// <summary>
    /// Builds the Qobuz album-search requests that Lidarr's HTTP pipeline executes.
    /// </summary>
    /// <remarks>
    /// The requests are hand-built rather than delegated to the Qobuz library's own
    /// <c>SearchAlbums</c>, because that method performs the HTTP call itself and returns a
    /// parsed result, whereas <see cref="HttpIndexerBase{TSettings}"/> requires an
    /// <see cref="IndexerRequest"/> it can execute, retry and rate-limit on its own terms. The
    /// library offers no "build me the request" primitive.
    /// </remarks>
    public class QobuzRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;
        private const int MaxPages = 15;

        private readonly IQobuzSession _session;
        private readonly Logger _logger;

        public QobuzRequestGenerator(IQobuzSession session, Logger logger)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger = logger;
        }

        /// <summary>
        /// Qobuz has no feed to poll, so RSS is unsupported and this exists only so Lidarr has
        /// something to exercise when testing the indexer's settings.
        /// </summary>
        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            var chain = new IndexerPageableRequestChain();
            chain.Add(GetRequests("never gonna give you up", pages: 1));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}"));
            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));
            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchQuery, int pages = MaxPages)
        {
            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                yield break;
            }

            var trimmedQuery = searchQuery.Trim();

            for (var page = 0; page < pages; page++)
            {
                var parameters = new Dictionary<string, string>
                {
                    ["query"] = trimmedQuery,
                    ["limit"] = PageSize.ToString(),
                    ["offset"] = (page * PageSize).ToString(),
                };

                var url = _session.BuildApiUrl("/album/search", parameters);

                var request = new IndexerRequest(url, HttpAccept.Json);
                request.HttpRequest.Method = System.Net.Http.HttpMethod.Get;

                // Header casing matches what the Qobuz web player sends.
                request.HttpRequest.Headers.Add("X-App-Id", _session.AppId);
                request.HttpRequest.Headers.Add("X-User-Auth-Token", _session.AuthToken);

                yield return request;
            }
        }
    }
}
