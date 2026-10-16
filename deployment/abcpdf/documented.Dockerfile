# ABCpdf's documented Linux setup (https://www.websupergoo.com/helppdfnet/source/2-getting_started/6-platforms.htm,
# read 2026-10-08): the apt package list given for Debian 12.7, which is what mcr.microsoft.com/dotnet/runtime:8.0
# is built on, plus the ABCpdf and ABCpdf.ABCChrome146.Linux packages (in the published adapter).
# ABCpdf creates no trial licence on Linux: the container needs ABCPDF_LICENSE_KEY (deploy.py passes it through).
FROM mcr.microsoft.com/dotnet/runtime:8.0
RUN apt-get update && apt-get install -y --no-install-recommends \
      libasound2 libnss3 libcurl4 xserver-xorg-core libcairo2 libatk1.0-0 libatk-bridge2.0-0 libcups2 \
      libxcomposite1 libxdamage1 libxrandr2 libxkbcommon0 libpango-1.0-0 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-abcpdf"]
