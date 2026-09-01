// Оснастка для проверок НАСТОЯЩЕГО приложения, а не страницы в браузере.
//
// Быстрый набор (tests/) открывает paint-pro.html в Chromium и подменяет мост в Electron
// заглушками. Этого хватает для всего, что живёт в renderer, но ровно поэтому мимо него
// проходят две вещи:
//
//   1. main.js целиком - диалоги сохранения и открытия, запись файла, вопрос при закрытии
//      и пятисекундная страховка. Двести семьдесят строк, которых не касалась ни одна
//      проверка.
//   2. Клавиатура в той оболочке, где она живёт. Хоткеи проверялись в Chromium; если
//      Electron перехватывает их иначе, набор этого не увидит.
//
// Playwright умеет запускать настоящий Electron и, главное, ВЫПОЛНЯТЬ КОД В MAIN-ПРОЦЕССЕ:
// electronApp.evaluate() получает туда доступ. Значит можно подменить нативный диалог,
// не показывая его, и проверить, что приложение сделало дальше.
//
// Эти проверки медленные: запуск приложения - секунды. Поэтому они отдельной командой
// (`npm run test:app`), а не в быстром `npm test`.

const path = require('path');
const os = require('os');
const fs = require('fs');
const { _electron: electron } = require('playwright-core');

const APP_DIR = path.join(__dirname, '..');

/**
 * Запустить приложение и дождаться его окна.
 *
 * Каждому запуску - СВОЯ папка пользовательских данных. Приложение держит замок одного
 * экземпляра (`requestSingleInstanceLock`), а замок этот лежит именно там: с общей папкой
 * второй запуск молча выходит, и проверка падает с «окно закрыто» вместо своей причины.
 * Там же живёт кеш, который иначе не поделить между прогонами.
 */
async function launchApp(extraArgs) {
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'paintpro-data-'));
  const app = await electron.launch({
    executablePath: require('electron'),
    args: [APP_DIR, '--user-data-dir=' + dataDir].concat(extraArgs || []),
    cwd: APP_DIR,
  });
  app.__dataDir = dataDir;

  // Вопрос «сохранить перед выходом?» подменяем СРАЗУ и по умолчанию. Иначе закрытие
  // приложения в конце каждой проверки открывает настоящее окно и ждёт живого человека:
  // прогон стоит до таймаута, а причина выглядит как «окно закрыто». Ответ по умолчанию -
  // «не сохранять» (1). Проверкам, для которых этот вопрос и есть предмет, ничто не мешает
  // поставить свой ответ поверх.
  await app.evaluate(async ({ dialog }) => {
    dialog.showMessageBox = async () => ({ response: 1 });
  });

  const win = await app.firstWindow();
  await win.waitForFunction(() => typeof state !== 'undefined' && !!document.getElementById('canvas'));
  return { app, win };
}

/** Закрыть приложение и убрать за собой папку данных. */
async function closeApp(app) {
  // Сначала снимаем признак несохранённой работы: закрытие тогда идёт коротким путём,
  // без вопросов вообще. Подмена диалога выше - вторая линия обороны, на случай если
  // окно уже закрылось и до renderer не достучаться.
  try {
    const w = app.windows()[0];
    if (w) await w.evaluate(() => { state.savedHistoryIndex = state.historyIndex; state.savedDisabledKey = disabledKey(); });
  } catch (_) { /* окна уже нет */ }
  try { await app.close(); } catch (_) { /* окно могло закрыться само */ }
  if (app.__dataDir) {
    try { fs.rmSync(app.__dataDir, { recursive: true, force: true }); } catch (_) { /* занято - не беда */ }
  }
}

/** Отдельная папка под файлы проверки; удаляется вызывающим. */
function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'paintpro-'));
}

/**
 * Подменить нативный диалог сохранения так, чтобы он ничего не показывал и отдавал
 * заданный путь. Делается В MAIN-ПРОЦЕССЕ - иначе диалог откроется по-настоящему и
 * проверка повиснет.
 */
async function stubSaveDialog(app, filePath) {
  await app.evaluate(async ({ dialog }, p) => {
    dialog.showSaveDialog = async () => (p ? { canceled: false, filePath: p } : { canceled: true });
  }, filePath);
}

/** То же для вопроса про несохранённую работу: 0 - сохранить, 1 - не сохранять, 2 - отмена. */
async function stubUnsavedAnswer(app, response) {
  await app.evaluate(async ({ dialog }, r) => {
    dialog.showMessageBox = async () => ({ response: r });
  }, response);
}

/** И для открытия файла. */
async function stubOpenDialog(app, filePaths) {
  await app.evaluate(async ({ dialog }, list) => {
    dialog.showOpenDialog = async () =>
      (list && list.length ? { canceled: false, filePaths: list } : { canceled: true, filePaths: [] });
  }, filePaths);
}

/** Перехватить showErrorBox, чтобы узнать, сказали ли пользователю об ошибке. */
async function captureErrorBoxes(app) {
  await app.evaluate(async ({ dialog }) => {
    global.__errorBoxes = [];
    const orig = dialog.showErrorBox;
    dialog.showErrorBox = (title, content) => { global.__errorBoxes.push({ title, content }); };
    global.__restoreErrorBox = () => { dialog.showErrorBox = orig; };
  });
}

async function errorBoxes(app) {
  return app.evaluate(async () => global.__errorBoxes || []);
}

/** Нарисовать что-нибудь настоящей мышью, чтобы документ стал непустым. */
async function drawSomething(win) {
  const box = await win.locator('#canvas').boundingBox();
  const size = await win.evaluate(() => [canvas.width, canvas.height]);
  const toScreen = (x, y) => ({
    x: box.x + (x / size[0]) * box.width,
    y: box.y + (y / size[1]) * box.height,
  });
  await win.click('[data-tool="pencil"]');
  await win.evaluate(() => { state.size = 24; syncSizeForTool(); setColor('#ff0000'); });
  const a = toScreen(150, 200);
  const b = toScreen(700, 400);
  await win.mouse.move(a.x, a.y);
  await win.mouse.down();
  await win.mouse.move(b.x, b.y, { steps: 8 });
  await win.mouse.up();
  await win.waitForFunction(() => !state.restoring);
}

/** Первые байты файла: по ним видно, PNG это или JPEG на самом деле. */
function magic(filePath) {
  const b = fs.readFileSync(filePath).subarray(0, 4);
  if (b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47) return 'png';
  if (b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return 'jpeg';
  return 'иное: ' + Array.from(b).map((x) => x.toString(16)).join(' ');
}

module.exports = {
  launchApp, closeApp, tempDir, stubSaveDialog, stubUnsavedAnswer, stubOpenDialog,
  captureErrorBoxes, errorBoxes, drawSomething, magic, APP_DIR,
};
