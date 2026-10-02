// Generates the API reference (docs/api/*.md + docs/api/sidebar.json) from the
// /// XML doc comments of the public KenseiECS API.
//
//   1. Copies the package sources that make up the .NET build (KenseiECS/**/*.cs
//      minus Editor/ and Samples~/) to .vitepress/cache/api-src, wrapping indented
//      code-like runs in doc comments that have no <code> yet (e.g. the
//      ComponentPool layout diagram) in <code> so they keep their line breaks.
//      The comments themselves are well-formed: KenseiECS.csproj makes CS1570
//      (badly formed XML comment) an error.
//   2. Runs `dotnet tool restore` + `dotnet docfx metadata docfx.json` (Markdown
//      output into .vitepress/cache/api-raw). Unity-only code is behind
//      `#if UNITY_...` and debug-only code behind `#if KENSEI_DEBUG`; neither symbol
//      is defined, so the reference documents the release .NET API.
//   3. Post-processes the Markdown so VitePress/Vue can compile it (escapes `<`,
//      `{{` outside code), writes docs/api/<Type>.md and docs/api/sidebar.json.
//
// docs/api/index.md is hand-written and kept; everything else in docs/api is
// generated and gitignored.
// Usage: node scripts/gen-api.mjs   (from docs/; needs the .NET SDK)
import { execFileSync } from 'node:child_process'
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync, existsSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const docsDir = join(dirname(fileURLToPath(import.meta.url)), '..')
const repoDir = join(docsDir, '..')
const pkgDir = join(repoDir, 'KenseiECS')
const srcOut = join(docsDir, '.vitepress', 'cache', 'api-src')
const rawOut = join(docsDir, '.vitepress', 'cache', 'api-raw')
const apiDir = join(docsDir, 'api')
const KEEP = new Set(['index.md'])

// ---------------------------------------------------------------- 1. sources

const EXCLUDED_DIRS = new Set(['Editor', 'Samples~', 'bin', 'obj'])

function* walk(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!EXCLUDED_DIRS.has(entry.name)) yield* walk(join(dir, entry.name))
    } else if (entry.name.endsWith('.cs')) yield join(dir, entry.name)
  }
}

// One `///` block: wrap runs of lines indented deeper than the block's base
// indentation that look like code in <code>, unless the block already uses
// <code>/<example>.
function fixDocBlock(lines) {
  const prefix = lines.map((l) => l.match(/^(\s*\/\/\/ ?)/)[1])
  const body = lines.map((l, i) => l.slice(prefix[i].length))
  const joined = body.join('\n')
  if (!/<code>|<example>/.test(joined)) {
    const indents = body.filter((b) => b.trim() && !b.trim().startsWith('<')).map((b) => b.match(/^ */)[0].length)
    const base = indents.length ? Math.min(...indents) : 0
    const deep = (b) => b.trim() !== '' && b.match(/^ */)[0].length > base
    for (let i = 0; i < body.length; i++) {
      if (!deep(body[i])) continue
      let j = i
      while (j + 1 < body.length && (deep(body[j + 1]) || (body[j + 1].trim() === '' && j + 2 < body.length && deep(body[j + 2])))) j++
      // Indented prose (e.g. SystemsRunner's "Lifecycle contract:") stays prose.
      if (!/[;{}=→]|\(\)/.test(body.slice(i, j + 1).join('\n'))) { i = j; continue }
      body[i] = '<code>' + body[i]
      body[j] = body[j] + '</code>'
      i = j
    }
  }
  return body.map((b, i) => prefix[i] + b)
}

function fixSource(text) {
  const lines = text.split('\n')
  const out = []
  for (let i = 0; i < lines.length; ) {
    if (/^\s*\/\/\//.test(lines[i])) {
      let j = i
      while (j < lines.length && /^\s*\/\/\//.test(lines[j])) j++
      out.push(...fixDocBlock(lines.slice(i, j)))
      i = j
    } else out.push(lines[i++])
  }
  return out.join('\n')
}

rmSync(srcOut, { recursive: true, force: true })
rmSync(rawOut, { recursive: true, force: true })
let sourceCount = 0
for (const file of walk(pkgDir)) {
  const dest = join(srcOut, relative(pkgDir, file))
  mkdirSync(dirname(dest), { recursive: true })
  writeFileSync(dest, fixSource(readFileSync(file, 'utf8')))
  sourceCount++
}
console.log(`gen-api: ${sourceCount} source files -> ${relative(docsDir, srcOut)}`)

// ---------------------------------------------------------------- 2. docfx

const run = (args) => execFileSync('dotnet', args, { cwd: docsDir, stdio: 'inherit' })
run(['tool', 'restore'])
run(['docfx', 'metadata', 'docfx.json'])

// ---------------------------------------------------------------- 3. Markdown

// Text outside code: `<` may only start the plain HTML tags docfx emits (anchors,
// links, <code class="paramref">, ...); anything else (`ComponentPool<T>`,
// `Span<int>`) would be an unclosed Vue element. `{{` would be an interpolation.
const HTML_TAG = /<(?!\/?(?:a|code|pre|em|strong|b|i|br|p|ul|ol|li|sup|sub|span|table|thead|tbody|tr|th|td)(?=[\s>\/]))/g
function escapeText(s) {
  return s
    .replace(HTML_TAG, '&lt;')
    .replace(/\{\{/g, '&#123;&#123;')
    .replace(/\}\}/g, '&#125;&#125;')
}

function escapeOutsideInlineCode(line) {
  // Split on backtick runs; odd segments are code spans.
  const parts = line.split(/(`+[^`]*`+)/)
  return parts.map((p, i) => (i % 2 ? p : escapeText(p))).join('')
}

const decode = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&')

// docfx renders <code> blocks as raw <pre><code> HTML; turn them into fenced
// blocks (highlighted by Shiki, ignored by Vue). The compiler trims the first
// line of a <code> block, so the common indent is taken from the other lines.
// Diagrams (arrows, no statements or assignments) are fenced without a language.
const isDiagram = (text) => text.includes('→') && !/[;{}=]/.test(text)
function fenceCodeBlocks(md) {
  return md.replace(/<pre><code(?: class="lang-([\w-]+)")?>([\s\S]*?)<\/code><\/pre>/g, (_, lang, code) => {
    const lines = decode(code).replace(/^\s*\n/, '').replace(/\s+$/, '').split('\n')
    if (isDiagram(lines.join('\n'))) lang = 'text'
    const rest = lines.slice(1).filter((l) => l.trim())
    const cut = rest.length ? Math.min(...rest.map((l) => l.match(/^ */)[0].length)) : 0
    const body = [lines[0].trim(), ...lines.slice(1).map((l) => l.slice(Math.min(cut, l.match(/^ */)[0].length)))]
    return '\n```' + (lang || 'csharp') + '\n' + body.join('\n') + '\n```\n'
  })
}

function toVitePress(md) {
  md = fenceCodeBlocks(md)
  const out = []
  let fence = null
  for (const line of md.split('\n')) {
    const f = line.match(/^\s*(`{3,}|~{3,})/)
    if (fence) {
      if (f && line.trim().startsWith(fence)) fence = null
      out.push(line)
    } else if (f) {
      fence = f[1]
      out.push(line)
    } else out.push(escapeOutsideInlineCode(line))
  }
  return out.join('\n')
}

