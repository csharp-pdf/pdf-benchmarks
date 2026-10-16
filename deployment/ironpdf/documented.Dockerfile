# IronPDF's documented Docker setup (https://ironpdf.com/get-started/ironpdf-docker/, read 2026-10-07): the apt
# package list from the guide, LinuxAndDockerDependenciesAutoConfig = false (and ChromeGpuMode = Disabled, the
# default; both set by the adapter when IRONPDF_DOCKER_DOCUMENTED=1), and the IronPdf.Linux package.
FROM mcr.microsoft.com/dotnet/runtime:8.0
RUN apt-get update && apt-get install -y --no-install-recommends \
      libasound2 libatk1.0-0 libcairo2 libcups2 libdbus-1-3 libdrm2 libexpat1 libfontconfig1 libgbm1 \
      libglib2.0-0 libgtk-3-0 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 \
      libxext6 libxfixes3 libxkbcommon0 libxrandr2 fonts-liberation wget \
    && rm -rf /var/lib/apt/lists/*
ENV IRONPDF_DOCKER_DOCUMENTED=1
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-ironpdf"]
