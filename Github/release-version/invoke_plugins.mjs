// Only bridge the two pinned plugins. History, policy and versions belong to Python.
import { readFileSync } from "node:fs";
import { analyzeCommits } from "@semantic-release/commit-analyzer";
import { generateNotes } from "@semantic-release/release-notes-generator";

const request = JSON.parse(readFileSync(0, "utf8"));
const config = request.config;
for (const name of ["headerPattern", "breakingHeaderPattern"]) {
  config.parserOpts[name] = new RegExp(config.parserOpts[name]);
}
const context = { ...request.context, cwd: process.cwd(), logger: { log() {}, error() {} } };
const value = request.operation === "analyze"
  ? await analyzeCommits(config, context)
  : request.operation === "notes"
    ? await generateNotes(config, context)
    : (() => { throw new Error("unknown plugin operation"); })();
process.stdout.write(JSON.stringify(value));
