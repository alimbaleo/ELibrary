using System.Globalization;
using System.Xml.Linq;

namespace ELibrary.Infrastructure.Doab
{
    public sealed record DoabOaiPage(
        IReadOnlyList<DoabCatalogueEntry> Entries,
        IReadOnlyList<string> DeletedHandles,
        string? ResumptionToken,
        int? CompleteListSize,
        int? Cursor);

    /// <summary>An OAI-PMH protocol error returned by DOAB (e.g. <c>badResumptionToken</c>).</summary>
    public sealed class DoabOaiException(string code, string message) : Exception($"DOAB OAI-PMH error '{code}': {message}")
    {
        public string Code { get; } = code;
    }

    /// <summary>
    /// Parses DOAB OAI-PMH responses (ListRecords / GetRecord) in the DSpace <c>xoai</c> format, which,
    /// unlike <c>oai_dc</c>, includes the item UUID, the download URL and the thumbnail bitstream URL.
    /// </summary>
    public static class DoabXoaiParser
    {
        private static readonly XNamespace Oai = "http://www.openarchives.org/OAI/2.0/";
        private static readonly XNamespace X = "http://www.lyncode.com/xoai";

        public static DoabOaiPage Parse(string xml)
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root ?? throw new InvalidOperationException("Empty OAI-PMH response");

            var error = root.Element(Oai + "error");
            if (error != null)
            {
                var code = (string?)error.Attribute("code");
                if (code is "noRecordsMatch" or "idDoesNotExist")
                    return new DoabOaiPage(Array.Empty<DoabCatalogueEntry>(), Array.Empty<string>(), null, 0, null);
                throw new DoabOaiException(code ?? "unknown", error.Value);
            }

            var entries = new List<DoabCatalogueEntry>();
            var deleted = new List<string>();

            foreach (var record in root.Descendants(Oai + "record"))
            {
                var header = record.Element(Oai + "header");
                var oaiId = header?.Element(Oai + "identifier")?.Value.Trim();

                if ((string?)header?.Attribute("status") == "deleted")
                {
                    var handle = HandleFromOaiIdentifier(oaiId);
                    if (handle != null)
                        deleted.Add(handle);
                    continue;
                }

                var metadata = record.Element(Oai + "metadata")?.Element(X + "metadata");
                if (metadata == null)
                    continue;

                var entry = ToEntry(metadata, oaiId);
                if (entry != null)
                    entries.Add(entry);
            }

            var tokenElement = root.Descendants(Oai + "resumptionToken").FirstOrDefault();
            var token = tokenElement?.Value.Trim();
            int? size = int.TryParse((string?)tokenElement?.Attribute("completeListSize"), out var s) ? s : null;
            int? cursor = int.TryParse((string?)tokenElement?.Attribute("cursor"), out var c) ? c : null;

            return new DoabOaiPage(entries, deleted, string.IsNullOrEmpty(token) ? null : token, size, cursor);
        }

        /// <summary>Strips hyphens/spaces; returns null unless it's a 10- or 13-character ISBN.</summary>
        public static string? NormalizeIsbn(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var isbn = new string(value.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray()).ToUpperInvariant();
            if (isbn.Length is not (10 or 13))
                return null;

            // Placeholder ISBNs such as 9780000000002 are attached to many unrelated books.
            var core = isbn.Length == 13 ? isbn[3..12] : isbn[..9];
            return core.All(c => c == '0') ? null : isbn;
        }

        public static string? HandleFromOaiIdentifier(string? oaiIdentifier)
        {
            // oai:directory.doabooks.org:20.500.12854/62823 -> 20.500.12854/62823
            if (string.IsNullOrEmpty(oaiIdentifier))
                return null;
            var idx = oaiIdentifier.LastIndexOf(':');
            return idx >= 0 && idx < oaiIdentifier.Length - 1 ? oaiIdentifier[(idx + 1)..] : null;
        }

        private static DoabCatalogueEntry? ToEntry(XElement metadata, string? oaiId)
        {
            var others = Elements(metadata, "others").FirstOrDefault();
            var uuid = Field(others, "uuid");
            var title = Values(metadata, "dc", "title").FirstOrDefault();
            if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(title))
                return null;

            var authors = Values(metadata, "dc", "contributor", "author").ToList();
            if (authors.Count == 0)
                authors = Values(metadata, "dc", "contributor", "editor").ToList();

            var downloadUrl =
                metadata.Descendants(X + "field").FirstOrDefault(f => (string?)f.Attribute("name") == "oapenidentifierdownloadUrl")?.Value.Trim()
                ?? Values(metadata, "oapen", "identifier", "downloadUrl").FirstOrDefault();

            var thumbnailUrl = Elements(metadata, "bundles", "bundle")
                .Where(b => Field(b, "name") == "THUMBNAIL")
                .SelectMany(b => Elements(b, "bitstreams", "bitstream"))
                .Select(bs => Field(bs, "url"))
                .FirstOrDefault(u => !string.IsNullOrEmpty(u));

            var lastModified = DateTimeOffset.TryParse(Field(others, "lastModifyDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var lm) ? lm : (DateTimeOffset?)null;

            return new DoabCatalogueEntry
            {
                Uuid = uuid,
                Handle = Field(others, "handle") ?? HandleFromOaiIdentifier(oaiId),
                Title = title,
                Authors = authors,
                Abstract = Values(metadata, "dc", "description", "abstract").FirstOrDefault(),
                Type = Values(metadata, "dc", "type").FirstOrDefault(),
                Subjects = Values(metadata, "dc", "subject", "classification")
                    .Concat(Values(metadata, "dc", "subject", "other"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DownloadUrl = string.IsNullOrEmpty(downloadUrl) ? null : downloadUrl,
                ThumbnailUrl = thumbnailUrl,
                LastModified = lastModified,
                Isbns = Values(metadata, "oapen", "relation", "isbn")
                    .Concat(Values(metadata, "dc", "identifier", "isbn"))
                    .Select(NormalizeIsbn)
                    .Where(i => i != null)
                    .Select(i => i!)
                    .Distinct()
                    .ToList()
            };
        }

        // xoai nests metadata as <element name="dc"><element name="title"><element name="none"><field name="value">.
        private static IEnumerable<XElement> Elements(XElement parent, params string[] path)
        {
            IEnumerable<XElement> current = new[] { parent };
            foreach (var name in path)
                current = current.SelectMany(e => e.Elements(X + "element").Where(c => (string?)c.Attribute("name") == name));
            return current;
        }

        // Values one language level below the path (so dc/title doesn't pick up dc/title/alternative).
        private static IEnumerable<string> Values(XElement metadata, params string[] path) =>
            Elements(metadata, path)
                .Elements(X + "element")
                .Elements(X + "field")
                .Where(f => (string?)f.Attribute("name") == "value")
                .Select(f => f.Value.Trim())
                .Where(v => v.Length > 0);

        private static string? Field(XElement? element, string name)
        {
            var value = element?.Elements(X + "field").FirstOrDefault(f => (string?)f.Attribute("name") == name)?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
