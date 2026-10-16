# Bit Miracle's documented Docker setup ("Convert HTML to PDF in a Docker container",
# https://bitmiracle.com/pdf-library/html-pdf/convert-in-azure, read 2026-10-08): install Chromium's dependencies with
# the distribution's chromium package (the guide's Dockerfile installs "chromium" with apt-get) and "disable the
# Chromium sandbox in C# code" - HtmlEngineOptions.UseSandbox = false, set by the adapter when
# DOCOTIC_DOCKER_DOCUMENTED=1. The add-on still downloads and runs its own pinned Chromium build, on the first conversion.
FROM mcr.microsoft.com/dotnet/runtime:8.0
RUN apt-get update && apt-get install -y chromium \
    && rm -rf /var/lib/apt/lists/*
ENV DOCOTIC_DOCKER_DOCUMENTED=1
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-docotic"]
