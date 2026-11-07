#!/usr/bin/env python3
"""Checks that the documentation still matches the repository. Standard library only; exits 1 on any problem.

  python3 tools/check-docs.py

What it checks:
  * every relative link and image in every Markdown file resolves, and every #anchor exists in the target file
  * each docs/diagrams/*.dot has a rendered docs/images/*.svg, and the other way round
  * every key of infra/.env.example, and every environment variable the services read, is in docs/configuration.md
  * every key of .factory/project.json that the controller reads is in docs/project-setup.md
  * every agent role file is in docs/configuration.md, and every result status the controller can return is in docs/workflows.md
  * every label the router or work mode looks at is in docs/workflows.md
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
problems: list[str] = []
checked: dict[str, int] = {}


def fail(message: str) -> None:
    problems.append(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def markdown_files() -> list[Path]:
    """Tracked and new Markdown files; without git (a bare CI image) every .md file outside build and tool folders."""
    try:
        out = subprocess.run(["git", "ls-files", "-co", "--exclude-standard", "*.md"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
        return sorted(ROOT / line for line in out.splitlines() if line and (ROOT / line).exists())
    except (OSError, subprocess.CalledProcessError):
        skipped = {".git", "bin", "obj", "node_modules"}
        return sorted(p for p in ROOT.rglob("*.md") if not skipped.intersection(p.relative_to(ROOT).parts))


def slug(heading: str) -> str:
    """The anchor a renderer such as Forgejo or GitHub makes from a heading."""
    text = heading.strip().lower().replace("`", "")
    text = re.sub(r"[^\w\- ]", "", text)
    return text.replace(" ", "-")


def anchors(path: Path) -> set[str]:
    found: set[str] = set()
    counts: dict[str, int] = {}
    in_code = False
    for line in read(path).splitlines():
        if line.startswith("```"):
            in_code = not in_code
            continue
        match = re.match(r"^#{1,6}\s+(.*?)\s*#*\s*$", line) if not in_code else None
        if match:
            base = slug(match.group(1))
            number = counts.get(base, 0)
            counts[base] = number + 1
            found.add(base if number == 0 else f"{base}-{number}")
    return found


LINK = re.compile(r"!?\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")


def check_links(files: list[Path]) -> None:
    cache: dict[Path, set[str]] = {}
    for file in files:
        in_code = False
        for number, line in enumerate(read(file).splitlines(), 1):
            if line.startswith("```"):
                in_code = not in_code
                continue
            if in_code:
                continue
            for target in LINK.findall(re.sub(r"`[^`]*`", "", line)):
                if re.match(r"^[a-z][a-z0-9+.-]*:", target):
                    continue
                where = f"{file.relative_to(ROOT)}:{number}"
                path_part, _, anchor = target.partition("#")
                resolved = file if path_part == "" else (file.parent / path_part).resolve()
                if path_part and not resolved.exists():
                    fail(f"{where}: broken link {target}")
                    continue
                if anchor and resolved.suffix == ".md":
                    if resolved not in cache:
                        cache[resolved] = anchors(resolved)
                    if anchor not in cache[resolved]:
                        fail(f"{where}: no heading for #{anchor} in {resolved.relative_to(ROOT)}")


def check_diagrams() -> None:
    sources = {p.stem for p in (ROOT / "docs/diagrams").glob("*.dot")}
    images = {p.stem for p in (ROOT / "docs/images").glob("*.svg")}
    for name in sorted(sources - images):
        fail(f"docs/diagrams/{name}.dot has no docs/images/{name}.svg (run sh docs/diagrams/render.sh)")
    for name in sorted(images - sources):
        fail(f"docs/images/{name}.svg has no docs/diagrams/{name}.dot source")


def check_settings() -> None:
    configuration = read(ROOT / "docs/configuration.md")
    keys = set(re.findall(r"^#?\s*([A-Z][A-Z0-9_]{2,})=", read(ROOT / "infra/.env.example"), re.M))
    checked["env keys"] = len(keys)
    for key in sorted(keys):
        if key not in configuration:
            fail(f"infra/.env.example: {key} is not in docs/configuration.md")

    read_vars: set[str] = set()
    pattern = re.compile(r'(?:configuration\["|NonEmpty\(configuration, "|GetValue(?:<[a-z]+>)?\("|GetEnvironmentVariable\(")([A-Z][A-Z0-9_]{2,})"')
    for source in (ROOT / "services").glob("*/src/**/*.cs"):
        if "/obj/" in str(source) or "/bin/" in str(source):
            continue
        read_vars.update(pattern.findall(read(source)))
    checked["service variables"] = len(read_vars)
    for key in sorted(read_vars):
        if key not in configuration:
            fail(f"services read {key}, which is not in docs/configuration.md")


def check_project_keys() -> None:
    guide = read(ROOT / "docs/project-setup.md")
    source = read(ROOT / "services/controller/src/DevFoundry.Controller/Project/ProjectConfig.cs")
    project_keys = sorted(set(re.findall(r'TryGetProperty\("([a-z_]+)"', source)))
    checked["project.json keys"] = len(project_keys)
    for key in project_keys:
        if f"`{key}`" not in guide and f"`{key}\"" not in guide and f'"{key}"' not in guide:
            fail(f".factory/project.json key \"{key}\" (ProjectConfig.cs) is not in docs/project-setup.md")


def check_roles() -> None:
    configuration = read(ROOT / "docs/configuration.md")
    roles = sorted((ROOT / "agents/agents").glob("*.md"))
    checked["roles"] = len(roles)
    for role in roles:
        if f"`{role.stem}`" not in configuration:
            fail(f"agents/agents/{role.name}: role `{role.stem}` is not in the table in docs/configuration.md")


def check_embedded_copies() -> None:
    """A shared text (the engineering floor, the security checklist) is copied into role files; the copies must equal the source."""
    copies = [
        ("engineering-floor", "agents/standards/engineering-floor.md", ("reviewer", "security")),
        ("security-checklist", "agents/standards/security-checklist.md", ("security",)),
    ]
    count = 0
    for marker, source, roles in copies:
        text_of_source = read(ROOT / source).strip()
        for role in roles:
            text = read(ROOT / f"agents/agents/{role}.md")
            match = re.search(rf"<!-- {marker}:start[^>]*-->\n(.*?)\n<!-- {marker}:end -->", text, re.S)
            if not match or not match.group(1).endswith(text_of_source):
                fail(f"agents/agents/{role}.md must contain the text of {source} between the {marker} markers")
            count += 1
    checked["embedded shared texts"] = count


def check_templates() -> None:
    """Every starter project under templates/ is listed, names an image the factory offers, and its Dockerfile example starts from one of them."""
    compose = read(ROOT / "infra/compose.yaml")
    pairs = re.search(r"SANDBOX_IMAGES:\s*(\S+)", compose)
    if not pairs:
        fail("infra/compose.yaml has no SANDBOX_IMAGES to check the templates against")
        return
    images = dict(pair.split("=", 1) for pair in pairs.group(1).split(";") if "=" in pair)
    index = read(ROOT / "templates/README.md")
    names = sorted(path.name for path in (ROOT / "templates").iterdir() if path.is_dir())
    checked["templates"] = len(names)
    for name in names:
        base = ROOT / "templates" / name
        if f"[{name}]({name}/)" not in index:
            fail(f"templates/{name} is not listed in templates/README.md")
        settings = base / ".factory/project.json"
        if not settings.exists():
            fail(f"templates/{name} has no .factory/project.json")
            continue
        try:
            alias = json.loads(read(settings)).get("image")
        except json.JSONDecodeError as error:
            fail(f"templates/{name}/.factory/project.json is not valid JSON: {error}")
            continue
        if alias not in images:
            fail(f"templates/{name}: image \"{alias}\" is not one of the factory's images ({', '.join(sorted(images))})")
        example = base / ".factory/Dockerfile.example"
        if example.exists():
            instructions = [line.strip() for line in read(example).splitlines() if line.strip() and not line.strip().startswith("#")]
            if not instructions or instructions[0].split()[:1] != ["FROM"] or instructions[0].split()[1:] not in ([image] for image in images.values()):
                fail(f"templates/{name}/.factory/Dockerfile.example must start with FROM and one of: {', '.join(sorted(images.values()))}")


def check_statuses_and_labels() -> None:
    workflows = read(ROOT / "docs/workflows.md")
    statuses: set[str] = set()
    statuses.update(re.findall(r'const string \w+ = "([a-z_]+)";', read(ROOT / "services/controller/src/DevFoundry.Controller/Workflows/Statuses.cs")))
    checked["statuses"] = len(statuses)
    for status in sorted(statuses):
        if f"`{status}`" not in workflows:
            fail(f"result status {status} is not in docs/workflows.md")

    labels = set(re.findall(r'(?:FeatureLabel|BugLabel|IncidentLabel|QuickLabel|FullLabel|ForceLabel) = "([a-z-]+)"', "".join(
        read(p) for p in [ROOT / "services/controller/src/DevFoundry.Controller/Workflows/Routing/EventRouter.cs",
                          ROOT / "services/controller/src/DevFoundry.Controller/Workflows/Routing/WorkMode.cs",
                          ROOT / "services/controller/src/DevFoundry.Controller/Agents/Coverage/Triage.cs"])))
    checked["labels"] = len(labels)
    for label in sorted(labels):
        if f"`{label}`" not in workflows:
            fail(f"label {label} is not in docs/workflows.md")


def main() -> int:
    files = markdown_files()
    check_links(files)
    check_diagrams()
    check_settings()
    check_project_keys()
    check_roles()
    check_embedded_copies()
    check_templates()
    check_statuses_and_labels()
    if problems:
        print(f"{len(problems)} documentation problem(s):")
        for problem in problems:
            print(f"  - {problem}")
        return 1
    detail = ", ".join(f"{count} {name}" for name, count in checked.items())
    print(f"documentation ok: {len(files)} Markdown files checked ({detail})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
