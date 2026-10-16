# Haukcode.WkHtmlToPdfDotNet's documented Docker setup (README "Docker", https://github.com/HakanL/WkHtmlToPdf-DotNet,
# read 2026-10-16): the README's lines for "debian based linux distros", unchanged, inserted before WORKDIR as it says.
# They install a wkhtmltox 0.12.5 package built for Debian 9 (stretch) and libssl1.1.
FROM mcr.microsoft.com/dotnet/runtime:8.0
RUN apt update
RUN apt install -y libgdiplus
RUN ln -s /usr/lib/libgdiplus.so /lib/x86_64-linux-gnu/libgdiplus.so
RUN apt-get install -y --no-install-recommends zlib1g fontconfig libfreetype6 libx11-6 libxext6 libxrender1 wget gdebi
RUN wget https://github.com/wkhtmltopdf/wkhtmltopdf/releases/download/0.12.5/wkhtmltox_0.12.5-1.stretch_amd64.deb
RUN gdebi --n wkhtmltox_0.12.5-1.stretch_amd64.deb
RUN apt install libssl1.1
RUN ln -s /usr/local/lib/libwkhtmltox.so /usr/lib/libwkhtmltox.so
COPY app/ /app/
WORKDIR /app
ENTRYPOINT ["/app/adapter-wkhtmltopdf"]
