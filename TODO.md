# To do

Open work on the project itself, in the order it matters. Delete a line when it is done. (Designs for features are in [docs/ideas.md](docs/ideas.md).)

## Before going public
- [ ] Create the GitHub repositories (`ai-sdlc-factory`, `ai-sdlc-factory-alert-intake`, `ai-sdlc-factory-demo`) and push the two new ones: `git remote add origin …`, `git push -u origin master`. Push the unpushed commits of `ai-sdlc-factory` too.
- [ ] Replace `OWNER` in the links of all three repositories (`git grep OWNER`); change `master` to `main` in the `SECURITY.md` links if the branch is renamed.
- [ ] Enable private vulnerability reporting on each GitHub repository (Settings, Code security), or the link in `SECURITY.md` leads nowhere.
- [ ] Read `README.md` as a stranger would: can they tell what it is and start in ten minutes?
- [ ] Keep one throwaway repository per git host as the live-check fixture (`AGENTS.md` describes it without naming one; `valters/weather` is gone).

- [ ] Try one permanent way in for GitHub webhooks end to end (a named Cloudflare Tunnel with the address rule is the suggested one) and correct [exposing-webhooks.md](docs/exposing-webhooks.md) where reality differs; it is written from the providers' documentation and not tested.

## Optional
- [ ] `CONTRIBUTING.md`: three lines pointing to `AGENTS.md` (GitHub then links it from pull requests).
- [ ] `CODE_OF_CONDUCT.md` (the Contributor Covenant needs a contact address): when there is a community.
- [ ] A docs website (the Markdown in `docs/` reads fine on GitHub meanwhile).
- [ ] Make every repository `host:owner/repo` and remove the special case of the host named `forgejo`.
- [ ] Remove the old-data fallbacks (nothing outside depends on them yet).
- [ ] Try `tools/onboard-repo.py` against a real GitHub repository (it is tested against a fake API only): `GITHUB_TOKEN=... python3 tools/onboard-repo.py owner/repo --host github --public-url https://... --dry-run`, then without `--dry-run`.
- [ ] Live check of the project's own `commands` key (needs a merge into a default branch).
- [ ] Live check of a review that requests changes on GitHub (needs a second GitHub account).
- [ ] Spike for Azure DevOps as a git host ([ideas.md](docs/ideas.md)).
