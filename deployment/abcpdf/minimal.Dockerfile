# The smallest image: the .NET runtime and the published adapter, nothing else.
# ABCpdf creates no trial licence on Linux: the container needs ABCPDF_LICENSE_KEY (deploy.py passes it through).
FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-abcpdf"]
