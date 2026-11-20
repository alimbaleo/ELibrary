# ------------------------------------------------------------------
# Dockerfile (put this at the repo root)
# ------------------------------------------------------------------


# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# copy only the project file first (enables cache for restore)
COPY ELibrary/ELibrary.csproj ELibrary/

# restore the project
RUN dotnet restore ELibrary/ELibrary.csproj

# copy entire repo and publish the specific project
COPY . .
RUN dotnet publish ELibrary/ELibrary.csproj -c Release -o /app/out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# copy published output
COPY --from=build /app/out .

# ensure the app listens on the expected port
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# run the published DLL (ensure this name matches your csproj output)
ENTRYPOINT ["dotnet", "ELibrary.dll"]