for (const name of existsSync(apiDir) ? readdirSync(apiDir) : []) {
  if (!KEEP.has(name)) rmSync(join(apiDir, name), { recursive: true, force: true })
}
mkdirSync(apiDir, { recursive: true })

const pages = readdirSync(rawOut).filter((n) => n.endsWith('.md'))

// docfx leaves some <see cref> targets as <xref href="UID"> tags instead of links.
// Resolve them through the heading anchors docfx writes (`<a id="...">`, the UID
// with every non-alphanumeric character replaced by `_`) to code-formatted links.
const anchors = new Map()
for (const name of pages) {
  for (const line of readFileSync(join(rawOut, name), 'utf8').split('\n')) {
    const m = line.match(/^(#+) <a id="([^"]+)"><\/a> (.*)$/)
    if (!m) continue
    let title = m[3].replace(/\\/g, '')
    if (m[1] === '#') title = title.replace(/^\S+ /, '') // "Struct EventBuffer<T>" -> "EventBuffer<T>"
    else title = title.replace(/\(.*$/, '()').replace(/^.*\./, '') // "Write(BinaryWriter, ref T)" -> "Write()"
    anchors.set(m[2], { href: m[1] === '#' ? name : `${name}#${m[2]}`, title })
  }
}
function resolveXrefs(md, page) {
  return md.replace(/<xref href="([^"]+)"[^>]*><\/xref>/g, (_, href) => {
    const uid = decodeURIComponent(href)
    const target = anchors.get(uid.replace(/[^A-Za-z0-9]/g, '_'))
    if (!target) {
      console.warn(`gen-api: unresolved cref ${uid} in ${page}`)
      return '`' + uid.replace(/\(.*$/, '').replace(/`\d+/g, '').split('.').pop() + '`'
    }
    return `[\`${target.title}\`](${target.href})`
  })
}

for (const name of pages) {
  const md = resolveXrefs(readFileSync(join(rawOut, name), 'utf8'), name)
  const front = '---\neditLink: false\nlastUpdated: false\n---\n\n'
  writeFileSync(join(apiDir, name), front + toVitePress(md))
}

// toc.yml -> sidebar.json. docfx writes a flat list per namespace where entries
// without href ("Classes", "Structs", ...) start a group.
const toc = readFileSync(join(rawOut, 'toc.yml'), 'utf8').split('\n')
const sidebar = [{ text: 'API Reference', items: [{ text: 'Overview', link: '/api/' }] }]
let ns = null
let group = null
let pending = null
const flush = () => {
  if (!pending) return
  const link = pending.href ? '/api/' + pending.href.replace(/\.md$/, '') : null
  if (pending.depth === 0) {
    ns = { text: pending.name, link, items: [] }
    sidebar[0].items.push({ text: `Namespace ${pending.name}`, link })
  } else if (!link) {
    group = { text: pending.name, collapsed: false, items: [] }
    sidebar.push(group)
  } else (group ?? ns).items.push({ text: pending.name.replace(/</g, '&lt;').replace(/>/g, '&gt;'), link })
  pending = null
}
for (const line of toc) {
  const m = line.match(/^(\s*)- name: (.*)$/)
  if (m) {
    flush()
    pending = { name: m[2].replace(/^(['"])(.*)\1$/, '$2'), depth: m[1].length === 0 ? 0 : 1 }
    continue
  }
  const h = line.match(/^\s*href: (.*)$/)
  if (h && pending) pending.href = h[1].trim()
}
flush()
// Several namespaces would interleave groups; this package has one (KenseiECS).
writeFileSync(join(apiDir, 'sidebar.json'), JSON.stringify(sidebar, null, 2) + '\n')

console.log(`gen-api: ${pages.length} pages + sidebar.json -> ${relative(docsDir, apiDir)}`)
