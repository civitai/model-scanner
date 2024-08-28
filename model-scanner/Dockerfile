FROM debian:bullseye
RUN apt-get update && export DEBIAN_FRONTEND=noninteractive && \
    apt-get install -y curl && \
    apt-get install -y clamav && \
    freshclam && \
    apt-get install -y python3 python3-pip && \
    pip3 install picklescan && \
    pip3 install 'numpy<2' safetensors torch torchsde==0.2.5 --extra-index-url https://download.pytorch.org/whl/cpu

COPY scripts/ /convert/
