/* Wrapper so `npm test` can run the Python round-trip. Skips cleanly (exit 0) if
   python3 is not available, so minimal CI never breaks on a missing interpreter. */
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const py = ["python3", "python"].find(bin => {
  const r = spawnSync(bin, ["--version"], { stdio: "ignore" });
  return r.status === 0;
});
if (!py) {
  console.log("save-roundtrip: python3 not found — skipping (this is fine).");
  process.exit(0);
}
const r = spawnSync(py, [join(here, "save_roundtrip.py")], { stdio: "inherit" });
process.exit(r.status || 0);
