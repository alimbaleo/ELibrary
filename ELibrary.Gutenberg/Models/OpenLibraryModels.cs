using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ELibrary.Infrastructure.Models
{
    public class OpenLibrarySearchResponse
    {
        [JsonProperty("numFound")]
        public int NumFound { get; set; }

        [JsonProperty("docs")]
        public List<OpenLibraryDoc> Docs { get; set; } = new();
    }

    public class OpenLibraryDoc
    {
        [JsonProperty("key")]
        public string? Key { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("author_name")]
        public List<string>? AuthorName { get; set; }

        [JsonProperty("first_sentence")]
        public List<string>? FirstSentence { get; set; }

        [JsonProperty("cover_i")]
        public long? CoverId { get; set; }

        // Internet Archive identifiers; the first one is used as the book id
        [JsonProperty("ia")]
        public List<string>? Ia { get; set; }
    }

    public class ArchiveMetadataResponse
    {
        [JsonProperty("result")]
        public ArchiveItemMetadata? Result { get; set; }
    }

    public class ArchiveItemMetadata
    {
        [JsonProperty("identifier")]
        public string? Identifier { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        // archive.org returns a string for one creator and an array for several
        [JsonProperty("creator")]
        public JToken? Creator { get; set; }

        [JsonProperty("description")]
        public JToken? Description { get; set; }

        public List<string> Creators() => ToStrings(Creator);

        public string? DescriptionText() => ToStrings(Description).FirstOrDefault();

        private static List<string> ToStrings(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return new List<string>();
            if (token.Type == JTokenType.Array)
                return token.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return new List<string> { token.ToString() };
        }
    }
}
