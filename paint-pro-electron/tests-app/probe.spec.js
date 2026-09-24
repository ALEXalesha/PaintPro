// ВРЕМЕННЫЕ гипотезы по main-процессу и настоящей клавиатуре.
const { test, expect } = require('@playwright/test');
const fs = require('fs');
const path = require('path');
const {
  launchApp, closeApp, tempDir, stubSaveDialog, stubUnsavedAnswer, stubOpenDialog,
  captureErrorBoxes, errorBoxes, drawSomething, magic,
} = require('./harness');

// ─── Настоящая клавиатура в настоящей оболочке ───
test('LA1 хоткеи инструментов работают в самом приложении', async () => {
  const { app, win } = await launchApp();
  try {
    for (const [key, tool] of [['b', 'brush'], ['e', 'eraser'], ['g', 'fill'], ['p', 'pencil']]) {
      await win.keyboard.press(key);
      expect(await win.evaluate(() => state.tool), key).toBe(tool);
    }
  } finally {
    await closeApp(app);
  }
});

test('LA2 Ctrl+Z и Ctrl+Y работают в самом приложении', async () => {
  const { app, win } = await launchApp();
  try {
    const clean = await win.evaluate(() => canvas.toDataURL());
    await drawSomething(win);
    const drawn = await win.evaluate(() => canvas.toDataURL());
    expect(drawn).not.toBe(clean);

    await win.keyboard.press('Control+z');
    await win.waitForFunction(() => !state.restoring);
    expect(await win.evaluate(() => canvas.toDataURL())).toBe(clean);

    await win.keyboard.press('Control+y');
    await win.waitForFunction(() => !state.restoring);
    expect(await win.evaluate(() => canvas.toDataURL())).toBe(drawn);
  } finally {
    await closeApp(app);
  }
});

test('LA3 Ctrl+D снимает выделение в самом приложении', async () => {
  const { app, win } = await launchApp();
  try {
    await drawSomething(win);
    await win.evaluate(() => selectAll());
    expect(await win.evaluate(() => !!state.selection)).toBe(true);
    await win.keyboard.press('Control+d');
    await win.waitForTimeout(120);
    expect(await win.evaluate(() => !!state.selection), 'Ctrl+D не дошёл до приложения').toBe(false);
  } finally {
    await closeApp(app);
  }
});

