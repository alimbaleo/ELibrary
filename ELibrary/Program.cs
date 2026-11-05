using ELibrary.Core.Configuration;
using ELibrary.Core.Helpers;
using ELibrary.Core.Interfaces;
using ELibrary.Core.Services;
using ELibrary.Filters;
using ELibrary.Infrastructure.Configuration;
using ELibrary.Infrastructure.Doab;
using ELibrary.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;

namespace ELibrary
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var configuration = builder.Configuration;

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.Configure<ELibrarySettings>(o => o.BaseUrl = configuration[ELibrarySettings.BaseUrlKey] ?? string.Empty);
            builder.Services.Configure<GutenbergSettings>(configuration.GetSection(GutenbergSettings.Section));
            builder.Services.Configure<GoogleBooksSettings>(configuration.GetSection(GoogleBooksSettings.Section));
            builder.Services.Configure<OpenLibrarySettings>(configuration.GetSection(OpenLibrarySettings.Section));
            builder.Services.Configure<DoaBooksSettings>(configuration.GetSection(DoaBooksSettings.Section));

            builder.Services.AddHttpClient<IApiClient, ApiClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ELibrary/1.0");
            });

            builder.Services.AddScoped<ILibraryService, GutenbergService>();
            builder.Services.AddScoped<ILibraryService, GoogleService>();
            builder.Services.AddScoped<ILibraryService, OpenLibraryService>();
            builder.Services.AddScoped<ILibraryService, DoaBooksService>();

            // DOAB's REST API is allow-list only, so its catalogue is harvested from the public
            // OAI-PMH feed into a local SQLite store and kept current in the background.
            builder.Services.AddSingleton<IDoabCatalogueStore, DoabCatalogueStore>();
            builder.Services.AddHttpClient<DoabClient>();
            builder.Services.AddHostedService<DoabCatalogueSyncService>();

            builder.Services.AddScoped<LibraryCoordinator>();

            builder.Services.AddAuthentication(BasicAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(BasicAuthenticationHandler.SchemeName, null);
            builder.Services.AddAuthorization();

            var app = builder.Build();

            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseHttpsRedirection();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();

            app.Run();
        }
    }
}
