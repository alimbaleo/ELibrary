
using ELibrary.Core.Helpers;
using ELibrary.Core.Interfaces;
using ELibrary.Core.Services;
using ELibrary.Filters;
using ELibrary.Gutenberg.Services;
using ELibrary.Infrastructure.Doab;
using ELibrary.Infrastructure.Services;
using ELibrary.Infrastruture.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;

namespace ELibrary
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddMemoryCache();

            builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
            builder.Services.AddScoped<IApiClient, ApiClient>();
            builder.Services.AddScoped<ILibraryService, GutendexService>();
            builder.Services.AddScoped<ILibraryService, GoogleService>();
            builder.Services.AddScoped<ILibraryService, DoaBooksService>();

            // DOAB: served from a local catalogue (DOAB's REST API is allow-list only), kept in sync in the background.
            builder.Services.AddSingleton<IDoabCatalogueStore, DoabCatalogueStore>();
            builder.Services.AddHttpClient<DoabClient>();
            builder.Services.AddHostedService<DoabCatalogueSyncService>();

            builder.Services.AddScoped<LibraryCoordinator>();

            builder.Services.AddAuthentication("BasicAuthentication")
                   .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>("BasicAuthentication", null);

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
