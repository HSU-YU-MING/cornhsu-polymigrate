#!/usr/bin/env node
// npm 通路的啟動腳本。
//
// 為什麼要有這一層(而不是直接把執行檔當 bin):
// npm 靠 optionalDependencies 的 os/cpu 只下載使用者當前平台的那一包,
// 但執行時仍需要解析出它實際被裝到哪裡。
//
// 為什麼要有 npm 通路:PolyMigrate 的使用者是「要把舊網站搬成靜態站的人」,
// 那些人多半在 Hugo / Eleventy / Astro / Next.js 的生態裡,手邊有 Node、
// 不一定有 .NET SDK。而且搬站是一次性任務 —— 為了用一次而裝一整套 SDK,
// 摩擦成本高到多數人會直接放棄。
//
// 刻意不做 postinstall 下載:那會被 `npm ci --ignore-scripts` 擋掉,
// 而不少公司的 CI 預設就是關腳本的。

"use strict";

const { spawnSync } = require("node:child_process");
const path = require("node:path");

// 平台 → 子套件名 / 執行檔名
const TARGETS = {
  "win32 x64": { pkg: "@cornhsu/polymigrate-win32-x64", bin: "polymigrate.exe" },
  "linux x64": { pkg: "@cornhsu/polymigrate-linux-x64", bin: "polymigrate" },
  "darwin x64": { pkg: "@cornhsu/polymigrate-darwin-x64", bin: "polymigrate" },
  "darwin arm64": { pkg: "@cornhsu/polymigrate-darwin-arm64", bin: "polymigrate" },
};

const key = `${process.platform} ${process.arch}`;
const target = TARGETS[key];

if (!target) {
  console.error(
    `Cornhsu.PolyMigrate 沒有 ${key} 的預先建置版本。\n` +
      `目前支援:${Object.keys(TARGETS).join("、")}\n` +
      `其他平台請改用 .NET 版本:dotnet tool install -g Cornhsu.PolyMigrate`
  );
  process.exit(1);
}

let binary;
try {
  // 從子套件的 package.json 反推安裝位置,不用猜 node_modules 的層級
  // (pnpm / yarn 的實體結構與 npm 不同,require.resolve 才可靠)
  const manifest = require.resolve(`${target.pkg}/package.json`);
  binary = path.join(path.dirname(manifest), "bin", target.bin);
} catch {
  console.error(
    `找不到平台套件 ${target.pkg}。\n` +
      `可能是安裝時加了 --no-optional,或安裝過程中斷。\n` +
      `請重新安裝:npm install cornhsu-polymigrate`
  );
  process.exit(1);
}

const result = spawnSync(binary, process.argv.slice(2), { stdio: "inherit" });

if (result.error) {
  console.error(`無法執行 ${binary}:${result.error.message}`);
  process.exit(1);
}

// 被訊號中斷時 status 會是 null;轉成慣例的 128+signal,別讓 CI 誤判為成功
if (result.status === null && result.signal) {
  process.exit(128 + (require("node:os").constants.signals[result.signal] ?? 0));
}

process.exit(result.status ?? 0);
