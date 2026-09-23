#!/usr/bin/env bash
# Публикация Paint Pro на GitHub: https://github.com/ALEXalesha/PaintPro
#
#   bash tools/publish-github.sh            # только код
#   bash tools/publish-github.sh v1.27.0    # код и тег, по тегу воркфлоу Release соберёт обе версии
#
# Источник правды - этот репозиторий на Gitea. До 1.27.0 на GitHub уезжал один
# paint-pro-electron/ срезом подкаталога; с 1.27.0 публикуется весь репозиторий: и
# C#-версия, и Electron-версия. Ветка пересобирается каждый раз заново, отсюда --force:
# на GitHub в неё никто, кроме этого скрипта, не пишет. Пушить руками нельзя - коммиты
# уехали бы с личной почтой.
#
# В публикуемой копии заменяется личное - и в авторах коммитов, и в содержимом
# файлов по всей истории:
#   - gmail автора   -> анонимная почта аккаунта GitHub;
#   - адрес Gitea    -> gitea.local;
#   - полное имя     -> ник ALEXalesha (по решению автора, 23.09.2026).
# Ни одна из этих строк не записана здесь: почта берётся из git config user.email,
# адрес - из remote origin, имя - из git config publish.privateName (локальная
# настройка репозитория, в историю она не попадает). Строкой в скрипте личное
# однажды уже уехало на GitHub - внутри самого такого скрипта.
set -euo pipefail

export PATH="$PATH:/c/Program Files/GitHub CLI"
REPO=ALEXalesha/PaintPro
PRIVATE_EMAIL="$(git config user.email)"
PUBLIC_EMAIL=203467574+ALEXalesha@users.noreply.github.com
LAN_GITEA="$(git remote get-url origin | sed -E 's#^[a-z]+://([^/]+)/.*#\1#')"
PUBLIC_GITEA='gitea.local'
PRIVATE_NAME="$(git config --get publish.privateName || true)"
PUBLIC_NAME=ALEXalesha
TAG="${1:-}"

cd "$(dirname "$0")/.."

if [ -z "$PRIVATE_NAME" ]; then
  echo "не задано git config publish.privateName - без него полное имя не заменить" >&2
  exit 1
fi

git remote get-url github >/dev/null 2>&1 || git remote add github "https://github.com/$REPO.git"

git branch -D github-main >/dev/null 2>&1 || true
git branch -f github-main HEAD

FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f --env-filter "
  if [ \"\$GIT_AUTHOR_EMAIL\" = '$PRIVATE_EMAIL' ]; then export GIT_AUTHOR_EMAIL='$PUBLIC_EMAIL'; fi
  if [ \"\$GIT_COMMITTER_EMAIL\" = '$PRIVATE_EMAIL' ]; then export GIT_COMMITTER_EMAIL='$PUBLIC_EMAIL'; fi
" github-main >/dev/null 2>&1
git update-ref -d refs/original/refs/heads/github-main 2>/dev/null || true

PATTERN="$PRIVATE_EMAIL|$LAN_GITEA|$PRIVATE_NAME"
# `|| true`: без совпадений git grep возвращает 1, и при pipefail скрипт молча
# обрывался бы здесь.
DIRTY=$(git grep -I -l -E "$PATTERN" $(git rev-list github-main) -- 2>/dev/null \
        | sed 's/^[^:]*://' | sort -u | tr '\n' ' ' || true)
if [ -n "$DIRTY" ]; then
  echo "личное в файлах: $DIRTY- переписываю содержимое"
  FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f --index-filter "
    for f in $DIRTY; do
      mode=\$(git ls-tree \$GIT_COMMIT -- \"\$f\" | awk '{print \$1}')
      [ -n \"\$mode\" ] || continue
      blob=\$(git cat-file blob \$GIT_COMMIT:\"\$f\" \
             | sed -e 's/$PRIVATE_EMAIL/$PUBLIC_EMAIL/g' -e 's/$LAN_GITEA/$PUBLIC_GITEA/g' \
                   -e 's/$PRIVATE_NAME/$PUBLIC_NAME/g' \
             | git hash-object -w --stdin)
      git update-index --cacheinfo \$mode,\$blob,\"\$f\"
    done
  " github-main >/dev/null 2>&1
  git update-ref -d refs/original/refs/heads/github-main 2>/dev/null || true
fi

if git log github-main --format='%ae%n%ce%n%an%n%cn' | grep -q -E -x "$PRIVATE_EMAIL|$PRIVATE_NAME"; then
  echo "в публикуемой ветке осталось личное в авторе коммита - пуш отменён" >&2
  exit 1
fi
if git grep -I -q -E "$PATTERN" $(git rev-list github-main) -- 2>/dev/null; then
  echo "личное осталось в файлах публикуемой истории - пуш отменён" >&2
  exit 1
fi

echo "ветка github-main: $(git rev-list --count github-main) коммитов, $(git log -1 --format=%h github-main)"
gh auth setup-git
git push github github-main:main --force

if [ -n "$TAG" ]; then
  git tag -f "$TAG" github-main
  git push github "refs/tags/$TAG" --force
  echo "тег $TAG отправлен: релиз соберёт воркфлоу Release"
fi
