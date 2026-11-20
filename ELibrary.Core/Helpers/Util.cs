using System.Net;
using System.Text.RegularExpressions;

namespace ELibrary.Core.Helpers
{
    public static class Util
    {
        public static string SanitizeForComparison(this string? input, bool discardInitials = false)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var alphanumeric = Regex.Replace(input, "[^a-zA-Z0-9 ]", "");
            if (!discardInitials) return alphanumeric;

            var withoutInitials = Regex.Replace(alphanumeric, @"\b[a-zA-Z]\b\s*", "");
            return Regex.Replace(withoutInitials, @"\s+", "");
        }

        public static string TruncateText(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= maxLength) return text;

            var truncated = text[..maxLength];
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > 0) truncated = truncated[..lastSpace];
            return truncated + "...";
        }

        public static string StripHtml(string? text)
            => string.IsNullOrEmpty(text) ? string.Empty : Regex.Replace(text, "<[^>]+>", " ").Trim();

        public static string PageLink(string baseUrl, string route, string parameter, string term, int page)
            => $"{baseUrl}/{route}?{parameter}={Uri.EscapeDataString(term)}&Page={page}";

        public static string IframeViewerHtml(string? src, string title = "Book Viewer")
        {
            return $@"<!DOCTYPE html>
<html>
<head>
    <title>{WebUtility.HtmlEncode(title)}</title>
</head>
<body style='margin:0;padding:0;'>
    <iframe src='{src}' style='width:100%;height:100vh;border:none;' allowfullscreen></iframe>
</body>
</html>";
        }

        // For hosts that put a browser check (e.g. Anubis) in front of their files: the check can't
        // complete inside another site's frame, so the book opens in its own tab instead.
        public static string NewTabViewerHtml(string url, string title = "Book Viewer")
        {
            var safeTitle = WebUtility.HtmlEncode(title);
            var safeUrl = WebUtility.HtmlEncode(url);
            return $@"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <title>{safeTitle}</title>
</head>
<body style='margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;font-family:system-ui,sans-serif;background:#f6f6f6;color:#222;'>
    <main style='max-width:32rem;padding:2rem 1.5rem;text-align:center;'>
        <h1 style='font-size:1.25rem;line-height:1.4;margin:0 0 1rem;'>{safeTitle}</h1>
        <p style='margin:0 0 1.5rem;line-height:1.5;color:#555;'>This book opens in a new tab. The library that hosts it checks that you're a person first, which takes a few seconds.</p>
        <a href='{safeUrl}' target='_blank' rel='noopener' style='display:inline-block;padding:.75rem 1.5rem;border-radius:.5rem;background:#1a56db;color:#fff;text-decoration:none;font-weight:600;'>Open book</a>
    </main>
</body>
</html>";
        }
    }
}
