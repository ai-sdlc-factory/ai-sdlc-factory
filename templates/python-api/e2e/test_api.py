"""End-to-end checks: talk to the running application over HTTP.

The factory starts the application (`verify.start`) before these run.
"""

import http.client
import json


def get(path: str) -> tuple[int, dict]:
    connection = http.client.HTTPConnection("127.0.0.1", 5080, timeout=10)
    try:
        connection.request("GET", path)
        response = connection.getresponse()
        return response.status, json.loads(response.read())
    finally:
        connection.close()


def test_a_valid_name_is_greeted():
    status, body = get("/api/greetings/Ada")
    assert status == 200
    assert body == {"message": "Hello, Ada!"}


def test_a_name_that_is_not_usable_is_refused():
    status, body = get("/api/greetings/" + "a" * 51)
    assert status == 400
    assert body["error"]
