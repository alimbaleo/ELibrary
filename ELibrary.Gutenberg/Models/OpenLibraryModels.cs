using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ELibrary.Infrastructure.Models;

/// <summary>Response of https://openlibrary.org/search.json (only the fields we request).</summary>
public class OpenLibrarySearchResponse
{
    [JsonProperty("numFound")] public int NumFound { get; set; }

    [JsonProperty("docs")] public List<OpenLibrarySearchDoc>? Docs { get; set; }
}

/// <summary>A work in search results. Also used for the edition documents nested under <see cref="Editions"/>.</summary>
public class OpenLibrarySearchDoc
{
    /// <summary>"/works/OL66554W" for a work, "/books/OL7173379M" for an edition.</summary>
    [JsonProperty("key")] public string? Key { get; set; }

    [JsonProperty("title")] public string? Title { get; set; }

    [JsonProperty("author_name")] public List<string>? AuthorName { get; set; }

    [JsonProperty("cover_i")] public long? CoverId { get; set; }

    [JsonProperty("first_sentence")] public List<string>? FirstSentence { get; set; }

    /// <summary>"public", "borrowable", "printdisabled" or "no_ebook".</summary>
    [JsonProperty("ebook_access")] public string? EbookAccess { get; set; }

    /// <summary>Internet Archive identifiers of the scans (for a work: of all its editions, readable or not).</summary>
    [JsonProperty("ia")] public List<string>? InternetArchiveIds { get; set; }

    /// <summary>The edition that best matches the query (Open Library returns one per work).</summary>
    [JsonProperty("editions")] public OpenLibraryEditions? Editions { get; set; }
}

public class OpenLibraryEditions
{
    [JsonProperty("docs")] public List<OpenLibrarySearchDoc>? Docs { get; set; }
}

/// <summary>Response of https://openlibrary.org/works/{id}.json (only the fields we use).</summary>
public class OpenLibraryWork
{
    /// <summary>Either a plain string or an object like { "type": "/type/text", "value": "..." }.</summary>
    [JsonProperty("description")] public JToken? Description { get; set; }
}

/// <summary>Response of https://archive.org/advancedsearch.php?output=json (only the fields we use).</summary>
public class ArchiveSearchResponse
{
    [JsonProperty("response")] public ArchiveSearchResult? Response { get; set; }
}

public class ArchiveSearchResult
{
    [JsonProperty("docs")] public List<ArchiveSearchDoc>? Docs { get; set; }
}

public class ArchiveSearchDoc
{
    [JsonProperty("identifier")] public string? Identifier { get; set; }
}
