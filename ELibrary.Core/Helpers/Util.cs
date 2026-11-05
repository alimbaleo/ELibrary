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
    }
}
