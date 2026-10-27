FROM python:3.12.16-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /tests
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt \
    && useradd --system --uid 10001 --create-home tester
COPY test_forgejo_delivery.py ./
USER tester
CMD ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider"]
