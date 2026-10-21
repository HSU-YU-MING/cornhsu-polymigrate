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
// ⚠ 這份清單要與 npm/prepare.mjs 的 TARGETS 一致。
// 兩邊漂開的話症狀很隱蔽:平台包會被 npm 正確裝好,啟動時卻說「沒有預先建置版本」。
// (2.1.0 加了 win32-arm64 / linux-arm64,但漏改這裡,2.1.0~2.3.0 的 arm64 使用者都中。)
const TARGETS = {
  "win32 x64": { pkg: "@cornhsu/polymigrate-win32-x64", bin: "polymigrate.exe" },
  "win32 arm64": { pkg: "@cornhsu/polymigrate-win32-arm64", bin: "polymigrate.exe" },
  "linux x64": { pkg: "@cornhsu/polymigrate-linux-x64", bin: "polymigrate" },
  "linux arm64": { pkg: "@cornhsu/polymigrate-linux-arm64", bin: "polymigrate" },
  "darwin x64": { pkg: "@cornhsu/polymigrate-darwin-x64", bin: "polymigrate" },
  "darwin arm64": { pkg: "@cornhsu/polymigrate-darwin-arm64", bin: "polymigrate" },
};

const key = `${process.platform} ${process.arch}`;
const target = TARGETS[key];

// 啟動腳本自己失敗 = PolyMigrate 根本沒有執行。exit code 目前是 1,和 CLI 契約的「1 = 只有 warning」
// 撞號(依「0/1 放行」的 CI 會把它當成通過);改成 2 屬既有情境的 exit code 改變,待維護者裁定。
// 在那之前至少把話講清楚:訊息用英文(與 CLI 其他輸出一致),明講工具沒有執行、這個 1 不是 warning。
const LAUNCHER_FAILURE_EXIT = 1;

function fail(...lines) {
  console.error(
    [
      `error: ${lines[0]} PolyMigrate did not run.`,
      ...lines.slice(1),
      "(This exit code 1 comes from the npm launcher, not a PolyMigrate warning.)",
    ].join("\n")
  );
  process.exit(LAUNCHER_FAILURE_EXIT);
}

if (!target) {
  fail(
    `no prebuilt binary for ${key}.`,
    `Supported platforms: ${Object.keys(TARGETS).join(", ")}.`,
    "On other platforms, use the .NET tool instead: dotnet tool install -g Cornhsu.PolyMigrate"
  );
}

let binary;
try {
  // 從子套件的 package.json 反推安裝位置,不用猜 node_modules 的層級
  // (pnpm / yarn 的實體結構與 npm 不同,require.resolve 才可靠)
  const manifest = require.resolve(`${target.pkg}/package.json`);
  binary = path.join(path.dirname(manifest), "bin", target.bin);
} catch {
  fail(
    `the platform package ${target.pkg} is not installed.`,
    "It is an optional dependency: --no-optional / --omit=optional skips it, or the install was interrupted.",
    "Reinstall: npm install cornhsu-polymigrate"
  );
}

const result = spawnSync(binary, process.argv.slice(2), { stdio: "inherit" });

if (result.error) {
  fail(`could not start ${binary}: ${result.error.message}.`);
}

// 被訊號中斷時 status 會是 null;轉成慣例的 128+signal,別讓 CI 誤判為成功
if (result.status === null && result.signal) {
  process.exit(128 + (require("node:os").constants.signals[result.signal] ?? 0));
}

process.exit(result.status ?? 0);
