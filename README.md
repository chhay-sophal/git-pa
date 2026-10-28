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
`new`, `commit` and `sync` accept `--dry-run` to show the git commands they would run
without changing anything.

### `git pa new [name]`

Fetches the latest main from the remote and creates `name` from it. You stay on dev, and
uncommitted changes are untouched.

### `git pa commit`

Commits changes to a feature branch, merges that branch into dev, and pushes both (unless
auto-push is off).

In a terminal, you pick the files to commit from a checklist (already staged files are
pre-selected; partially staged files keep their staged hunks). Then you pick the feature
branch and type the commit message. Every prompt can also be answered with an option:

| Option | Meaning |
| --- | --- |
| `-b, --branch` | Feature branch to commit to |
| `-m, --summary` | Commit summary |
| `-d, --description` | Commit description (optional) |
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

## Configuration

Stored in git config (per repository, or `--global`):

| Key | Default | Meaning |
| --- | --- | --- |
| `pa.main` | — | Main branch name |
| `pa.dev` | — | Dev branch name |
| `pa.remote` | `origin` | Remote to fetch from and push to |
| `pa.autoPush` | `true` | Push the feature and dev branches after `commit` |

Turn off auto-push for one repository with `git config pa.autoPush false`, or for all of
them with `git config --global pa.autoPush false`.

## Development

```sh
dotnet build
dotnet test
dotnet pack src/GitPa -c Release      # -> artifacts/GitPa.<version>.nupkg
dotnet tool install -g GitPa --add-source ./artifacts
```

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
