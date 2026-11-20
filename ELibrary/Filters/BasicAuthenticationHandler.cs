using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ELibrary.Filters
{
    public class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "BasicAuthentication";

        private readonly List<OAuthUser> _users;

        public BasicAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory loggerFactory,
            UrlEncoder encoder,
            IConfiguration configuration)
            : base(options, loggerFactory, encoder)
        {
            _users = configuration.GetSection("OAuthUsers").Get<List<OAuthUser>>() ?? new List<OAuthUser>();
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
                return Task.FromResult(AuthenticateResult.Fail("Missing Authorization Header"));

            string? username = null;
            try
            {
                var parsed = AuthenticationHeaderValue.Parse(header!);
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter ?? string.Empty)).Split(':', 2);
                username = credentials[0];
                var password = credentials.Length > 1 ? credentials[1] : string.Empty;

                var user = _users.FirstOrDefault(u =>
                    string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase) && u.Password == password);

                if (user == null)
                {
                    Logger.LogWarning("Authentication failed for user {Username} from {RemoteIp}", username, Context.Connection.RemoteIpAddress);
                    return Task.FromResult(AuthenticateResult.Fail("Invalid Username or Password"));
                }

                var identity = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Username),
                    new Claim(ClaimTypes.Name, user.Username)
                }, Scheme.Name);

                Logger.LogDebug("Authenticated user {Username}", user.Username);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Malformed Authorization header for user {Username}", username);
                return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization Header"));
            }
        }
    }
}
