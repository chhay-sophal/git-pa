# git-pa

Git workflow helper for Power Apps plugin developers who share a single dev environment.

Stay on the dev branch all the time. `git pa` creates feature branches from main and
commits your staged changes to a feature branch (then merges it back into dev) without
your working tree ever switching branches.

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 8 or later (`dotnet --version`)
- Git, recent version (`git --version`)
- Push access to the repository's remote, if you use auto-push

## Install

```sh
dotnet tool install -g GitPa
```

Update later with `dotnet tool update -g GitPa`.

Then, once per repository:

```sh
git pa init                      # asks for the main and dev branch names
git pa init --main main --dev dev
```

## Commands

Use `git pa <command> -h` for help (`--help` is intercepted by git itself).
`new`, `commit`, `sync`, `update`, `clean` and `reset-dev` accept `--dry-run` to show the git commands they would run
without changing anything.

### `git pa new`

Creates a branch from the latest main on the remote. You stay on dev, and uncommitted changes
are untouched.

You pick the branch type and enter the details; the name is built for you:

| Type | Asks for | Branch name |
| --- | --- | --- |
| `bugfix`, `hotfix`, `feature`, `refactor` | Ticket number, title (optional) | `feature/1234-add-login-page`, `feature/1234` |
| `chore` | Title | `chore/update-nuget-packages` |

The title is lowercased, spaces become `-`, and characters git or shells dislike are dropped.
Ticket numbers keep their case and may contain letters, digits and dashes (`PA-77`). Branch
names are limited to 60 characters.

Without prompts: `git pa new -t feature --ticket 1234 --title "Add login page"`. To use an
exact name instead, pass it directly: `git pa new my/branch-name`.

### `git pa commit`

Commits changes to a feature branch, merges that branch into dev, and pushes both (unless
auto-push is off).

In a terminal, you pick the files to commit from a checklist (already staged files are
pre-selected; partially staged files keep their staged hunks). Then you pick the feature
branch and type the commit message. The description can span several lines; an empty line
finishes it. Every prompt can also be answered with an option:

| Option | Meaning |
| --- | --- |
| `-b, --branch` | Feature branch to commit to |
| `-m, --summary` | Commit summary |
| `-d, --description` | Commit description (optional); repeat for more paragraphs, like `git commit -m` |
| `-a, --all` | Commit all changes instead of choosing files |
| `--push` / `--no-push` | Override the `pa.autoPush` setting for this commit |
| `--dry-run` | Show what would happen without changing anything |

Without a terminal (scripts, CI), `--branch` and `--summary` are required and the currently
staged changes are committed (or all changes, with `--all`).

Under the hood:

1. Your staged changes are saved as a patch.
2. The feature branch is checked out in a temporary worktree under `.git/pa-worktrees`,
   fast-forwarded to its remote copy, and the patch is applied and committed there.
3. Back on dev, your changes are set aside, the latest remote dev (if a teammate pushed)
   and then the feature branch are merged in, and any **unstaged** changes are put back.
4. The feature branch and dev are pushed.

