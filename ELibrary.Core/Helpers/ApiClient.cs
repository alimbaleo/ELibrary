using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ELibrary.Core.Helpers
{
    public interface IApiClient
    {
        Task<T?> GetAsync<T>(string url);
        Task<string> GetStringAsync(string url);
        Task<byte[]> GetBytesAsync(string url);
        Task<T?> PostAsync<T>(string url, object body, IDictionary<string, string>? headers = null);
    }

    public class ApiClient : IApiClient
    {
        private const int LoggedBodyLength = 500;
        private static readonly Regex ApiKeyPattern = new(@"([?&]key=)[^&]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTimeOffset,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() }
        };

        private readonly HttpClient _httpClient;
        private readonly ILogger<ApiClient> _logger;

        public ApiClient(HttpClient httpClient, ILogger<ApiClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<T?> GetAsync<T>(string url)
        {
            var body = await GetStringAsync(url);
            return Deserialize<T>(body, url);
        }

        public async Task<string> GetStringAsync(string url)
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<byte[]> GetBytesAsync(string url)
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task<T?> PostAsync<T>(string url, object body, IDictionary<string, string>? headers = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonConvert.SerializeObject(body, JsonSettings), Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (headers != null)
            {
                foreach (var (name, value) in headers)
                    request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await SendAsync(request);
            return Deserialize<T>(await response.Content.ReadAsStringAsync(), url);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            var method = request.Method;
            var url = Redact(request.RequestUri?.ToString());
            var stopwatch = Stopwatch.StartNew();

            HttpResponseMessage response;
            using (request)
            {
                try
                {
                    response = await _httpClient.SendAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Method} {Url} failed after {ElapsedMs}ms", method, url, stopwatch.ElapsedMilliseconds);
                    throw;
                }
            }

            var status = response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("{Method} {Url} -> {StatusCode} in {ElapsedMs}ms", method, url, (int)status, stopwatch.ElapsedMilliseconds);
                return response;
            }

            var reason = response.ReasonPhrase;
            var errorBody = await response.Content.ReadAsStringAsync();
            response.Dispose();

            _logger.LogWarning("{Method} {Url} -> {StatusCode} in {ElapsedMs}ms: {Body}",
                method, url, (int)status, stopwatch.ElapsedMilliseconds, Util.TruncateText(errorBody, LoggedBodyLength));

            throw new HttpRequestException($"{method} {url} returned {(int)status} ({reason})", null, status);
        }

        private T? Deserialize<T>(string body, string url)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("Empty response body from {Url}", Redact(url));
                return default;
            }

            if (typeof(T) == typeof(string)) return (T)(object)body;

            try
            {
                return JsonConvert.DeserializeObject<T>(body, JsonSettings);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Could not deserialize response from {Url} as {Type}: {Body}",
                    Redact(url), typeof(T).Name, Util.TruncateText(body, LoggedBodyLength));
                throw;
            }
        }

        private static string Redact(string? url)
            => url == null ? string.Empty : ApiKeyPattern.Replace(url, "$1***");
    }
}
