# -----------------------------------------------------------------------------
# Vigil — dedicated server container.
#
# Expects a Linux server build to already exist at ./Build/LinuxServer. The build
# happens in CI (see .github/workflows/ci.yml), not here: a Unity build image is
# ~10 GB and requires a licence, so baking the engine into the runtime image would
# make every deploy enormous and every pull slow.
#
# Result is a ~150 MB image containing only the compiled server.
# -----------------------------------------------------------------------------

FROM ubuntu:22.04

# Unity's Linux player links against these. The set is deliberately minimal —
# every extra package is attack surface on a process that accepts traffic from
# the public internet.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        libc6 \
        libstdc++6 \
        libgcc-s1 \
    && rm -rf /var/lib/apt/lists/*

# Never run a network-facing game server as root.
RUN useradd --system --create-home --shell /usr/sbin/nologin vigil

WORKDIR /app

COPY --chown=vigil:vigil Build/LinuxServer/ /app/

RUN chmod +x /app/VigilServer

USER vigil

# UDP, not TCP. Unity Transport is a UDP protocol; exposing 7777/tcp is a very
# common copy-paste error that produces a server nobody can reach.
EXPOSE 7777/udp

ENV VIGIL_SERVER=1 \
    VIGIL_PORT=7777 \
    VIGIL_BIND=0.0.0.0 \
    VIGIL_MAX_PLAYERS=4 \
    VIGIL_LEVEL=Level_Facility \
    VIGIL_LOG=Session,Net,Core

# -logFile /dev/stdout is required: without it Unity writes to a file inside the
# container and `docker logs` shows nothing at all.
ENTRYPOINT ["/app/VigilServer", "-batchmode", "-nographics", "-logFile", "/dev/stdout"]
