import json

from greetings.web import application


def call(path: str, method: str = "GET"):
    captured = {}

    def start_response(status, headers):
        captured["status"] = status

    body = b"".join(application({"PATH_INFO": path, "REQUEST_METHOD": method}, start_response))
    return captured["status"], json.loads(body)


def test_a_greeting_comes_back_as_json():
    assert call("/api/greetings/Ada") == ("200 OK", {"message": "Hello, Ada!"})


def test_an_unusable_name_is_a_400_with_an_error():
    status, body = call("/api/greetings/%3Cscript%3E")

    assert status == "400 Bad Request"
    assert "must be" in body["error"]


def test_other_routes_and_methods_are_a_404():
    assert call("/nope")[0] == "404 Not Found"
    assert call("/api/greetings/Ada", method="POST")[0] == "404 Not Found"
