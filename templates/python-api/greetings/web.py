"""The web layer: a plain WSGI application, so it needs no dependencies and is easy to test."""

import json
from collections.abc import Callable, Iterable
from urllib.parse import unquote
from wsgiref.simple_server import make_server

from greetings.greeting import MAX_NAME_LENGTH, greeting_for

PREFIX = "/api/greetings/"


def application(environ: dict, start_response: Callable) -> Iterable[bytes]:
    """GET /api/greetings/Ada -> 200 {"message": ...}; a name that is not usable -> 400 {"error": ...}."""
    path = environ.get("PATH_INFO", "")
    if environ.get("REQUEST_METHOD") != "GET" or not path.startswith(PREFIX):
        return _reply(start_response, "404 Not Found", {"error": "Not found."})
    message = greeting_for(unquote(path[len(PREFIX):]))
    if message is None:
        error = f"The name must be 1 to {MAX_NAME_LENGTH} letters, digits, spaces, dots or dashes."
        return _reply(start_response, "400 Bad Request", {"error": error})
    return _reply(start_response, "200 OK", {"message": message})


def _reply(start_response: Callable, status: str, body: dict) -> list[bytes]:
    payload = json.dumps(body).encode()
    start_response(status, [("Content-Type", "application/json"), ("Content-Length", str(len(payload)))])
    return [payload]


if __name__ == "__main__":
    with make_server("127.0.0.1", 5080, application) as server:
        print("Listening on http://127.0.0.1:5080/api/greetings/Ada")
        server.serve_forever()
