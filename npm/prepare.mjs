// 把 dotnet publish 的產物組裝成可發布的 npm 套件。
//
// 用法:node npm/prepare.mjs <version> <publishRoot>
//   version     發布版號(取自 git tag,例:1.1.0)
//   publishRoot 內含各 RID 子目錄的資料夾,例:artifacts/publish/win-x64/…
//
// 產出:npm/dist/ 底下七個可直接 `npm publish` 的資料夾(6 個平台包 + 1 個主套件)
//   cornhsu-polymigrate/         主套件(啟動腳本)
//   polymigrate-win32-x64/ 等    平台套件(自帶執行環境的 .NET 產物)
//
// 只有 CLI 上 npm。PolyMigrate.Core 是給 .NET 開發者用的函式庫,
// 受眾本來就在 NuGet,不需要也不該發到 npm。

import { cp, mkdir, readFile, rm, writeFile, access } from "node:fs/promises";
import os from "node:os";
import path from "node:path";

const [version, publishRoot] = process.argv.slice(2);
if (!version || !publishRoot) {
  console.error("用法:node npm/prepare.mjs <version> <publishRoot>");
  process.exit(1);
}

// RID → npm 平台套件的 os/cpu 與執行檔名
const TARGETS = [
  { rid: "win-x64", pkg: "polymigrate-win32-x64", os: "win32", cpu: "x64", bin: "polymigrate.exe" },
  { rid: "win-arm64", pkg: "polymigrate-win32-arm64", os: "win32", cpu: "arm64", bin: "polymigrate.exe" },
  { rid: "linux-x64", pkg: "polymigrate-linux-x64", os: "linux", cpu: "x64", bin: "polymigrate" },
  { rid: "linux-arm64", pkg: "polymigrate-linux-arm64", os: "linux", cpu: "arm64", bin: "polymigrate" },
  { rid: "osx-x64", pkg: "polymigrate-darwin-x64", os: "darwin", cpu: "x64", bin: "polymigrate" },
  { rid: "osx-arm64", pkg: "polymigrate-darwin-arm64", os: "darwin", cpu: "arm64", bin: "polymigrate" },
];

const root = path.resolve(import.meta.dirname, "..");
const distDir = path.join(root, "npm", "dist");
await rm(distDir, { recursive: true, force: true });
await mkdir(distDir, { recursive: true });

const exists = async (p) => access(p).then(() => true, () => false);

// ── 第三方授權聲明 ──
// NuGet 通路不需要這一段:相依由 NuGet 解析,每個套件的授權隨它自己走。
// npm 不一樣——這裡出的是 self-contained 執行檔,Magick.Native-Q8-* 是「夾在裡面」
// 一起散布的,而 Magick.NET 為 Apache-2.0 且其套件內含 Notice.txt,§4(d) 要求
// 該通知隨附於再散布。
//
// 找不到就中止建置,不是印個警告帶過。原本這是 RELEASING.md 上的一條人工檢查項,
// 而人工檢查項遲早會被忘記——「安靜地沒做到」正是把它自動化要消滅的失敗方式。
async function readMagickNotice() {
  const assetsPath = path.join(root, "src", "PolyMigrate.Core", "obj", "project.assets.json");
  if (!(await exists(assetsPath))) {
    throw new Error(`找不到 ${assetsPath} — 請先 dotnet publish(還原後才有解析出的相依版本)`);
  }
  const assets = JSON.parse(await readFile(assetsPath, "utf8"));
  // 版本取自實際還原結果,不寫死:相依一升版,寫死的路徑就會安靜地失效
  const key = Object.keys(assets.libraries ?? {}).find(
    (k) => k.toLowerCase().startsWith("magick.net-q8-anycpu/")
  );
  if (!key) {
    throw new Error(
      "project.assets.json 裡沒有 Magick.NET-Q8-AnyCPU。若影像相依換掉了," +
        "請同步更新 THIRD-PARTY-NOTICES.md 與這段程式,別直接把它拿掉"
    );
  }
  const [id, version] = key.split("/");
  const cache = process.env.NUGET_PACKAGES || path.join(os.homedir(), ".nuget", "packages");
  const notice = path.join(cache, id.toLowerCase(), version.toLowerCase(), "Notice.txt");
  if (!(await exists(notice))) {
    throw new Error(`找不到 ${notice} — Apache-2.0 §4(d) 要求隨附此通知,不能略過`);
  }
  return { text: await readFile(notice, "utf8"), version };
}

const magick = await readMagickNotice();
console.log(`✔ Magick.NET ${magick.version} 的 Notice.txt`);

// ── 平台套件 ──
const built = [];
for (const t of TARGETS) {
  const src = path.join(publishRoot, t.rid);
  if (!(await exists(src))) {
    console.warn(`⚠ 跳過 ${t.rid}:找不到 ${src}`);
    continue;
  }

  const out = path.join(distDir, t.pkg);
  await cp(src, path.join(out, "bin"), { recursive: true });

  // 帶著原生檔散布的是平台套件,所以通知要放在這裡,不是只放主套件。
  // LICENSE 與 README 由 npm 自動收錄,NOTICE.txt 不會 → 必須列進 files。
  await writeFile(path.join(out, "NOTICE.txt"), magick.text);
  await cp(path.join(root, "LICENSE"), path.join(out, "LICENSE"));

  await writeFile(
    path.join(out, "package.json"),
    JSON.stringify(
      {
        name: `@cornhsu/${t.pkg}`,
        version,
        description: `The ${t.os}-${t.cpu} binary for Cornhsu.PolyMigrate. Install cornhsu-polymigrate instead — do not depend on this package directly.`,
        homepage: "https://cornhsu.com/polymigrate",
        repository: {
          type: "git",
          url: "git+https://github.com/HSU-YU-MING/cornhsu-polymigrate.git",
        },
        license: "MIT",
        author: "許彧銘 Hsu Yu-Ming (https://cornhsu.com/)",
        os: [t.os],
        cpu: [t.cpu],
        files: ["bin/", "NOTICE.txt"],
      },
      null,
      2
    ) + "\n"
  );

  built.push(t);
  console.log(`✔ ${t.pkg}`);
}

if (built.length === 0) {
  console.error("沒有任何平台產物,中止");
  process.exit(1);
}

// ── 主套件 ──
const mainSrc = path.join(root, "npm", "cornhsu-polymigrate");
const mainOut = path.join(distDir, "cornhsu-polymigrate");
await cp(mainSrc, mainOut, { recursive: true });

const manifest = JSON.parse(await readFile(path.join(mainOut, "package.json"), "utf8"));
manifest.version = version;
// 只列出這次真的有建出來的平台,避免安裝時去要一個不存在的版本
manifest.optionalDependencies = Object.fromEntries(
  built.map((t) => [`@cornhsu/${t.pkg}`, version])
);
await writeFile(path.join(mainOut, "package.json"), JSON.stringify(manifest, null, 2) + "\n");

await cp(path.join(root, "README.md"), path.join(mainOut, "README.md"));
// 使用者裝的是主套件,所以「有哪些第三方、各是什麼授權」要在這裡看得到;
// 原生檔的 Apache-2.0 通知則隨平台套件走(上面)。
await cp(path.join(root, "THIRD-PARTY-NOTICES.md"), path.join(mainOut, "THIRD-PARTY-NOTICES.md"));
await cp(path.join(root, "LICENSE"), path.join(mainOut, "LICENSE"));

console.log(`✔ cornhsu-polymigrate(${built.length} 個平台,版本 ${version})`);
