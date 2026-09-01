const { test, expect } = require('@playwright/test');
const { launchApp, closeApp } = require('./harness');

test('приложение запускается и показывает свою версию', async () => {
  const { app, win } = await launchApp();
  try {
    const version = await app.evaluate(async ({ app }) => app.getVersion());
    expect(version).toBe(require('../package.json').version);
    await expect(win.locator('#app-version')).toContainText(version);
    expect(await win.evaluate(() => [canvas.width, canvas.height])).toEqual([900, 600]);
  } finally {
    await closeApp(app);
  }
});
