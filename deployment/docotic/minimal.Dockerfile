# The smallest image: the .NET runtime and the published adapter, nothing else. Docotic.Pdf's defaults apply: the
# HtmlToPdf add-on downloads its pinned Chromium on the first conversion and runs it with the sandbox on.
# Needs DOCOTIC_LICENSE_KEY (deploy.py passes it through).
FROM mcr.microsoft.com/dotnet/runtime:8.0
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-docotic"]
