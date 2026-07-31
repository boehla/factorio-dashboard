#!/usr/bin/env node
//
// Statische Pruefung des Mod-Lua-Codes.
//
// Factorio meldet Lua-Fehler erst, wenn der Code laeuft — bei einem Collector,
// der nur alle 60 Sekunden dran ist, kann das lange dauern. Diese drei
// Pruefungen fangen das ab, was ohne Spiel feststellbar ist:
//
//   1. Syntax        — parst jede Datei als Lua 5.2
//   2. Globals       — meldet gelesene Globals, die weder Lua-Stdlib noch
//                      Factorio-API sind (also fast immer Tippfehler)
//   3. Cross-Modul   — prueft, ob require()-te Module die benutzten Felder
//                      ueberhaupt exportieren
//
// Aufruf:  npm install && npm run check

const luaparse = require('luaparse');
const fs = require('fs');
const path = require('path');

const ALLOWED_GLOBALS = new Set([
  // Lua 5.2 Standardbibliothek
  'assert', 'error', 'ipairs', 'next', 'pairs', 'pcall', 'print', 'select',
  'tonumber', 'tostring', 'type', 'unpack', 'rawget', 'rawset', 'rawequal',
  'rawlen', 'setmetatable', 'getmetatable', 'xpcall', 'string', 'table',
  'math', 'os', 'bit32', '_G', 'require', 'load', 'loadstring',
  // Factorio Control-Stage
  'game', 'script', 'storage', 'defines', 'helpers', 'remote', 'commands',
  'settings', 'prototypes', 'rcon', 'log', 'serpent', 'rendering',
  'table_size', 'localised_print', 'mods',
]);

// `data` existiert nur in der Data-Stage. Sie nur dort zu erlauben haelt den
// Check fuer den Control-Stage-Code scharf: dort waere `data` ein Tippfehler.
const DATA_STAGE_FILES = new Set(['settings.lua', 'data.lua', 'data-updates.lua', 'data-final-fixes.lua']);
const DATA_STAGE_GLOBALS = new Set(['data']);

const root = path.resolve(process.argv[2] || '../fdash-exporter');
if (!fs.existsSync(root)) {
  console.error(`Mod directory not found: ${root}`);
  process.exit(2);
}

const files = [];
(function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(p);
    else if (entry.name.endsWith('.lua')) files.push(p);
  }
})(root);
files.sort();

const rel = (f) => path.relative(root, f).split(path.sep).join('/');
let problems = 0;

// --------------------------------------------------------------- 1. Syntax
const asts = new Map();
for (const f of files) {
  const code = fs.readFileSync(f, 'utf8');
  try {
    asts.set(f, { code, ast: luaparse.parse(code, { luaVersion: '5.2' }) });
  } catch (e) {
    console.error(`SYNTAX  ${rel(f)}: ${e.message}`);
    problems++;
  }
}
if (problems > 0) {
  console.error(`\n${problems} syntax error(s) — stopping here.`);
  process.exit(1);
}
console.log(`syntax: ${files.length} files OK`);

// -------------------------------------------------------------- 2. Globals
for (const f of files) {
  const seen = new Map();
  const allowed = DATA_STAGE_FILES.has(rel(f))
    ? new Set([...ALLOWED_GLOBALS, ...DATA_STAGE_GLOBALS])
    : ALLOWED_GLOBALS;
  luaparse.parse(asts.get(f).code, {
    luaVersion: '5.2', scope: true, locations: true,
    onCreateNode(node) {
      if (node.type === 'Identifier' && node.isLocal === false
          && !allowed.has(node.name) && !seen.has(node.name)) {
        seen.set(node.name, node.loc.start.line);
      }
    },
  });
  for (const [name, line] of seen) {
    console.error(`GLOBAL  ${rel(f)}:${line}: unexpected global '${name}'`);
    problems++;
  }
}
console.log('globals: OK');

// ---------------------------------------------------------- 3. Cross-Modul
// Exportierte Felder je Modul einsammeln. Bewusst textuell statt ueber den
// AST: die Muster sind im Mod einheitlich, und ein zu strenger Check waere
// hier laestiger als nuetzlich.
const moduleExports = new Map();
for (const f of files) {
  const modName = path.relative(root, f).split(path.sep).join('.').replace(/\.lua$/, '');
  const code = asts.get(f).code;
  const names = new Set();
  for (const m of code.matchAll(/^\s*function\s+(\w+)\.(\w+)\s*\(/gm)) names.add(m[2]);
  for (const m of code.matchAll(/^\s*(\w+)\.(\w+)\s*=/gm)) names.add(m[2]);
  for (const m of code.matchAll(/^local\s+\w+\s*=\s*\{([^}]*)\}/gm)) {
    for (const k of m[1].matchAll(/(\w+)\s*=/g)) names.add(k[1]);
  }
  moduleExports.set(modName, names);
}

for (const f of files) {
  const code = asts.get(f).code;
  // Kommentare entfernen, sonst schlagen Verweise wie "siehe registry.lua" an.
  const stripped = code.replace(/--\[\[[\s\S]*?\]\]/g, '').replace(/--.*$/gm, '');
  const aliases = new Map();
  for (const m of stripped.matchAll(/local\s+(\w+)\s*=\s*require\(["']([\w.]+)["']\)/g)) {
    aliases.set(m[1], m[2]);
  }
  for (const [alias, mod] of aliases) {
    if (!moduleExports.has(mod)) {
      console.error(`REQUIRE ${rel(f)}: require('${mod}') — no such module`);
      problems++;
      continue;
    }
    const exported = moduleExports.get(mod);
    // Nur echte Modulzugriffe: nicht Teil eines laengeren Pfads wie
    // defines.events.on_tick, wo 'events' zufaellig ein Alias-Name ist.
    const re = new RegExp('(^|[^\\w.])' + alias + '\\.(\\w+)', 'g');
    for (const m of stripped.matchAll(re)) {
      if (!exported.has(m[2])) {
        console.error(`FIELD   ${rel(f)}: ${alias}.${m[2]} not exported by ${mod}`);
        problems++;
      }
    }
  }
}
console.log('cross-module: OK');

console.log(problems === 0
  ? `\nall checks passed (${files.length} files)`
  : `\n${problems} problem(s)`);
process.exit(problems === 0 ? 0 : 1);
