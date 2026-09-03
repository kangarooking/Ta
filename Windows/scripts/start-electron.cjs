const { existsSync } = require("node:fs");
const { spawn } = require("node:child_process");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const executable = path.join(root, "node_modules", "electron", "dist", "electron.exe");

if (!existsSync(executable)) {
  throw new Error("Electron runtime is missing. Run `npm install` from the Windows directory and try again.");
}

const environment = { ...process.env };
delete environment.ELECTRON_RUN_AS_NODE;

const child = spawn(executable, [root], { cwd: root, env: environment, stdio: "inherit" });
child.on("error", (error) => { throw error; });
child.on("exit", (code) => { process.exitCode = code ?? 1; });
