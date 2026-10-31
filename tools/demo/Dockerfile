FROM mcr.microsoft.com/playwright:v1.64.1-noble
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY record.mjs ./
# A aceleração é uniforme (2×): o GIF fica curto sem edição por trechos.
CMD node record.mjs \
 && ffmpeg -y -loglevel error -ss 1 -i /out/demo.webm \
    -filter_complex "setpts=PTS/2,fps=10,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" \
    /assets/demo.gif
