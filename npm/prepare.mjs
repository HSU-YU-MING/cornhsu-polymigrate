// 把 dotnet publish 的產物組裝成可發布的 npm 套件。
//
// 用法:node npm/prepare.mjs <version> <publishRoot>
//   version     發布版號(取自 git tag,例:1.1.0)
//   publishRoot 內含各 RID 子目錄的資料夾,例:artifacts/publish/win-x64/…
//
// 產出:npm/dist/ 底下五個可直接 `npm publish` 的資料夾
//   cornhsu-polymigrate/         主套件(啟動腳本)
//   polymigrate-win32-x64/ 等    平台套件(自帶執行環境的 .NET 產物)
//
// 只有 CLI 上 npm。PolyMigrate.Core 是給 .NET 開發者用的函式庫,
// 受眾本來就在 NuGet,不需要也不該發到 npm。

import { cp, mkdir, readFile, rm, writeFile, access } from "node:fs/promises";
import path from "node:path";

const [version, publishRoot] = process.argv.slice(2);
if (!version || !publishRoot) {
  console.error("用法:node npm/prepare.mjs <version> <publishRoot>");
  process.exit(1);
}

// RID → npm 平台套件的 os/cpu 與執行檔名
const TARGETS = [
  { rid: "win-x64", pkg: "polymigrate-win32-x64", os: "win32", cpu: "x64", bin: "polymigrate.exe" },
  { rid: "linux-x64", pkg: "polymigrate-linux-x64", os: "linux", cpu: "x64", bin: "polymigrate" },
  { rid: "osx-x64", pkg: "polymigrate-darwin-x64", os: "darwin", cpu: "x64", bin: "polymigrate" },
  { rid: "osx-arm64", pkg: "polymigrate-darwin-arm64", os: "darwin", cpu: "arm64", bin: "polymigrate" },
];

const root = path.resolve(import.meta.dirname, "..");
const distDir = path.join(root, "npm", "dist");
await rm(distDir, { recursive: true, force: true });
await mkdir(distDir, { recursive: true });

const exists = async (p) => access(p).then(() => true, () => false);

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

  await writeFile(
    path.join(out, "package.json"),
    JSON.stringify(
      {
        name: `@cornhsu/${t.pkg}`,
        version,
        description: `Cornhsu.PolyMigrate 的 ${t.os}-${t.cpu} 執行檔。請安裝 cornhsu-polymigrate,不要直接安裝這個套件。`,
        homepage: "https://cornhsu.com/polymigrate",
        repository: {
          type: "git",
          url: "git+https://github.com/HSU-YU-MING/cornhsu-polymigrate.git",
        },
        license: "MIT",
        author: "許彧銘 Hsu Yu-Ming (https://cornhsu.com/)",
        os: [t.os],
        cpu: [t.cpu],
        files: ["bin/"],
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

console.log(`✔ cornhsu-polymigrate(${built.length} 個平台,版本 ${version})`);
