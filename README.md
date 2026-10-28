# git-pa

Git workflow helper for Power Apps plugin developers who share a single dev environment.

Stay on the dev branch all the time. `git pa` creates feature branches from main and
commits your staged changes to a feature branch (then merges it back into dev) without
your working tree ever switching branches.

## Install

```sh
dotnet tool install -g GitPa --add-source <folder or feed with the .nupkg>
```

Then, once per repository:

```sh
git pa init                      # asks for the main and dev branch names
git pa init --main main --dev dev
```

## Commands

Use `git pa <command> -h` for help (`--help` is intercepted by git itself).

### `git pa new [name]`

Fetches the latest main from the remote and creates `name` from it. You stay on dev, and
uncommitted changes are untouched.

### `git pa commit`

Commits your **staged** changes to a feature branch, merges that branch into dev, and pushes
both (unless auto-push is off).

| Option | Meaning |
| --- | --- |
| `-b, --branch` | Feature branch to commit to (prompted with a list if omitted) |
| `-m, --summary` | Commit summary (prompted if omitted) |
| `-d, --description` | Commit description (optional) |
| `-a, --all` | Stage all changes first |
| `--push` / `--no-push` | Override the `pa.autoPush` setting for this commit |

Under the hood:

1. Your staged changes are saved as a patch.
2. The feature branch is checked out in a temporary worktree under `.git/pa-worktrees`,
   fast-forwarded to its remote copy, and the patch is applied and committed there.
3. Back on dev, your changes are set aside, the feature branch is merged in, and any
   **unstaged** changes are put back.
4. The feature branch and dev are pushed.

If the patch doesn't apply to the feature branch, nothing is committed and your working tree
is left exactly as it was. This usually means your change depends on code that is on dev
(from someone else's feature) but not on your feature branch.

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
