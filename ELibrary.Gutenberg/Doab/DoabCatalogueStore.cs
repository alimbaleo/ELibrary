using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ELibrary.Infrastructure.Doab
{
    public enum DoabSearchField
    {
        TitleOrAuthor,
        Subject
    }

    public interface IDoabCatalogueStore
    {
        /// <summary>True once the full catalogue has been imported at least once.</summary>
        bool IsReady { get; }

        int Count();
        DoabCatalogueEntry? Get(string uuid);
        (IReadOnlyList<DoabCatalogueEntry> Items, int Total) Search(string text, DoabSearchField field, int limit, int offset);
        void Upsert(IReadOnlyCollection<DoabCatalogueEntry> entries);
        void DeleteByHandle(string handle);
        void SetThumbnailUrl(string uuid, string url);
        string? GetState(string key);
        void SetState(string key, string value);
    }

    /// <summary>
    /// SQLite-backed copy of the DOAB catalogue with an FTS5 full-text index.
    /// DOAB's REST API only serves allow-listed addresses, so search and lookups are served from
    /// this local copy, which <see cref="DoabCatalogueSyncService"/> keeps up to date.
    /// </summary>
    public sealed partial class DoabCatalogueStore : IDoabCatalogueStore
    {
        public const string BootstrapCompletedKey = "bootstrap_completed_at";
        public const string NextHarvestFromKey = "next_harvest_from";

        private const string Separator = "||";
        private const string Columns =
            "b.uuid, b.handle, b.title, b.authors, b.abstract, b.type, b.subjects, b.download_url, b.thumbnail_url, b.last_modified";

        private readonly string _connectionString;

        public DoabCatalogueStore(IConfiguration config, IHostEnvironment env, ILogger<DoabCatalogueStore> logger)
        {
            var path = config.GetValue<string>("DoaBooks:CatalogueDbPath");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine("data", "doab-catalogue.db");
            if (!Path.IsPathRooted(path))
                path = Path.Combine(env.ContentRootPath, path);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                DefaultTimeout = 30
            }.ToString();

            EnsureSchema();
            logger.LogInformation("DOAB catalogue store at {Path}: {Count} items, ready={Ready}", path, Count(), IsReady);
        }

        public bool IsReady => GetState(BootstrapCompletedKey) != null;

        public int Count()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM books";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public DoabCatalogueEntry? Get(string uuid)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM books b WHERE b.uuid = $uuid";
            cmd.Parameters.AddWithValue("$uuid", uuid.Trim().ToLowerInvariant());
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }

        public (IReadOnlyList<DoabCatalogueEntry> Items, int Total) Search(string text, DoabSearchField field, int limit, int offset)
        {
            var match = BuildMatchExpression(text, field);
            if (match == null)
                return (Array.Empty<DoabCatalogueEntry>(), 0);

            using var conn = Open();

            int total;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT count(*) FROM books_fts WHERE books_fts MATCH $match";
                countCmd.Parameters.AddWithValue("$match", match);
                total = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var items = new List<DoabCatalogueEntry>();
            using (var cmd = conn.CreateCommand())
            {
                // Whole books rank above individual chapters; within each group, best match first.
                cmd.CommandText = $"""
                    SELECT {Columns}
                    FROM books_fts f JOIN books b ON b.rowid = f.rowid
                    WHERE books_fts MATCH $match
                    ORDER BY (b.type = 'chapter'), bm25(books_fts)
                    LIMIT $limit OFFSET $offset
                    """;
                cmd.Parameters.AddWithValue("$match", match);
                cmd.Parameters.AddWithValue("$limit", limit);
                cmd.Parameters.AddWithValue("$offset", offset);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    items.Add(ReadEntry(reader));
            }

            return (items, total);
        }

        public void Upsert(IReadOnlyCollection<DoabCatalogueEntry> entries)
        {
            if (entries.Count == 0)
                return;

            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO books (uuid, handle, title, authors, abstract, type, subjects, download_url, thumbnail_url, last_modified)
                VALUES ($uuid, $handle, $title, $authors, $abstract, $type, $subjects, $download_url, $thumbnail_url, $last_modified)
                ON CONFLICT(uuid) DO UPDATE SET
                    handle = excluded.handle,
                    title = excluded.title,
                    authors = excluded.authors,
                    abstract = excluded.abstract,
                    type = excluded.type,
                    subjects = excluded.subjects,
                    download_url = excluded.download_url,
                    thumbnail_url = COALESCE(excluded.thumbnail_url, books.thumbnail_url),
                    last_modified = COALESCE(excluded.last_modified, books.last_modified)
                """;

            var pUuid = cmd.Parameters.Add("$uuid", SqliteType.Text);
            var pHandle = cmd.Parameters.Add("$handle", SqliteType.Text);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pAuthors = cmd.Parameters.Add("$authors", SqliteType.Text);
            var pAbstract = cmd.Parameters.Add("$abstract", SqliteType.Text);
            var pType = cmd.Parameters.Add("$type", SqliteType.Text);
            var pSubjects = cmd.Parameters.Add("$subjects", SqliteType.Text);
            var pDownload = cmd.Parameters.Add("$download_url", SqliteType.Text);
            var pThumb = cmd.Parameters.Add("$thumbnail_url", SqliteType.Text);
            var pModified = cmd.Parameters.Add("$last_modified", SqliteType.Text);
            cmd.Prepare();

            foreach (var e in entries)
            {
                pUuid.Value = e.Uuid.Trim().ToLowerInvariant();
                pHandle.Value = (object?)e.Handle ?? DBNull.Value;
                pTitle.Value = e.Title;
                pAuthors.Value = Join(e.Authors);
                pAbstract.Value = (object?)e.Abstract ?? DBNull.Value;
                pType.Value = (object?)e.Type ?? DBNull.Value;
                pSubjects.Value = Join(e.Subjects);
                pDownload.Value = (object?)e.DownloadUrl ?? DBNull.Value;
                pThumb.Value = (object?)e.ThumbnailUrl ?? DBNull.Value;
                pModified.Value = (object?)e.LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        public void DeleteByHandle(string handle)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM books WHERE handle = $handle";
            cmd.Parameters.AddWithValue("$handle", handle);
            cmd.ExecuteNonQuery();
        }

        public void SetThumbnailUrl(string uuid, string url)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE books SET thumbnail_url = $url WHERE uuid = $uuid";
            cmd.Parameters.AddWithValue("$url", url);
            cmd.Parameters.AddWithValue("$uuid", uuid.Trim().ToLowerInvariant());
            cmd.ExecuteNonQuery();
        }

        public string? GetState(string key)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM sync_state WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }

        public void SetState(string key, string value)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sync_state (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Turns free text into a safe FTS5 query: every word must match (the last one as a prefix),
        /// restricted to the relevant columns. Returns null when the text has no searchable words.
        /// </summary>
        internal static string? BuildMatchExpression(string? text, DoabSearchField field)
        {
            var words = Word().Matches(text ?? string.Empty).Select(m => m.Value).Take(8).ToList();
            if (words.Count == 0)
                return null;

            var columns = field == DoabSearchField.Subject ? "{subjects}" : "{title authors}";
            return string.Join(" AND ", words.Select((w, i) => $"{columns} : \"{w}\"" + (i == words.Count - 1 ? "*" : "")));
        }

        [GeneratedRegex(@"[\p{L}\p{N}]+")]
        private static partial Regex Word();

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private void EnsureSchema()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS books (
                    uuid TEXT PRIMARY KEY,
                    handle TEXT,
                    title TEXT NOT NULL,
                    authors TEXT,
                    abstract TEXT,
                    type TEXT,
                    subjects TEXT,
                    download_url TEXT,
                    thumbnail_url TEXT,
                    last_modified TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_books_handle ON books (handle);

                CREATE VIRTUAL TABLE IF NOT EXISTS books_fts USING fts5 (
                    title, authors, subjects,
                    content = 'books', content_rowid = 'rowid'
                );

                CREATE TRIGGER IF NOT EXISTS books_ai AFTER INSERT ON books BEGIN
                    INSERT INTO books_fts (rowid, title, authors, subjects) VALUES (new.rowid, new.title, new.authors, new.subjects);
                END;
                CREATE TRIGGER IF NOT EXISTS books_ad AFTER DELETE ON books BEGIN
                    INSERT INTO books_fts (books_fts, rowid, title, authors, subjects) VALUES ('delete', old.rowid, old.title, old.authors, old.subjects);
                END;
                CREATE TRIGGER IF NOT EXISTS books_au AFTER UPDATE ON books BEGIN
                    INSERT INTO books_fts (books_fts, rowid, title, authors, subjects) VALUES ('delete', old.rowid, old.title, old.authors, old.subjects);
                    INSERT INTO books_fts (rowid, title, authors, subjects) VALUES (new.rowid, new.title, new.authors, new.subjects);
                END;

                CREATE TABLE IF NOT EXISTS sync_state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        private static DoabCatalogueEntry ReadEntry(SqliteDataReader r) => new()
        {
            Uuid = r.GetString(0),
            Handle = r.IsDBNull(1) ? null : r.GetString(1),
            Title = r.GetString(2),
            Authors = Split(r.IsDBNull(3) ? null : r.GetString(3)),
            Abstract = r.IsDBNull(4) ? null : r.GetString(4),
            Type = r.IsDBNull(5) ? null : r.GetString(5),
            Subjects = Split(r.IsDBNull(6) ? null : r.GetString(6)),
            DownloadUrl = r.IsDBNull(7) ? null : r.GetString(7),
            ThumbnailUrl = r.IsDBNull(8) ? null : r.GetString(8),
            LastModified = r.IsDBNull(9)
                ? null
                : DateTimeOffset.TryParse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null
        };

        private static object Join(IReadOnlyList<string> values) =>
            values.Count == 0 ? DBNull.Value : string.Join(Separator, values);

        internal static IReadOnlyList<string> Split(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
