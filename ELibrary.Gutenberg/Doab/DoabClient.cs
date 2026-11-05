using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace ELibrary.Infrastructure.Doab
{
    /// <summary>
    /// Talks to DOAB's public endpoints that are open to everyone: the OAI-PMH feed and thumbnail
    /// bitstreams. (DOAB's REST API is restricted to allow-listed addresses, and its CSV export is cut
    /// off by the server after ~50 MB, so neither can be used to load the catalogue.)
    /// </summary>
    public sealed class DoabClient
    {
        private const string OaiIdentifierPrefix = "oai:directory.doabooks.org:";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

        private readonly HttpClient _http;
        private readonly string _oaiUrl;

        public DoabClient(HttpClient http, IConfiguration config)
        {
            _http = http;
            _http.Timeout = RequestTimeout;
            if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
                _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ELibrary", "1.0"));

            _oaiUrl = config.GetValue<string>("DoaBooks:OaiUrl") ?? "https://directory.doabooks.org/oai/request";
        }

        /// <summary>
        /// One page (100 records) of the catalogue: changed since <paramref name="from"/> (all records when null),
        /// or the page a resumption token from a previous page points to.
        /// </summary>
        public async Task<DoabOaiPage> ListRecordsAsync(DateOnly? from, string? resumptionToken, CancellationToken ct)
        {
            var url = resumptionToken != null
                ? $"{_oaiUrl}?verb=ListRecords&resumptionToken={Uri.EscapeDataString(resumptionToken)}"
                : $"{_oaiUrl}?verb=ListRecords&metadataPrefix=xoai" + (from != null ? $"&from={from:yyyy-MM-dd}" : "");
            return DoabXoaiParser.Parse(await GetStringAsync(url, ct));
        }

        /// <summary>Looks up a single item by handle via OAI-PMH GetRecord.</summary>
        public async Task<DoabCatalogueEntry?> GetRecordAsync(string handle, CancellationToken ct)
        {
            var url = $"{_oaiUrl}?verb=GetRecord&metadataPrefix=xoai&identifier={Uri.EscapeDataString(OaiIdentifierPrefix + handle)}";
            return DoabXoaiParser.Parse(await GetStringAsync(url, ct)).Entries.FirstOrDefault();
        }

        public async Task<(byte[] Bytes, string ContentType)> DownloadAsync(string url, CancellationToken ct)
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return (bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }

        /// <summary>Like <see cref="DownloadAsync"/>, but returns null when the image doesn't exist (404).</summary>
        public async Task<(byte[] Bytes, string ContentType)?> TryDownloadAsync(string url, CancellationToken ct)
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return (bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }

        private async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
}
