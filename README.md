# git-pa

Git workflow helper for Power Apps plugin developers who share a single dev environment.

Stay on the dev branch all the time; `git pa` creates feature branches from main and
commits your staged changes to a feature branch (then merges it back into dev) without
you ever switching branches.

## Commands (planned)

| Command | What it does |
| --- | --- |
| `git pa init` | Configure the main and dev branch names for this repo |
| `git pa new` | Create a feature branch from the latest remote main |
| `git pa commit` | Commit staged changes to a feature branch, merge it into dev, push |

## Configuration

Stored in git config (per repo, or `--global`):

| Key | Default | Meaning |
| --- | --- | --- |
| `pa.main` | — | Main branch name |
| `pa.dev` | — | Dev branch name |
| `pa.autoPush` | `true` | Push the feature and dev branches after `commit` |

## Development

```sh
dotnet build
dotnet test
dotnet pack src/GitPa            # -> artifacts/GitPa.<version>.nupkg
dotnet tool install -g GitPa --add-source ./artifacts
```
