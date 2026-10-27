FROM python:3.12.16-slim-bookworm AS base
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
COPY pyproject.toml ./
COPY src ./src
RUN pip install --no-cache-dir . \
    && useradd --system --uid 10001 --create-home controller

FROM base AS test
RUN pip install --no-cache-dir ".[test]"
COPY tests ./tests
USER controller
CMD ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider"]

FROM base AS runtime
USER controller
EXPOSE 8000
CMD ["uvicorn", "factory_controller.api:create_app", "--factory", "--host", "0.0.0.0", "--port", "8000"]
