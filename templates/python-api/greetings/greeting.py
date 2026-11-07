"""The one piece of business logic in this demo: build a greeting, or refuse a name that is not usable."""

MAX_NAME_LENGTH = 50


def greeting_for(name: str | None) -> str | None:
    """A greeting for the name, or None if it is empty, too long or has characters that are not allowed."""
    trimmed = (name or "").strip()
    if not trimmed or len(trimmed) > MAX_NAME_LENGTH or not all(_allowed(c) for c in trimmed):
        return None
    return f"Hello, {trimmed}!"


def _allowed(character: str) -> bool:
    return character.isalnum() or character in " .-"