If the patch doesn't apply to the feature branch, nothing is committed and your working tree
is left exactly as it was. This usually means your change depends on code that is on dev
(from someone else's feature) but not on your feature branch.

If a merge into dev conflicts, the merge is left open for you to resolve in your editor, and
the error lists the exact steps to finish. Your commit on the feature branch is already safe.

### `git pa sync`

Pulls the latest remote dev into your dev branch, keeping your uncommitted changes (staged
changes stay staged). Run it before deploying to the shared environment, so you deploy
everyone's work, not just yours.

### `git pa status`

Shows whether dev is behind or ahead of the remote, and a table of your feature branches:
how far each is ahead of or behind main, whether it is fully merged into dev, and whether it
has been pushed. Add `--fetch` to fetch from the remote first.

### `git pa update [branch]`

Merges the latest main into a feature branch (picked from a list if omitted) in a temporary
worktree, so you stay on dev, then pushes it (unless auto-push is off). Use it on branches
that went stale while waiting for approval. `--all` updates every local feature branch.

On a conflict nothing is changed, and the error shows how to resolve it in a separate folder.

### `git pa pr [branch]`

Opens the "create pull request" page from the feature branch to main in your browser,
pushing the branch first if needed. Works for GitHub, Azure DevOps, Bitbucket Cloud and
Bitbucket Server, including SSH host aliases from `~/.ssh/config`. `--print` prints the URL
instead. For other hosts, set a URL template:

```sh
git config pa.prUrl "https://git.example.com/repo/pulls/new?from={branch}&to={target}"
```

### `git pa clean`

Deletes local feature branches whose work is already in main, including branches whose pull
request was squash-merged. Branches you created but never committed to are kept. You pick
from the list (all pre-selected); `--yes` deletes without asking, `--dry-run` only lists them.

### `git pa reset-dev`

Over time dev collects abandoned experiments. `reset-dev` rebuilds it as main plus the
feature branches you pick (those in dev now are pre-selected) and force-pushes it with
`--force-with-lease`, so it fails safely if someone pushed in the meantime. Your old dev is
kept as `refs/pa-backup/dev-<date>`.

This replaces dev for the whole team, so tell your teammates first. Their next `git pa sync`
or `git pa commit` notices the reset and moves their dev onto the new one, keeping their
uncommitted changes and backing up their old dev (and listing any commits that were only on
it).

Each teammate should run `git pa sync` once before using plain `git pull` on dev again. Until
then their dev still has the old history, and `git pull` would merge it back in. After that
first sync, `git pull` is safe as usual. `git pa status --fetch` tells them when they still
need to sync. If someone pulled too early and hasn't pushed, `git reset --hard origin/dev`
undoes it (stash uncommitted changes first). If they already pushed, run `git pa reset-dev`
again.

Non-interactively, pick branches with `-b` (repeatable) and confirm with `--yes`.

### Update notice

When run in a terminal, git-pa checks nuget.org at most once a day and tells you when a newer
version is available. Turn it off with `git config --global pa.updateCheck false` or the
environment variable `GIT_PA_NO_UPDATE_CHECK=1`.

## Configuration

Stored in git config (per repository, or `--global`):

| Key | Default | Meaning |
| --- | --- | --- |
| `pa.main` | — | Main branch name |
| `pa.dev` | — | Dev branch name |
| `pa.remote` | `origin` | Remote to fetch from and push to |
| `pa.autoPush` | `true` | Push after `commit` and `update` |
| `pa.prUrl` | — | Pull request URL template for hosts `pr` doesn't recognize |
| `pa.updateCheck` | `true` | Check nuget.org for a newer git-pa once a day |

Turn off auto-push for one repository with `git config pa.autoPush false`, or for all of
them with `git config --global pa.autoPush false`.

## Development

```sh
dotnet build
dotnet test
```

### Run from source

Stand in a plugin repository and run the tool straight from the source. Everything after
`--` is passed to git-pa:

```sh
cd path/to/plugin-repo
dotnet run --project path/to/git-pa/src/GitPa -- status
dotnet run --project path/to/git-pa/src/GitPa -- commit --dry-run
```

### Install a local build

Pack under a unique pre-release version, then update your global install to it. Bump the
suffix (`-dev.2`, `-dev.3`, …) for each new build: reusing a version makes NuGet serve the
cached older package.

```sh
dotnet pack src/GitPa -c Release -p:Version=0.2.0-dev.1      # -> artifacts/
dotnet tool update -g GitPa --add-source ./artifacts --version 0.2.0-dev.1
git pa -v
```

Go back to the published version with `dotnet tool update -g GitPa`. A pre-release
(`0.2.0-dev.1`) is lower than the final `0.2.0`, so this moves you up once it's released.
To go back to an older release, add `--version <version> --allow-downgrade`.

## Releasing

Push a version tag. The release workflow tests, packs that version, publishes it to nuget.org
and creates a GitHub release:

```sh
git tag v0.2.0
git push origin v0.2.0
```

One-time setup for publishing with nuget.org trusted publishing (no stored API key):

1. On nuget.org, open your account's **Trusted Publishing** page and add a policy for
   repository owner `chhay-sophal`, repository `git-pa`, workflow file `release.yml`.
2. In the GitHub repository, add a secret `NUGET_USER` containing your nuget.org username
   (Settings → Secrets and variables → Actions).