// ─── Запись файла ───
test('LB1 сохранение в .jpg даёт настоящий JPEG, а не PNG под чужим именем', async () => {
  const { app, win } = await launchApp();
  const dir = tempDir();
  try {
    const target = path.join(dir, 'проба.jpg');
    await stubSaveDialog(app, target);
    await drawSomething(win);
    expect(await win.evaluate(() => saveCanvas(true))).toBe(true);
    expect(fs.existsSync(target), 'файл не записан').toBe(true);
    expect(magic(target)).toBe('jpeg');
  } finally {
    await closeApp(app);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('LB2 сохранение в .png даёт PNG', async () => {
  const { app, win } = await launchApp();
  const dir = tempDir();
  try {
    const target = path.join(dir, 'проба.png');
    await stubSaveDialog(app, target);
    await drawSomething(win);
    expect(await win.evaluate(() => saveCanvas(true))).toBe(true);
    expect(magic(target)).toBe('png');
  } finally {
    await closeApp(app);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('LB3 ошибка записи доезжает до пользователя', async () => {
  const { app, win } = await launchApp();
  try {
    // Каталога нет - запись обязана не выйти.
    const target = path.join('Z:', 'нет-такого-каталога', 'файл.png');
    await stubSaveDialog(app, target);
    await drawSomething(win);

    const ok = await win.evaluate(() => saveCanvas(true));
    await win.waitForTimeout(200);

    expect(ok, 'сохранение отчиталось об успехе').toBe(false);
    // Сообщение - окно в цветах темы (glassAlert), а не системный alert.
    const said = await win.evaluate(() => (document.querySelector('.gm-box .gm-text') || {}).textContent || '');
    expect(said, 'об ошибке записи не сказали ни слова').toContain('Ошибка сохранения');
    // И документ не должен считаться сохранённым.
    expect(await win.evaluate(() => isDirty()), 'документ объявлен сохранённым').toBe(true);
  } finally {
    await closeApp(app);
  }
});

test('LB4 отказ от выбора пути не считается сохранением', async () => {
  const { app, win } = await launchApp();
  try {
    await stubSaveDialog(app, null);      // пользователь закрыл диалог
    await drawSomething(win);
    expect(await win.evaluate(() => saveCanvas(true))).toBe(false);
    expect(await win.evaluate(() => isDirty())).toBe(true);
  } finally {
    await closeApp(app);
  }
});

test('LB5 после «Создать» Ctrl+S не перезаписывает прежний файл молча', async () => {
  const { app, win } = await launchApp();
  const dir = tempDir();
  try {
    const target = path.join(dir, 'первый.png');
    await stubSaveDialog(app, target);
    await drawSomething(win);
    expect(await win.evaluate(() => saveCanvas(true))).toBe(true);
    const before = fs.readFileSync(target).length;

    // «Создать» обязан отвязать документ от файла. Дальше диалог отдаёт отказ:
    // если приложение всё-таки запишет, значит путь оно помнит и спрашивать не стало.
    win.on('dialog', (d) => d.accept().catch(() => {}));
    await win.evaluate(() => newCanvas());
    await win.waitForFunction(() => !state.restoring);
    await stubSaveDialog(app, null);
    await win.evaluate(() => saveCanvas(false));
    await win.waitForTimeout(200);

    expect(fs.readFileSync(target).length,
      'чистый лист лёг поверх прежнего файла').toBe(before);
  } finally {
    await closeApp(app);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ─── Закрытие окна ───
test('LC1 чистый документ закрывается без вопроса', async () => {
  const { app, win } = await launchApp();
  try {
    // «Отмена» - если спросят, окно останется, и ожидание закрытия не дождётся.
    await stubUnsavedAnswer(app, 2);
    // Окно закрывается, поэтому дальше по нему ничего вызывать нельзя: ждём само событие.
    const closed = win.waitForEvent('close', { timeout: 5000 }).then(() => true).catch(() => false);
    await win.evaluate(() => handleCloseRequest()).catch(() => {});
    expect(await closed, 'чистый документ не закрылся').toBe(true);
  } finally {
    await closeApp(app);
  }
});

test('LC2 несохранённый документ по ответу «отмена» не закрывается', async () => {
  const { app, win } = await launchApp();
  try {
    await stubUnsavedAnswer(app, 2);
    await drawSomething(win);
    await win.evaluate(() => handleCloseRequest());
    await win.waitForTimeout(400);
    expect(win.isClosed(), 'окно закрылось вопреки ответу «отмена»').toBe(false);
  } finally {
    await closeApp(app);
  }
});

test('LC3 страховочный таймер не уносит несохранённую работу на тяжёлом документе', async () => {
  // main даёт renderer пять секунд на ответ и закрывает окно сам. Слои сделали
  // renderer тяжелее: isDirty и handleCloseRequest ходят по стопке. Проверяем, что
  // ответ успевает прийти на большом документе с несколькими слоями.
  test.setTimeout(120000);
  const { app, win } = await launchApp();
  try {
    await win.evaluate(() => {
      document.getElementById('width-input').value = '3000';
      document.getElementById('height-input').value = '2000';
      resizeCanvas();
    });
    await win.waitForFunction(() => !state.restoring);
    await win.evaluate(() => { for (let i = 0; i < 4; i++) addLayer(); });
    await win.waitForFunction(() => !state.restoring);
    await drawSomething(win);

    await stubUnsavedAnswer(app, 2);     // «отмена»
    const t0 = Date.now();
    await win.evaluate(() => handleCloseRequest());
    await win.waitForTimeout(6000);      // пережидаем страховку
    const elapsed = Date.now() - t0;
    expect(win.isClosed(),
      'страховка закрыла окно, унеся несохранённое; прошло мс: ' + elapsed).toBe(false);
  } finally {
    await closeApp(app);
  }
});

// ─── Открытие файла ───
test('LD1 открытие не-картинки объясняется', async () => {
  const { app, win } = await launchApp();
  const dir = tempDir();
  try {
    const bogus = path.join(dir, 'это-не-картинка.png');
    fs.writeFileSync(bogus, 'просто текст, а не изображение');
    await stubOpenDialog(app, [bogus]);
    await captureErrorBoxes(app);

    await win.keyboard.press('Control+o');
    await win.waitForTimeout(1200);

    const alerts = await win.evaluate(() =>
      [...document.querySelectorAll('.gm-box .gm-text')].map(e => e.textContent));
    const boxes = await errorBoxes(app);
    expect(alerts.length + boxes.length,
      'битый файл открылся молча: ' + JSON.stringify({ alerts, boxes })).toBeGreaterThan(0);
  } finally {
    await closeApp(app);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('LD2 открытие настоящей картинки заменяет документ', async () => {
  const { app, win } = await launchApp();
  const dir = tempDir();
  try {
    // Готовим настоящий PNG руками приложения: сохраняем, потом открываем.
    const target = path.join(dir, 'исходник.png');
    await stubSaveDialog(app, target);
    await win.evaluate(() => {
      document.getElementById('width-input').value = '320';
      document.getElementById('height-input').value = '240';
      resizeCanvas();
    });
    await win.waitForFunction(() => !state.restoring);
    await win.evaluate(() => saveCanvas(true));
    await win.waitForTimeout(300);
    expect(fs.existsSync(target)).toBe(true);

    await win.evaluate(() => {
      document.getElementById('width-input').value = '900';
      document.getElementById('height-input').value = '600';
      resizeCanvas();
    });
    await win.waitForFunction(() => !state.restoring);

    await stubOpenDialog(app, [target]);
    win.on('dialog', (d) => d.accept().catch(() => {}));
    await win.keyboard.press('Control+o');
    await win.waitForFunction(() => canvas.width === 320, { timeout: 20000 });
    expect(await win.evaluate(() => [canvas.width, canvas.height])).toEqual([320, 240]);
  } finally {
    await closeApp(app);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
