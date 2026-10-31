import { chromium } from "playwright";

const pause = 2500;

const browser = await chromium.launch();
const context = await browser.newContext({
  baseURL: "http://proxy:8080",
  viewport: { width: 1280, height: 800 },
  recordVideo: { dir: "/out/video", size: { width: 1280, height: 800 } },
});
const page = await context.newPage();
const video = page.video();

await page.goto("/");
const send = page.getByRole("button", { name: "Enviar" });
await send.waitFor();
await page.locator("input[type=file]").setInputFiles("/data/demo.csv");
await page.waitForTimeout(pause);
await send.click();

await page.waitForURL(/\/imports\//, { timeout: 60_000 });
await page.getByText("concluída com erros").waitFor({ timeout: 120_000 });
await page.waitForTimeout(pause);

await page.getByRole("button", { name: "Linhas rejeitadas" }).click();
await page.waitForTimeout(pause);
await page.getByRole("button", { name: "Transações" }).click();
await page.waitForTimeout(pause);

await page.getByRole("link", { name: "← Importações" }).click();
await page.getByRole("button", { name: "Enviar" }).waitFor();
await page.waitForTimeout(pause);

await context.close();
await video.saveAs("/out/demo.webm");
await browser.close();
