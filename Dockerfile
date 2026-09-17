# FoulFilterNet service image: ASP.NET Core runtime, FFmpeg, and the CUDA 13
# user-space libraries whisper.cpp links against. Model weights and all data
# live on the /data volume; nothing large is baked in.
#
# NOT YET VERIFIED: this file was written without a Docker daemon. See the T32
# output section of docs/STATUS.md for the assumptions it rests on.

ARG DOTNET_VERSION=10.0

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Project files first so a source edit does not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/FoulFilterNet.Domain/FoulFilterNet.Domain.csproj               src/FoulFilterNet.Domain/
COPY src/FoulFilterNet.Media/FoulFilterNet.Media.csproj                 src/FoulFilterNet.Media/
COPY src/FoulFilterNet.Transcription/FoulFilterNet.Transcription.csproj src/FoulFilterNet.Transcription/
COPY src/FoulFilterNet.SmartCut/FoulFilterNet.SmartCut.csproj           src/FoulFilterNet.SmartCut/
COPY src/FoulFilterNet.Pipeline/FoulFilterNet.Pipeline.csproj           src/FoulFilterNet.Pipeline/
COPY src/FoulFilterNet.Jobs/FoulFilterNet.Jobs.csproj                   src/FoulFilterNet.Jobs/
COPY src/FoulFilterNet.Web/FoulFilterNet.Web.csproj                     src/FoulFilterNet.Web/
COPY src/FoulFilterNet.Cli/FoulFilterNet.Cli.csproj                     src/FoulFilterNet.Cli/
RUN dotnet restore src/FoulFilterNet.Web/FoulFilterNet.Web.csproj \
 && dotnet restore src/FoulFilterNet.Cli/FoulFilterNet.Cli.csproj

COPY src/ src/

# Web and the CLI publish into one directory: they share every assembly, the
# native runtimes and appsettings.json, so the CLI costs a few hundred KB here
# rather than a second copy of the CUDA runtime.
#
# Whisper.net's runtime packages copy natives for every platform regardless of
# the target, so everything that is not linux-x64 is pruned - the Windows CUDA
# build alone is ~150 MB.
RUN dotnet publish src/FoulFilterNet.Web/FoulFilterNet.Web.csproj -c Release --no-restore -o /app \
 && dotnet publish src/FoulFilterNet.Cli/FoulFilterNet.Cli.csproj -c Release --no-restore -o /app \
 && find /app/runtimes -mindepth 1 -type d \
      \( -name 'win-*' -o -name 'macos-*' -o -name 'osx-*' -o -name 'linux-arm*' \
         -o -name 'linux-musl-*' -o -name 'browser-*' -o -name 'android-*' -o -name 'ios-*' \
         -o -name 'maccatalyst*' -o -name 'tvos-*' \) \
      -prune -exec rm -rf {} +

# ---------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime

# Whisper.net.Runtime.Cuda 1.9.1 is built with the CUDA 13 toolchain. Its
# libggml-cuda-whisper.so has these DT_NEEDED entries (read from the package):
#   libcudart.so.13, libcublas.so.13  -> installed here (libcublas pulls in
#                                        libcublasLt.so.13 from the same package)
#   libcuda.so.1                      -> the host driver, injected by the NVIDIA
#                                        container toolkit, never installed here
#   libgomp.so.1                      -> also needed by the CPU fallback runtime
#   libstdc++.so.6, libm, libgcc_s, libc -> already in the base image
ARG CUDA_PACKAGE_VERSION=13-0
ARG CUDA_LIBRARY_DIRECTORY=/usr/local/cuda-13.0/targets/x86_64-linux/lib

RUN set -eu \
 && . /etc/os-release \
 && case "${ID}${VERSION_ID}" in \
      ubuntu24.04) repo=ubuntu2404 ;; \
      ubuntu22.04) repo=ubuntu2204 ;; \
      debian12)    repo=debian12 ;; \
      *) echo "No NVIDIA CUDA apt repository mapped for ${ID} ${VERSION_ID}" >&2; exit 1 ;; \
    esac \
 && apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && curl -fsSL -o /tmp/cuda-keyring.deb \
      "https://developer.download.nvidia.com/compute/cuda/repos/${repo}/x86_64/cuda-keyring_1.1-1_all.deb" \
 && dpkg -i /tmp/cuda-keyring.deb \
 && rm /tmp/cuda-keyring.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg \
      libgomp1 \
      cuda-cudart-${CUDA_PACKAGE_VERSION} \
      libcublas-${CUDA_PACKAGE_VERSION} \
 && apt-get purge -y --auto-remove curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# DATA_DIR is the legacy name on purpose: a .env that sets it still wins, and
# the code default (a per-user folder) is what native runs use instead.
# The NVIDIA_* variables tell the container toolkit to inject the compute
# driver libraries. The library path is explicit rather than trusting that the
# CUDA packages registered an ld.so.conf entry.
ENV DATA_DIR=/data \
    Transcription__ModelDirectory=/data/models \
    ASPNETCORE_HTTP_PORTS=8000 \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    LD_LIBRARY_PATH=${CUDA_LIBRARY_DIRECTORY}

VOLUME /data
EXPOSE 8000

# The CLI is in the same directory:
#   docker compose exec foulfilter dotnet /app/foulfilter.dll /data/in.mp3 /data/bad_words.txt
ENTRYPOINT ["dotnet", "FoulFilterNet.Web.dll"]
