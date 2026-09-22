// Проверки Electron-версии гоняются в Chromium по файловому адресу: приложение это одна
// страница, и всё, что делает пользователь, воспроизводится обычной мышью по холсту.
// Настоящий Electron для этого не нужен - мост в него подменяется заглушками (tests/harness.js).
const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './tests',
  testIgnore: ['**/tests-app/**'],
  fullyParallel: true,
  // На раннере GitHub (переменная CI) ядер меньше, а Chromium на каждого воркера свой:
  // при четырёх воркерах тесты с десятками мазков упирались в 30 с, хотя локально с запасом
  // проходят. Там воркеров вдвое меньше, а запас по времени втрое больше.
  // Поведение от этого не меняется - только терпение.
  workers: process.env.CI ? 2 : 4,
  timeout: process.env.CI ? 90_000 : 30_000,
  reporter: [['list']],
  use: {
    // Холст 900x600 должен помещаться целиком: проверки водят мышью по его точкам.
    viewport: { width: 1600, height: 1000 },
    launchOptions: { args: ['--allow-file-access-from-files'] },
  },
});
