#!/bin/sh
# Renders docs/diagrams/*.dot to docs/images/*.svg with Graphviz (then finish.py adds the shadows and paper) in a throwaway container.
# Run from anywhere:  sh docs/diagrams/render.sh
# Needs only Docker. Output files belong to you, not root.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
docs=$(dirname "$here")

docker build -q -t ai-sdlc-factory/docs-render "$here" >/dev/null
for source in "$here"/*.dot; do
    name=$(basename "$source" .dot)
    docker run --rm --user "$(id -u):$(id -g)" -e XDG_CACHE_HOME=/tmp -v "$docs:/docs" ai-sdlc-factory/docs-render \
        dot -Tsvg "/docs/diagrams/$name.dot" -o "/docs/images/$name.svg"
    # shadows, dotted paper, font fallbacks
    docker run --rm --user "$(id -u):$(id -g)" -v "$docs:/docs" -v "$here/finish.py:/finish.py:ro" ai-sdlc-factory/docs-render \
        python3 /finish.py "/docs/images/$name.svg"
    echo "rendered docs/images/$name.svg"
done
