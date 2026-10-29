# git-pa features

Everything git-pa can do, at a glance. For usage details and examples, see the
[README](../README.md).

The idea: you stay on the dev branch all day. git-pa does the branch switching, committing,
merging and deploying for you, without your working folder ever leaving dev.

## Commands

| Command | What it does |
| --- | --- |
| `git pa init` | Saves the main and dev branch names for the repository (or globally) |
| `git pa new` | Creates a branch from the latest main, named from type, ticket and title |
| `git pa commit` | Commits chosen changes to a feature branch, merges it into dev, and pushes |
| `git pa sync` | Pulls the latest remote dev into dev, keeping uncommitted changes |
| `git pa status` | Shows dev and every feature branch compared with main, dev and the remote |
| `git pa update` | Merges the latest main into a feature branch without switching to it |
| `git pa pr` | Opens the "create pull request" page from a feature branch to main |
| `git pa clean` | Deletes local feature branches whose work is already in main |
| `git pa reset-dev` | Rebuilds the shared dev branch from main plus the branches still in progress |
| `git pa deploy` | Syncs dev, builds a plug-in assembly, and pushes it to the dev environment |

## Branches

**Creating branches (`new`)**
- Pick a type: `bugfix`, `hotfix`, `feature`, `chore` or `refactor`.
- Names are built for you: `feature/PA-77-add-login-page`, `feature/PA-77`, `chore/update-packages`.
- Ticket numbers are uppercased; titles are lowercased with spaces turned into `-`, and characters
  git or shells dislike are dropped.
- Branch names are limited to 60 characters, checked while you type.
- Always branches from the latest main on the remote, not a stale local copy.
- `git pa new my/exact-name` still accepts any name.

**Keeping branches current (`update`)**
- Merges the latest main into a stale feature branch in a hidden working folder.
- `--all` updates every local feature branch, reporting each failure and carrying on.
- On a conflict nothing changes, and it shows how to resolve it in a separate folder.

**Pull requests (`pr`)**
- Works with GitHub, Azure DevOps, Bitbucket Cloud and Bitbucket Server.
- Understands SSH host aliases from `~/.ssh/config` (like `git@github-work:...`).
- Pushes the branch first if it isn't on the remote yet.
- Other hosts: set a URL template in `pa.prUrl`. `--print` prints the URL instead of opening it.

**Cleaning up (`clean`)**
- Finds branches merged into main, including squash-merged pull requests (git 2.38+).
- Never deletes a branch you created but haven't committed to yet.
- Choose from a pre-selected list; `--yes` deletes them all without asking.

## Committing

- **Never switches branches.** The feature branch is checked out in a hidden temporary folder,
  your staged changes are applied and committed there, and the branch is merged back into dev.
- **File picker:** a checklist of changed files, with already-staged files pre-selected.
  Partially staged files keep their staged parts. `-a` commits everything.
- **Branch picker:** feature branches listed most recent first.
- **Multi-line descriptions:** type line by line (an empty line finishes), or repeat `-d` for
  paragraphs.
- **Jira linking:** the branch's ticket is added to the summary (`PA-77: Add login page`), so
  Bitbucket links the commit to the Jira issue. Not added twice.
- **Teammates' work:** fast-forwards the feature branch to its remote copy before committing, and
  merges the latest remote dev before pushing, so pushes aren't rejected.
- **Auto-push:** pushes the feature branch and dev afterwards. Turn off with `pa.autoPush false`,
  or per commit with `--push` / `--no-push`.
- **Scripts and CI:** everything can be passed as options; without a terminal, `--branch` and
  `--summary` are required and are checked before anything is staged.

## Working with a shared dev branch

**Sync (`sync`)**
- Pulls the remote dev without losing work: staged changes stay staged, others are kept.

**Status (`status`)**
- Says whether dev is behind, ahead, or was rebuilt by `reset-dev`.
- For each feature branch: last commit, ahead/behind main, whether it's fully in dev, and whether
  it's pushed. `--fetch` updates from the remote first.

**Rebuilding dev (`reset-dev`)**
- Rebuilds dev as main plus the branches you pick; branches in dev now are pre-selected, so you
  just untick abandoned ones.
- Force-pushes safely: if anyone pushed to dev in the meantime, it stops with nothing changed.
- Your old dev is kept as `refs/pa-backup/dev-<date>`.
- Teammates follow automatically: their next `sync` or `commit` notices the rebuild, moves their dev
  onto it, keeps their uncommitted work, backs up their old dev, and lists any commits that only
  existed there.

## Deploying (`deploy`)

- One step: sync dev → NuGet restore → Release rebuild with Visual Studio's MSBuild →
  `pac plugin push`.
- Supports ILMerge projects: pushes the merged assembly.
- Several assemblies per repository, deployed separately by name (or picked from a list).
- Always pushes to the environment in `git-pa.json`, whichever environment `pac` is signed in to.
- **Plug-in class check:** compares the plug-in and workflow-activity classes in the build with
  the ones registered in Dataverse. A registered class missing from the build stops the deploy
  before pushing. New classes are reported after the push, with whether they still need
  registering in the Plugin Registration Tool.
- Asks before deploying (`--yes` skips), mentions when uncommitted changes are included, and
  refuses to push if the build failed or didn't produce a fresh assembly.
- Configured once per repository in a committed `git-pa.json`.

## Safety

| Situation | What git-pa does |
| --- | --- |
| Staged changes depend on code only on dev | Refuses to commit; nothing changes |
| Merge into dev has conflicts | Leaves the merge open and lists the exact steps to finish |
| A merge would overwrite untracked files | Stops before doing anything and names the files |
| A merge is in progress | Refuses to run until it's finished or cancelled |
| Push fails | Keeps the work locally and prints the command to retry |
| Teammate pushed to dev during `reset-dev` | The force-push is refused; nothing changes |
| Build fails during `deploy` | Nothing is pushed to the environment |
| A registered plug-in class is missing from the build | `deploy` stops before pushing and names the class |
| Unsure what a command will do | `--dry-run` on `new`, `commit`, `sync`, `update`, `clean`, `reset-dev` and `deploy` prints the exact commands |

## Configuration

**Git config** (per repository, or `--global`)

| Key | Default | Meaning |
| --- | --- | --- |
| `pa.main` | — | Main branch name |
| `pa.dev` | — | Dev branch name |
| `pa.remote` | `origin` | Remote to fetch from and push to |
| `pa.autoPush` | `true` | Push after `commit` and `update` |
| `pa.prUrl` | — | Pull request URL template for hosts `pr` doesn't recognize |
| `pa.updateCheck` | `true` | Check nuget.org for a newer git-pa once a day |

**`git-pa.json`** (committed in the plugin repository): the environment URL, build
configuration, solution, and the assemblies to deploy with their project, built file and
plug-in assembly ID.

**Environment variable:** `GIT_PA_NO_UPDATE_CHECK=1` turns off the update check.

## Other

- **Update notice:** in a terminal, at most once a day, says when a newer git-pa is on nuget.org.
  Never slows a command down by more than a second, and stays quiet when offline.
- **Version:** `git pa -v` or `git pa --version`.
- **Help:** `git pa <command> -h` (`--help` is intercepted by git itself).
