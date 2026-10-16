# The smallest image: the .NET runtime and the published adapter, nothing else.
# SelectPdf.Universal documents no extra OS packages for Linux (shipped "Deployment-Docker" docs topic).
FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-selectpdf"]
