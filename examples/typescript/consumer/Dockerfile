# Build with the repo root as build context:
#   docker build -f examples/typescript/consumer/Dockerfile .
FROM node:20-alpine AS build
WORKDIR /build
COPY sdks/typescript/ ./sdks/typescript/
# The SDK build copies daprmq.proto into the package from here.
COPY server/src/DaprMQ.ApiServer/Protos/ ./server/src/DaprMQ.ApiServer/Protos/
RUN npm --prefix sdks/typescript ci && npm --prefix sdks/typescript run build
COPY examples/typescript/consumer/ ./examples/typescript/consumer/
RUN npm --prefix examples/typescript/consumer install && npm --prefix examples/typescript/consumer run build

# npm's "file:../../../sdks/typescript" dependency resolves to a symlink into this
# build stage's filesystem - it dangles once node_modules is copied into a separate
# runtime stage that never has /build/sdks/typescript. Replace it with the real,
# already-built package contents before that copy happens.
RUN rm -f examples/typescript/consumer/node_modules/@daprmq/client && \
    cp -RL sdks/typescript examples/typescript/consumer/node_modules/@daprmq/client

FROM node:20-alpine
WORKDIR /app

COPY --from=build /build/examples/typescript/consumer/dist ./dist
COPY --from=build /build/examples/typescript/consumer/node_modules ./node_modules
COPY --from=build /build/examples/typescript/consumer/package.json ./package.json
EXPOSE 8080
ENTRYPOINT ["node", "dist/index.js"]
