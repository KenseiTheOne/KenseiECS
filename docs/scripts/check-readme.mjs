// Fails unless every C# block (```csharp / ```cs / ```c#, any case, ``` or ~~~ fences)
// in the repository README and the UPM package README equals (after trimming
// trailing whitespace and removing common indentation) the content of some
// `// #region` in docs/snippets/**/*.cs — so README code is always compiled code.
// Usage: node scripts/check-readme.mjs   (from docs/)
import { readFileSync, readdirSync } from 'node:fs'
import { join, relative, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const docsDir = join(dirname(fileURLToPath(import.meta.url)), '..')
const repoDir = join(docsDir, '..')
const readmePaths = [join(repoDir, 'README.md'), join(repoDir, 'KenseiECS', 'README.md')]
const snippetsDir = join(docsDir, 'snippets')

function normalize(lines) {
  lines = lines.map((l) => l.replace(/\s+$/, ''))
  while (lines.length && lines[0] === '') lines.shift()
  while (lines.length && lines[lines.length - 1] === '') lines.pop()
  const indents = lines.filter((l) => l !== '').map((l) => l.match(/^[ \t]*/)[0].length)
  const cut = indents.length ? Math.min(...indents) : 0
  return lines.map((l) => l.slice(cut)).join('\n')
}

function* walk(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === 'artifacts') continue
    const p = join(dir, entry.name)
    if (entry.isDirectory()) yield* walk(p)
    else if (entry.name.endsWith('.cs')) yield p
  }
}

// Collect regions: `// #region name` ... `// #endregion [name]`, nesting allowed.
const regions = new Map() // normalized text -> "file#name"
for (const file of walk(snippetsDir)) {
  const lines = readFileSync(file, 'utf8').replace(/\r\n/g, '\n').split('\n')
  const stack = []
  lines.forEach((line, i) => {
    const open = line.match(/^\s*\/\/\s*#region\s+(\S+)/)
    const close = line.match(/^\s*\/\/\s*#endregion\b/)
    if (open) stack.push({ name: open[1], start: i + 1 })
    else if (close && stack.length) {
      const { name, start } = stack.pop()
      const body = lines.slice(start, i).filter((l) => !/^\s*\/\/\s*#(end)?region\b/.test(l))
      regions.set(normalize(body), `${relative(docsDir, file)}#${name}`)
    }
  })
}

// Opening fence: 3+ backticks or tildes, optional spaces, a C# language tag in any case.
// The closing fence is the same run of fence characters (or longer) at the same indent.
const blockRe = /^([ \t]*)(`{3,}|~{3,})[ \t]*(?:csharp|cs|c#)(?![\w#])[^\n]*\n([\s\S]*?)^\1\2\2*[ \t]*$/gim
let count = 0
const failures = []
for (const readmePath of readmePaths) {
  const name = relative(repoDir, readmePath)
  const readme = readFileSync(readmePath, 'utf8').replace(/\r\n/g, '\n')
  for (const m of readme.matchAll(blockRe)) {
    count++
    const line = readme.slice(0, m.index).split('\n').length
    const text = normalize(m[3].replace(/\n$/, '').split('\n'))
    const hit = regions.get(text)
    if (hit) console.log(`ok  ${name}:${line} = ${hit}`)
    else failures.push(`${name}:${line}`)
  }
}

if (failures.length) {
  for (const where of failures)
    console.error(`${where}: csharp block does not match any // #region in docs/snippets/**/*.cs`)
  console.error(`\n${failures.length} of ${count} README code block(s) are not compiled snippets. ` +
    'Copy the block verbatim from a snippet region (or add a region) so it is built in CI.')
  process.exit(1)
}
console.log(`check:readme passed — ${count} csharp block(s), ${regions.size} region(s) scanned.`)
