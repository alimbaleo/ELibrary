using Newtonsoft.Json;

namespace ELibrary.Infrastructure.Models
{
    public class GoogleBooksResponse
    {
        [JsonProperty("totalItems")]
        public int TotalItems { get; set; }

        [JsonProperty("items")]
        public List<GoogleBookItem>? Items { get; set; }
    }

    public class GoogleBookItem
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("volumeInfo")]
        public GoogleVolumeInfo VolumeInfo { get; set; } = new();

        [JsonProperty("accessInfo")]
        public GoogleAccessInfo? AccessInfo { get; set; }
    }

    public class GoogleVolumeInfo
    {
        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("subtitle")]
        public string? Subtitle { get; set; }

        [JsonProperty("authors")]
        public List<string>? Authors { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("imageLinks")]
        public GoogleImageLinks? ImageLinks { get; set; }
    }

    public class GoogleImageLinks
    {
        [JsonProperty("thumbnail")]
        public string? Thumbnail { get; set; }

        [JsonProperty("small")]
        public string? Small { get; set; }

        [JsonProperty("medium")]
        public string? Medium { get; set; }

        [JsonProperty("large")]
        public string? Large { get; set; }
    }

    public class GoogleAccessInfo
    {
        [JsonProperty("embeddable")]
        public bool Embeddable { get; set; }

        [JsonProperty("viewability")]
        public string? Viewability { get; set; }
    }
}
