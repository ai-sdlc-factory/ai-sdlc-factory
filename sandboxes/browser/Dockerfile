# Sandbox image for end-to-end browser tests: the "full" image (.NET SDK, Python, Node.js) plus Chromium and the Playwright test tooling.
# Used by the /qa run for a project's `ui` suite (`verify.image` in .factory/project.json). Large; only projects with browser tests need it.
# It runs as an arbitrary unprivileged user on a read-only root filesystem, started by the sandbox launcher; nothing here is a service.
# Build context is sandboxes/. Build: docker compose --profile images build sandbox-image-browser (build sandbox-image-full first).
# The Playwright version is pinned: a project's own `@playwright/test` must be the same version to find the browser installed here
# (PLAYWRIGHT_BROWSERS_PATH points at it). Chromium runs without its own sandbox (the container is the sandbox); Playwright does that by default.
FROM dev-foundry/sandbox-full:local
USER root
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright PLAYWRIGHT_SKIP_VALIDATE_HOST_REQUIREMENTS=1
RUN npm install --global --no-fund --no-audit playwright@1.52.0 \
    && playwright install --with-deps chromium \
    && chmod -R a+rX /opt/ms-playwright \
    && rm -rf /var/lib/apt/lists/* /root/.npm
