// Сборка ОДНОГО файла для работы в браузере.
//
// Приложение и так живёт в одном HTML: стили и скрипт внутри, ничего не подгружается
// снаружи. Поэтому «сборка» - это копия с двумя правками:
//
//   1. подставить номер версии (в Electron его отдаёт main-процесс, в браузере спросить
//      не у кого);
//   2. вписать заголовок страницы с версией, чтобы вкладка была узнаваема.
//
// Получившийся файл самодостаточен: его можно переслать, положить на флешку или открыть
// двойным щелчком на любом устройстве с браузером. Ни установки, ни интернета не нужно.

const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const version = require(path.join(root, 'package.json')).version;
const src = fs.readFileSync(path.join(root, 'paint-pro.html'), 'utf8');

let out = src;

const versionAnchor = "const WEB_VERSION = '';";
if (!out.includes(versionAnchor)) {
  console.error('не найдено место для номера версии - проверьте paint-pro.html');
  process.exit(1);
}
out = out.replace(versionAnchor, `const WEB_VERSION = '${version}';`);

const titleAnchor = '<title>Paint Pro</title>';
if (out.includes(titleAnchor)) {
  out = out.replace(titleAnchor, `<title>Paint Pro ${version}</title>`);
}

const distDir = path.join(root, 'dist');
fs.mkdirSync(distDir, { recursive: true });
const target = path.join(distDir, `paint-pro-${version}-web.html`);
fs.writeFileSync(target, out, 'utf8');

const kb = (fs.statSync(target).size / 1024).toFixed(0);
console.log(`Готово: ${target} (${kb} КБ)`);
console.log('Один файл, ничего больше не нужно: открывается двойным щелчком в любом браузере.');
