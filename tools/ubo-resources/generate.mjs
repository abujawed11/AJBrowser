// Builds AJBrowser's scriptlet + redirect resources for the adblock-rust engine (Phase 8) from an official uBlock Origin
// release, in adblock-rust's resources.json format.
//
//   node tools/ubo-resources/generate.mjs <unpacked uBlock0.chromium folder> <output .json>
//
// Get the folder by unpacking uBlock0_<version>.chromium.zip from https://github.com/gorhill/uBlock/releases
// (check its SHA-256 against the release page). The output is uBO code (GPLv3), unmodified apart from being turned
// into strings; record the uBO version in THIRD-PARTY-NOTICES.md.
//
// What it produces:
// - every registered scriptlet (`##+js(name, args…)`) as a function-style resource: adblock-rust injects the
//   function and calls it with the rule's arguments. Helpers (names ending in ".fn") are "fn/javascript"
//   dependencies. `requiresTrust` scriptlets get permission bit 1 (TRUSTED_SCRIPTLETS), so only lists AJBrowser
//   trusts (uBO's own) can use them, exactly as in uBO.
// - every redirect file from web_accessible_resources (`$redirect=noop.js`…), with its aliases.

import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const TRUSTED_SCRIPTLETS = 1;

const [uboDir, outFile] = process.argv.slice(2);
if (!uboDir || !outFile) {
    console.error('usage: node generate.mjs <unpacked uBlock0.chromium folder> <output.json>');
    process.exit(2);
}

const manifest = JSON.parse(fs.readFileSync(path.join(uboDir, 'manifest.json'), 'utf8'));
const importFile = (relative) => import(pathToFileURL(path.join(uboDir, relative)).href);

const { builtinScriptlets } = await importFile('js/resources/scriptlets.js');
const { default: redirectMap } = await importFile('js/redirect-resources.js');

const base64 = (text) => Buffer.from(text, 'utf8').toString('base64');
const withJs = (name) => (name.endsWith('.js') || name.endsWith('.fn') ? name : `${name}.js`);

const resources = [];
const names = new Set();

for (const s of builtinScriptlets) {
    if (typeof s.fn !== 'function' || typeof s.name !== 'string') {
        throw new Error(`Unexpected scriptlet entry: ${JSON.stringify(s)}`);
    }
    const isHelper = s.name.endsWith('.fn');
    const source = s.fn.toString();
    if (isHelper) {
        // Helpers are pasted in as plain text before the calls, so any named declaration works
        // (uBO has plain functions, async functions and classes here).
        if (!/^(?:async\s+)?(?:function\s*\*?|class)\s+[\w$]+/.test(source)) {
            throw new Error(`Helper ${s.name} isn't a named function or class`);
        }
    } else if (!/^function\s+[\w$]+\s*\(/.test(source)) {
        // adblock-rust calls a scriptlet by the name it reads with ^function\s+(name)\s*\( .
        throw new Error(`Scriptlet ${s.name} isn't a plain named function`);
    }
    names.add(s.name);
    resources.push({
        name: s.name,
        aliases: (s.aliases ?? []).map(withJs),
        kind: { mime: isHelper ? 'fn/javascript' : 'application/javascript' },
        content: base64(source),
        dependencies: s.dependencies ?? [],
        ...(s.requiresTrust ? { permission: TRUSTED_SCRIPTLETS } : {}),
    });
}

for (const r of resources) {
    for (const dep of r.dependencies) {
        if (!names.has(dep)) {
            throw new Error(`${r.name} depends on unknown ${dep}`);
        }
    }
}

const mimeByExtension = {
    css: 'text/css', gif: 'image/gif', html: 'text/html', js: 'application/javascript', json: 'application/json',
    mp3: 'audio/mp3', mp4: 'video/mp4', png: 'image/png', txt: 'text/plain', xml: 'text/xml',
};
let redirects = 0;
let skipped = 0;
for (const [name, props] of redirectMap) {
    if (props.params !== undefined) {
        skipped++; // parameterised redirects: adblock-rust doesn't support them (same as its own assembler)
        continue;
    }
    const file = path.join(uboDir, 'web_accessible_resources', name);
    const mime = mimeByExtension[path.extname(name).slice(1)] ?? 'application/octet-stream';
    let bytes = fs.readFileSync(file);
    if (mime === 'application/javascript' || mime === 'text/html' || mime === 'text/plain') {
        bytes = Buffer.from(bytes.toString('utf8').replace(/\r/g, ''), 'utf8');
    }
    const alias = props.alias === undefined ? [] : Array.isArray(props.alias) ? props.alias : [props.alias];
    if (names.has(name)) {
        throw new Error(`Redirect ${name} clashes with a scriptlet name`);
    }
    resources.push({ name, aliases: alias, kind: { mime }, content: bytes.toString('base64') });
    redirects++;
}

fs.writeFileSync(outFile, JSON.stringify(resources));
const trusted = resources.filter((r) => r.permission).length;
const helpers = resources.filter((r) => r.kind.mime === 'fn/javascript').length;
// uBO runs a few scriptlets in the extension's isolated world; AJBrowser injects everything into the page's world.
const isolated = builtinScriptlets.filter((s) => s.world === 'ISOLATED').map((s) => s.name);
console.log(`uBlock Origin ${manifest.version}: ${resources.length - redirects - helpers} scriptlets ` +
    `(${trusted} trusted), ${helpers} helpers, ${redirects} redirects (${skipped} parameterised skipped) -> ` +
    `${outFile} (${fs.statSync(outFile).size} bytes)`);
console.log(`Isolated-world scriptlets in uBO (run in the page world here): ${isolated.join(', ') || 'none'}`);
