import pytest

from greetings.greeting import MAX_NAME_LENGTH, greeting_for


def test_a_usable_name_is_greeted():
    assert greeting_for("  Ada Lovelace ") == "Hello, Ada Lovelace!"


@pytest.mark.parametrize("name", [None, "", "   ", "<script>", "name; drop table"])
def test_an_unusable_name_is_refused(name):
    assert greeting_for(name) is None


def test_a_name_over_the_limit_is_refused():
    assert greeting_for("a" * (MAX_NAME_LENGTH + 1)) is None
    assert greeting_for("a" * MAX_NAME_LENGTH) is not None
