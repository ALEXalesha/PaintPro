#!/usr/bin/env bash
# Публикация Electron-версии на GitHub: https://github.com/ALEXalesha/PaintPro
#
#   bash tools/publish-github.sh            # только код
#   bash tools/publish-github.sh v1.16.0    # код и тег, по тегу воркфлоу соберёт релиз
#
# Источник правды - этот репозиторий. На GitHub уезжает только paint-pro-electron/,
# поднятый в корень через subtree split: C#-версия не публикуется.
#
# Второй шаг - замена почты. В рабочих коммитах автором стоит личный gmail, а на GitHub
# почта автора видна любому, кто откроет коммит или допишет .patch к ссылке. Поэтому
# в публикуемой ветке она подменяется на анонимную почту аккаунта GitHub, по которой
# коммиты всё равно привязываются к профилю. Рабочая история на Gitea не трогается.
#
# Ветка переписывается каждый раз заново, так что пуш всегда --force: на GitHub в неё
# никто, кроме этого скрипта, не пишет.
set -euo pipefail

export PATH="$PATH:/c/Program Files/GitHub CLI"
REPO=ALEXalesha/PaintPro
PRIVATE_EMAIL=203467574+ALEXalesha@users.noreply.github.com
PUBLIC_EMAIL=203467574+ALEXalesha@users.noreply.github.com
TAG="${1:-}"

cd "$(dirname "$0")/.."

git remote get-url github >/dev/null 2>&1 || git remote add github "https://github.com/$REPO.git"

git branch -D github-main >/dev/null 2>&1 || true
git subtree split --prefix=paint-pro-electron -b github-main >/dev/null 2>&1

FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f --env-filter "
  if [ \"\$GIT_AUTHOR_EMAIL\" = '$PRIVATE_EMAIL' ]; then export GIT_AUTHOR_EMAIL='$PUBLIC_EMAIL'; fi
  if [ \"\$GIT_COMMITTER_EMAIL\" = '$PRIVATE_EMAIL' ]; then export GIT_COMMITTER_EMAIL='$PUBLIC_EMAIL'; fi
" github-main >/dev/null 2>&1
git update-ref -d refs/original/refs/heads/github-main 2>/dev/null || true

if git log github-main --format='%ae%n%ce' | grep -qx "$PRIVATE_EMAIL"; then
  echo "в публикуемой ветке остался личный адрес - пуш отменён" >&2
  exit 1
fi

echo "ветка github-main: $(git rev-list --count github-main) коммитов, $(git log -1 --format=%h github-main)"
gh auth setup-git
git push github github-main:main --force

if [ -n "$TAG" ]; then
  git tag -f "$TAG" github-main
  git push github "$TAG" --force
  echo "тег $TAG отправлен: релиз соберёт воркфлоу Release"
fi
