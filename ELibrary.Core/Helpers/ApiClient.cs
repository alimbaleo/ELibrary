using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
using static Newtonsoft.Json.JsonConvert;

namespace ELibrary.Core.Helpers
{
    public interface IApiClient
    {
        Task<T> GetAsync<T>(string url);
        Task<T> PostAsync<T>(string url, object data, Dictionary<string, string> headers = null);
    }

    public class ApiClient : IApiClient
    {
        private readonly HttpClient _httpClient;

        public ApiClient()
        {
            HttpClientHandler httpClientHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (source, certificate, chain, sslPolicyError) =>
                {
                    return true;
                }
            };
            _httpClient = new HttpClient(httpClientHandler)
            {
                // Fail with a clear timeout instead of hanging for HttpClient's default 100s when a provider is slow.
                Timeout = TimeSpan.FromSeconds(45)
            };

        }

        public async Task<T> GetAsync<T>(string url)
        {
            // Exceptions are left to propagate unchanged so callers can see the HTTP status code
            // (HttpRequestException.StatusCode) or the timeout (TaskCanceledException).
            var resp = await _httpClient.GetAsync(url);
            var responseString = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)resp.StatusCode} ({resp.ReasonPhrase}). Body: {Truncate(responseString, 500)}",
                    null,
                    resp.StatusCode);

            if (typeof(T) == typeof(string))
                return (T)(object)responseString;

            if (string.IsNullOrWhiteSpace(responseString) || responseString.Trim() == "[]")
                return default!;

            // Not an array: Direct deserialize to T
            var token = JToken.Parse(responseString);
            if (token.Type != JTokenType.Array)
                return DeserializeObject<T>(responseString);

            // For DOA Books - return format is different
            // JSON is an array: Check if T is a List<U>
            var tType = typeof(T);
            if (tType.IsGenericType && tType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = tType.GetGenericArguments()[0];
                var listType = typeof(List<>).MakeGenericType(elementType);

                // Deserialize array to List<U>, then cast to T
                var arrayObj = JsonConvert.DeserializeObject(responseString, listType, GetJsonSettings());
                return (T)Convert.ChangeType(arrayObj, tType);
            }

            // T isn't a list (e.g., single object or array): Fallback to direct deserialize
            return DeserializeObject<T>(responseString);
        }

        private static string Truncate(string value, int maxLength) =>
            string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength] + "...";

        
        // Helper for consistent settings (handles dates, nulls)
        private static JsonSerializerSettings GetJsonSettings()
        {
            return new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                DateParseHandling = DateParseHandling.DateTimeOffset,  // For "2024-03-30 02:53:16.821"
                DateFormatHandling = DateFormatHandling.IsoDateFormat
            };
        }

        private static T DeserializeObject<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, GetJsonSettings());
        }

        public async Task<T> PostAsync<T>(string url, object data, Dictionary<string, string> headers = null)
        {
            string error = null;
            try
            {
                _httpClient.BaseAddress = new Uri(url);
                var content = CreateHttpContent(data);

                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (headers != null && headers.Count > 0)
                {
                    foreach (var header in headers)
                    {
                        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
                var resp = await _httpClient.PostAsync(_httpClient.BaseAddress, content);
                var responseString = await resp.Content.ReadAsStringAsync();
                error = responseString;
                resp.EnsureSuccessStatusCode();
                return DeserializeObject<T>(responseString);
            }
            catch (Exception e)
            {
                throw new Exception(error ?? e.Message);
            }
        }

        private static HttpContent CreateHttpContent(object content)
        {
            DefaultContractResolver contractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy()
            };

            var json = SerializeObject(content, new JsonSerializerSettings
            {
                ContractResolver = contractResolver,
                Formatting = Newtonsoft.Json.Formatting.Indented
            });
            return new StringContent(json, Encoding.UTF8, "application/json");
        }
    }
}
