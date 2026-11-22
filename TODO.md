# To do

Open work on the project itself, in the order it matters. Delete a line when it is done. (Designs for features are in [docs/ideas.md](docs/ideas.md).)

## Before going public
- [ ] Create the GitHub repositories (`dev-foundry`, `dev-foundry-alert-intake`, `dev-foundry-demo`) and push the two new ones: `git remote add origin …`, `git push -u origin master`. Push the unpushed commits of `dev-foundry` too.
- [ ] Replace `OWNER` in the links of all three repositories (`git grep OWNER`); change `master` to `main` in the `SECURITY.md` links if the branch is renamed.
- [ ] Enable private vulnerability reporting on each GitHub repository (Settings, Code security), or the link in `SECURITY.md` leads nowhere.
- [ ] Read `README.md` as a stranger would: can they tell what it is and start in ten minutes?
- [ ] A new live-check fixture: `AGENTS.md` names `valters/greetings` on the local Forgejo; `valters/weather` is gone.

## Optional
- [ ] `CONTRIBUTING.md`: three lines pointing to `AGENTS.md` (GitHub then links it from pull requests).
- [ ] `CODE_OF_CONDUCT.md` (the Contributor Covenant needs a contact address): when there is a community.
- [ ] A docs website (the Markdown in `docs/` reads fine on GitHub meanwhile).
- [ ] Make every repository `host:owner/repo` and remove the special case of the host named `forgejo`.
- [ ] Remove the old-data fallbacks (nothing outside depends on them yet).
- [ ] Make `tools/onboard-repo.py` work for GitHub (labels and webhook); today only Forgejo, GitHub is set up by hand ([git-hosts.md](docs/git-hosts.md)).
- [ ] Live check of the project's own `commands` key (needs a merge into a default branch).
- [ ] Live check of a review that requests changes on GitHub (needs a second GitHub account).
- [ ] Spike for Azure DevOps as a git host ([ideas.md](docs/ideas.md)).
