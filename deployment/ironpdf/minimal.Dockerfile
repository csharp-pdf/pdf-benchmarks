# The smallest image: the .NET runtime and the published adapter, nothing else. IronPDF's defaults apply,
# including LinuxAndDockerDependenciesAutoConfig, which tries to install missing packages at run time.
FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-ironpdf"]
