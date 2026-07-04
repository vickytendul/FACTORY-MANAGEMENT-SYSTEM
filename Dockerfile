FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore "FACTORY MANAGEMENT SYSTEM.csproj"
RUN dotnet publish "FACTORY MANAGEMENT SYSTEM.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:10000

EXPOSE 10000

ENTRYPOINT ["dotnet", "FACTORY MANAGEMENT SYSTEM.dll"]