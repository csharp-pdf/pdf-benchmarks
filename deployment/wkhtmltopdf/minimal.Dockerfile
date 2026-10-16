# The smallest image: the .NET runtime and the published adapter, nothing else.
FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-wkhtmltopdf"]
