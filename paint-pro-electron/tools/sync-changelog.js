// CHANGELOG публикуемой Electron-версии собирается из общего, а не пишется вторым.
//
// История обеих реализаций ведётся одним файлом в корне рабочего репозитория: выпуски,
// затронувшие обе версии, там записаны один раз («1.26.0 / 1.15.0»), и разносить их
// руками по двум файлам - верный способ получить два расходящихся рассказа об одном
// изменении.
//
// До 1.27.0 на GitHub публиковалась только Electron-версия, и C#-часть истории сюда не
// ехала. С 1.27.0 репозиторий опубликован целиком вместе с общим файлом, а этот остаётся
// историей одной Electron-версии. Границу задаёт метка в общем файле, а не догадка по номерам: номера у версий свои у
// каждой и местами пересекаются, так что отличить «1.23.0 C#» от «1.12.0 Electron» по
// одному виду заголовка нельзя.
//
//   node tools/sync-changelog.js
//
// Запускается в рабочем репозитории, где общий файл лежит на уровень выше. Результат
// коммитится: в опубликованном репозитории родителя нет и брать историю неоткуда.

const fs = require('fs');
const path = require('path');

const SOURCE = path.join(__dirname, '..', '..', 'CHANGELOG.md');
const TARGET = path.join(__dirname, '..', 'CHANGELOG.md');
const MARKER = '<!-- electron-changelog-end -->';

const PREFACE = `> История обеих реализаций Paint Pro ведётся одним файлом, поэтому выпуски, менявшие
> обе сразу, записаны как «C# / Electron»: **1.26.0 / 1.15.0** — это C#-версия 1.26.0 и
> Electron-версия 1.15.0. Здесь опубликована Electron-версия, и её история начинается
> с 1.11.1 — с того выпуска, когда у неё появилась собственная линия номеров.
>
> Файл собирается из общего скриптом \`tools/sync-changelog.js\`, руками не правится.

`;

function main() {
  if (!fs.existsSync(SOURCE)) {
    console.error(`не найден общий файл истории: ${SOURCE}`);
    console.error('скрипт работает только в рабочем репозитории, где C#- и Electron-версии лежат рядом');
    process.exit(1);
  }

  const text = fs.readFileSync(SOURCE, 'utf8').replace(/^﻿/, '');
  const cut = text.indexOf(MARKER);
  if (cut === -1) {
    console.error(`в ${SOURCE} нет метки ${MARKER} - без неё границу эпох не определить`);
    process.exit(1);
  }

  const head = text.slice(0, cut).trimEnd();
  const firstBreak = head.indexOf('\n');
  const title = head.slice(0, firstBreak).trim();
  const body = head.slice(firstBreak + 1).trimStart();

  const out = `${title}\n\n${PREFACE}${body}\n`;
  fs.writeFileSync(TARGET, out, 'utf8');

  const releases = (out.match(/^## /gm) || []).length;
  const kb = (Buffer.byteLength(out, 'utf8') / 1024).toFixed(0);
  console.log(`Готово: ${TARGET} — ${releases} выпусков, ${kb} КБ`);
}

main();
